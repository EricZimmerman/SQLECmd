using System.Data.SQLite;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using SQLECmd;

namespace SQLMaps.Test;

/// <summary>
///     The SQLite.Interop libraries embedded in SQLECmd. The package copies its own under runtimes/ next to the tests
/// </summary>
[TestFixture]
[NonParallelizable]
public class NativeSqliteTests
{
    [TestCase("win-x64")]
    [TestCase("win-x86")]
    [TestCase("linux-x64")]
    [TestCase("osx-x64")]
    public void EmbeddedLibraryIsThePackageOne(string rid)
    {
        var fromPackage = Path.Combine(TestContext.CurrentContext.TestDirectory, "runtimes", rid, "native",
            "SQLite.Interop.dll");

        NativeSqlite.GetEmbedded(rid).Should().Equal(File.ReadAllBytes(fromPackage));
    }

    [Test]
    public void LoadsAndWorks()
    {
        NativeSqlite.CurrentRid().Should().NotBeNull("the tests run on a supported platform");

        NativeSqlite.Setup();
        NativeSqlite.Setup(); // a second call does nothing

        File.Exists(NativeSqlite.LoadedFrom).Should().BeTrue();
        File.ReadAllBytes(NativeSqlite.LoadedFrom).Should().Equal(NativeSqlite.GetEmbedded(NativeSqlite.CurrentRid()));

        using var connection = new SQLiteConnection("Data Source=:memory:");
        connection.Open();
        using var command = new SQLiteCommand("SELECT sqlite_version()", connection);
        command.ExecuteScalar().Should().Be(connection.ServerVersion);
        SQLiteConnection.InteropVersion.Should().Be(SQLiteConnection.ProviderVersion,
            "the native library must be the version System.Data.SQLite expects");
    }
}
