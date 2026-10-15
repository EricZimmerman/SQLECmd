using System;
using System.Data.SQLite;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;

namespace SQLECmd;

/// <summary>
///     System.Data.SQLite needs the native SQLite.Interop library. SQLECmd embeds the ones from the
///     Stub.System.Data.SQLite.Core.NetStandard package; this extracts the one for the current platform and makes
///     System.Data.SQLite load that exact file, wherever SQLECmd is run from.
/// </summary>
/// <remarks>
///     The library is extracted once per version to a per-user cache (%LOCALAPPDATA%\SQLECmd on Windows,
///     ~/.local/share/SQLECmd elsewhere), in a folder named after its SHA-256, and reused while the hash matches.
///     Nothing is written to the working directory. If the cache cannot be used, a private temporary directory is used
///     instead and removed at exit where the OS allows it.
/// </remarks>
internal static class NativeSqlite
{
    // The library name System.Data.SQLite imports
    private const string InteropName = "SQLite.Interop.dll";

    private static IntPtr _handle;
    private static string _tempDirectory;

    /// <summary>Full path of the loaded library, or null before <see cref="Setup" /></summary>
    public static string LoadedFrom { get; private set; }

    /// <summary>The runtime identifier of the embedded library for this process, or null if none is embedded</summary>
    internal static string CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return arch switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                _ => null
            };
        }

        if (arch != Architecture.X64)
        {
            return null;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux-x64";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx-x64" : null;
    }

    internal static byte[] GetEmbedded(string rid)
    {
        using var res = typeof(NativeSqlite).Assembly.GetManifestResourceStream($"SQLite.Interop.{rid}");
        if (res == null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        res.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Extracts and loads the library. Call before anything uses SQLite; later calls do nothing</summary>
    /// <exception cref="PlatformNotSupportedException">No library is embedded for this OS/architecture</exception>
    public static void Setup()
    {
        if (LoadedFrom != null)
        {
            return;
        }

        var rid = CurrentRid();
        var bytes = rid == null ? null : GetEmbedded(rid);
        if (bytes == null)
        {
            throw new PlatformNotSupportedException(
                $"No SQLite.Interop library is included for {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}). Supported: win-x64, win-x86, linux-x64, osx-x64");
        }

        var hash = Sha256(bytes);

        var path = ExtractToCache(bytes, rid, hash) ?? ExtractToTemp(bytes, hash);

        _handle = NativeLibrary.Load(path);

        NativeLibrary.SetDllImportResolver(typeof(SQLiteConnection).Assembly,
            (name, _, _) => string.Equals(name, InteropName, StringComparison.OrdinalIgnoreCase) ? _handle : IntPtr.Zero);

        LoadedFrom = path;

        Log.Debug("Loaded {Interop} ({Rid}) from {Path}", InteropName, rid, path);
    }

    /// <summary>Removes the temporary directory, if one had to be used. The per-user cache is kept</summary>
    public static void Cleanup()
    {
        if (_tempDirectory == null)
        {
            return;
        }

        try
        {
            // Works where a loaded library can be deleted (Linux, macOS). On Windows it stays until the next run
            Directory.Delete(_tempDirectory, true);
            _tempDirectory = null;
        }
        catch (Exception e)
        {
            Log.Debug("Unable to delete {Dir}: {Message}", _tempDirectory, e.Message);
        }
    }

    private static string ExtractToCache(byte[] bytes, string rid, string hash)
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var dir = Path.Combine(root, "SQLECmd", $"{rid}-{hash.Substring(0, 16)}");
            var path = Path.Combine(dir, InteropName);

            if (File.Exists(path) && Sha256(File.ReadAllBytes(path)) == hash)
            {
                return path;
            }

            Directory.CreateDirectory(dir);

            // Write to a unique name then move it into place, so a concurrent run never sees a partial file
            var tmp = Path.Combine(dir, $"{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                File.Move(tmp, path, true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another run put it in place first and has it loaded
                File.Delete(tmp);
            }

            return Sha256(File.ReadAllBytes(path)) == hash ? path : null;
        }
        catch (Exception e)
        {
            Log.Debug("Unable to use the SQLite.Interop cache: {Message}", e.Message);
            return null;
        }
    }

    private static string ExtractToTemp(byte[] bytes, string hash)
    {
        // A new directory only this user can read or write (0700 on Linux and macOS)
        var dir = Directory.CreateTempSubdirectory("SQLECmd-").FullName;
        _tempDirectory = dir;

        var path = Path.Combine(dir, InteropName);
        File.WriteAllBytes(path, bytes);

        if (Sha256(File.ReadAllBytes(path)) != hash)
        {
            throw new IOException($"Extracted {path} does not match the embedded library");
        }

        return path;
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
