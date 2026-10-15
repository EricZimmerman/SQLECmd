using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SQLMaps.Test;

/// <summary>
///     Runs the built SQLECmd.dll (copied next to the tests by the project reference) as a separate process, from an
///     empty working directory. It uses the Maps folder next to it, which has the TestFiles_*.smap maps
/// </summary>
[TestFixture]
public class CliTests
{
    private const string ContactsId = "83b99299-2d84-4844-af25-c727d3440b19";
    private const string CarsId = "af5bcf3f-b316-4e49-8d12-ce9e180b3914";

    private string _dir;
    private string _cwd;
    private string Csv => Path.Combine(_dir, "csv");

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sqlecmd-test-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_dir, "cwd");
        Directory.CreateDirectory(_cwd);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_dir, true);
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _cwd
        };
        start.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "SQLECmd.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        process.WaitForExit(120_000).Should().BeTrue("SQLECmd should finish");

        // SQLECmd must not leave anything (such as SQLite.Interop.dll) in the working directory
        Directory.GetFileSystemEntries(_cwd).Should().BeEmpty();

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private string[] CsvLines(string suffix)
    {
        var file = Directory.GetFiles(Csv, "*" + suffix).Should().ContainSingle().Subject;
        return File.ReadAllLines(file);
    }

    [Test]
    public void ContactsByFileName()
    {
        var db = SqLiteFileTests.TestFile("Contacts.db");

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(0, output);
        output.Should().Contain("Map queries found: 4");

        var users = CsvLines($"_Contacts_Users_{ContactsId}.csv");
        users.Should().HaveCount(21);
        users[0].Should().Be("FirstName,LastName,SourceFile");
        users[1].Should().Be($"John,Doe,{db}");

        var join = CsvLines($"_Contacts_JoinExample_{ContactsId}.csv");
        join.Should().HaveCount(37);
        join[0].Should().StartWith("ID,CustomerName,Year,January,");

        CsvLines($"_Contacts_StateIdInfo_{ContactsId}.csv")[0].Should().Be("ID,Wizzo,SourceFile");
    }

    [Test]
    public void HuntFindsEveryTestDatabase()
    {
        var (exitCode, output) = Run("-d", Path.Combine(TestContext.CurrentContext.TestDirectory, "TestFiles"),
            "--csv", Csv);

        exitCode.Should().Be(0, output);
        output.Should().Contain("Processed 5 files");
        output.Should().NotContain("Error processing");
        // other maps not matching is normal when hunting, so it is only shown with --debug
        output.Should().NotContain("Queries will not be processed");

        CsvLines($"_Cars_Companies_{CarsId}.csv").Should().HaveCount(20);
        CsvLines($"_Cars_PaymentTypeAndAmounts_{CarsId}.csv").Should().HaveCount(1001);
        CsvLines($"_Contacts_Users_{ContactsId}.csv").Should().HaveCount(21);
    }

    [Test]
    public void BlobsAreExtracted()
    {
        var db = SqLiteFileTests.TestFile("BlobTest_Three.db");

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(0, output);

        // Data: named from the Name column, .txt; DataTwo: .blob, only where it is not NULL
        var blobs = Directory.GetFiles(Csv).Where(t => !t.EndsWith(".csv")).Select(Path.GetFileName).ToList();
        blobs.Should().Contain(t => t.EndsWith("_ValidName1_Data.txt"));
        blobs.Should().Contain(t => t.EndsWith("_ValidName1_DataTwo.blob"));
        blobs.Should().Contain(t => t.EndsWith("_File_Name_With_Spaces_Slashes_Data.txt"));
        blobs.Should().OnlyContain(t => t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

        var csv = Directory.GetFiles(Csv, "*.csv").Should().ContainSingle().Subject;
        File.ReadAllLines(csv)[0].Should().Be("Id,Name,SourceFile", "blob columns are not written to the CSV");
    }

    [Test]
    public void NoBlobSkipsExtraction()
    {
        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("BlobTest_Three.db"), "--csv", Csv, "--hunt",
            "false", "--noblob");

        exitCode.Should().Be(0, output);
        Directory.GetFiles(Csv).Should().ContainSingle().Which.Should().EndWith(".csv");
    }

    [Test]
    public void JsonHasOneLinePerRow()
    {
        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--json",
            Path.Combine(_dir, "json"), "--hunt", "false");

        exitCode.Should().Be(0, output);

        var json = Directory.GetFiles(Path.Combine(_dir, "json"), "*_SQLECmd_Output.json").Should().ContainSingle()
            .Subject;
        var lines = File.ReadAllLines(json);
        lines.Should().HaveCount(20 + 20 + 36 + 20);
        lines[0].Should().StartWith("{\"FirstName\":\"John\",\"LastName\":\"Doe\",\"SourceFile\":")
            .And.EndWith(",\"MapDescription\":\"Some rando contacts database example\"}");
    }

    [Test]
    public void DuplicatesAreSkipped()
    {
        var input = Path.Combine(_dir, "in");
        Directory.CreateDirectory(Path.Combine(input, "a"));
        Directory.CreateDirectory(Path.Combine(input, "b"));
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), Path.Combine(input, "a", "Contacts.db"));
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), Path.Combine(input, "b", "Contacts.db"));

        var (exitCode, output) = Run("-d", input, "--csv", Csv);

        exitCode.Should().Be(0, output);
        output.Should().Contain("has already been processed");
        output.Should().Contain("Processed 1 file in");
    }

    [Test]
    public void UnmatchedDatabaseIsReported()
    {
        var input = Path.Combine(_dir, "in");
        Directory.CreateDirectory(input);
        var db = Path.Combine(input, "other.db");
        File.Copy(SqLiteFileTests.TestFile("BlobTest.db"), db);

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(0, output);
        output.Should().Contain("At least one database was found with no corresponding map");
        output.Should().Contain("Tables: TestBlobs,sqlite_sequence");
    }

    [Test]
    public void MissingArgumentsFail()
    {
        Run().ExitCode.Should().Be(1);
        Run("-f", SqLiteFileTests.TestFile("Contacts.db")).ExitCode.Should().Be(1, "--csv or --json is required");
        Run("-f", Path.Combine(_dir, "missing.db"), "--csv", Csv).ExitCode.Should().Be(1);
    }

    [Test]
    public void BadMapFails()
    {
        var maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        File.WriteAllText(Path.Combine(maps, "Bad.smap"), "Description: no queries\nAuthor: a\n");

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", Csv, "--maps", maps);

        exitCode.Should().Be(1);
        output.Should().Contain("Bad.smap");
    }

    [Test]
    public void CorruptDatabaseFails()
    {
        var db = Path.Combine(_dir, "Contacts.db");
        var bytes = File.ReadAllBytes(SqLiteFileTests.TestFile("Contacts.db"));
        // keep the header so it is still found, then garbage
        for (var i = 100; i < bytes.Length; i++)
        {
            bytes[i] = 0xAA;
        }

        File.WriteAllBytes(db, bytes);

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(1, output);
        output.Should().Contain("Unable to read database: database disk image is malformed");
        output.Should().Contain("1 file(s) could not be processed");
        output.Should().NotContain("No maps found");
    }
}
