using System;
using System.Data.SQLite;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace SQLECmd;

/// <summary>
///     System.Data.SQLite needs the native SQLite.Interop library. SQLECmd ships the ones from the
///     Stub.System.Data.SQLite.Core.NetStandard package as files next to it, in runtimes\&lt;rid&gt;\native (the
///     package's own layout, which the build makes too); this makes System.Data.SQLite load that exact file for this
///     platform, wherever SQLECmd is run from.
/// </summary>
/// <remarks>
///     Nothing is extracted or written anywhere: the library is loaded from SQLECmd's folder (Eric, 2026-10-20: nothing
///     outside the program's folder). A build for one runtime (dotnet publish -r linux-x64) has it right next to
///     SQLECmd.dll instead; that is tried second.
/// </remarks>
internal static class NativeSqlite
{
    // The library name System.Data.SQLite imports (the same file name on every OS)
    internal const string InteropName = "SQLite.Interop.dll";

    // The platforms the package has a library for
    internal static readonly string[] Rids = { "win-x64", "win-x86", "linux-x64", "osx-x64" };

    private static IntPtr _handle;

    /// <summary>Full path of the loaded library, or null before <see cref="Setup" /></summary>
    public static string LoadedFrom { get; private set; }

    /// <summary>The runtime identifier of the library for this process, or null if none is shipped for it</summary>
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

    /// <summary>Where the library for <paramref name="rid" /> is looked for, in order</summary>
    internal static string[] Candidates(string rid)
    {
        var dir = AppContext.BaseDirectory;
        return new[]
        {
            Path.Combine(dir, "runtimes", rid, "native", InteropName),
            Path.Combine(dir, InteropName)
        };
    }

    /// <summary>Loads the library from SQLECmd's folder. Call before anything uses SQLite; later calls do nothing</summary>
    /// <exception cref="PlatformNotSupportedException">No library is shipped for this OS/architecture</exception>
    /// <exception cref="FileNotFoundException">The library is not in SQLECmd's folder</exception>
    public static void Setup()
    {
        if (LoadedFrom != null)
        {
            return;
        }

        var rid = CurrentRid();
        if (rid == null)
        {
            throw new PlatformNotSupportedException(
                $"No SQLite.Interop library is included for {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}). Supported: {string.Join(", ", Rids)}");
        }

        var candidates = Candidates(rid);
        var path = Array.Find(candidates, File.Exists);
        if (path == null)
        {
            throw new FileNotFoundException(
                $"{InteropName} for {rid} was not found next to SQLECmd (looked for {string.Join(" and ", candidates)}). Unzip the whole download, with its runtimes folder", candidates[0]);
        }

        // System.Data.SQLite's own pre-loader looks in x86\ and x64\ folders next to it: off, this file is the one
        Environment.SetEnvironmentVariable("No_PreLoadSQLite", "1");

        _handle = NativeLibrary.Load(path);

        NativeLibrary.SetDllImportResolver(typeof(SQLiteConnection).Assembly,
            (name, _, _) => string.Equals(name, InteropName, StringComparison.OrdinalIgnoreCase) ? _handle : IntPtr.Zero);

        LoadedFrom = path;

        Log.Debug("Loaded {Interop} ({Rid}) from {Path}", InteropName, rid, path);
    }
}
