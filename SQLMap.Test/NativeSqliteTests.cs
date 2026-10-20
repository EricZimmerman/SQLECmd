using System;
using System.Data.SQLite;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using SQLECmd;

namespace SQLMaps.Test;

/// <summary>
///     The SQLite.Interop libraries next to SQLECmd. The package copies them under runtimes\&lt;rid&gt;\native next to
///     the tests, as the release has them next to SQLECmd.dll
/// </summary>
[TestFixture]
[NonParallelizable]
public class NativeSqliteTests
{
    [Test]
    public void EveryPlatformHasItsLibrary()
    {
        foreach (var rid in NativeSqlite.Rids)
        {
            File.Exists(NativeSqlite.Candidates(rid)[0]).Should().BeTrue($"{rid} is supported");
        }
    }

    [Test]
    public void LoadsFromTheProgramFolderAndWorks()
    {
        var rid = NativeSqlite.CurrentRid();
        rid.Should().NotBeNull("the tests run on a supported platform");

        NativeSqlite.Setup();
        NativeSqlite.Setup(); // a second call does nothing

        // the file in the program's folder, never a copy elsewhere
        NativeSqlite.LoadedFrom.Should().Be(Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "SQLite.Interop.dll"));

        using var connection = new SQLiteConnection("Data Source=:memory:");
        connection.Open();
        using var command = new SQLiteCommand("SELECT sqlite_version()", connection);
        command.ExecuteScalar().Should().Be(connection.ServerVersion);
        SQLiteConnection.InteropVersion.Should().Be(SQLiteConnection.ProviderVersion,
            "the native library must be the version System.Data.SQLite expects");
    }
}
