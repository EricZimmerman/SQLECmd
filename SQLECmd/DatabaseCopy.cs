using System;
using System.IO;
using Serilog;

namespace SQLECmd;

/// <summary>
///     A database copied (with its -wal and -journal) to a new folder in the temporary folder (--temp, or Temp next to
///     SQLECmd: <see cref="TempFolder" />), deleted when disposed. Only the copy is opened: SQLite opens databases
///     read-write and would checkpoint a -wal into the evidence and delete the -wal and -shm files
/// </summary>
internal sealed class DatabaseCopy : IDisposable
{
    private readonly string _folder;

    private DatabaseCopy(string folder, string path)
    {
        _folder = folder;
        Path = path;
    }

    /// <summary>The copy of the database</summary>
    public string Path { get; }

    public static DatabaseCopy Create(string database, string tempFolder)
    {
        var folder = System.IO.Path.Combine(tempFolder, "SQLECmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var copy = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(database));
            foreach (var suffix in new[] { "", "-wal", "-journal" })
            {
                if (File.Exists(database + suffix))
                {
                    CopyShared(database + suffix, copy + suffix);
                }
            }

            return new DatabaseCopy(folder, copy);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    public void Dispose()
    {
        if (TryDelete(_folder))
        {
            return;
        }

        //System.Data.SQLite closes a file only when its last statement is finalized: one not disposed keeps the copy
        //open (Windows then can't remove the folder) until the garbage collector finalizes it
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (TryDelete(_folder) == false)
        {
            Log.Warning("Could not delete the temporary copy {Folder}", _folder);
        }
    }

    /// <summary>Databases are often in use on a live system</summary>
    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        input.CopyTo(output);
    }

    private static bool TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            return true;
        }
        catch (Exception e)
        {
            Log.Debug("Could not delete {Folder}: {Message}", folder, e.Message);
            return false;
        }
    }
}
