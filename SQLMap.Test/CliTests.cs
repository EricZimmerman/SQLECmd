using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using SQLECmd;

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

    // the per-user folders earlier SQLECmd builds extracted SQLite.Interop.dll to (LOCALAPPDATA, ~/.local/share), or
    // a temp folder: pointed at an empty folder, which must stay empty (Windows takes LocalApplicationData from the
    // shell, not the variable; NativeSqliteTests checks where the library is loaded from)
    private string Profile => Path.Combine(_dir, "profile");

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sqlecmd-test-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_dir, "cwd");
        Directory.CreateDirectory(_cwd);
        Directory.CreateDirectory(Profile);
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
        foreach (var name in new[] { "LOCALAPPDATA", "XDG_DATA_HOME", "TEMP", "TMP", "TMPDIR" })
        {
            start.Environment[name] = Profile;
        }

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
        // nor anything anywhere else: the native library is loaded from SQLECmd's folder, never extracted
        Directory.GetFileSystemEntries(Profile).Should().BeEmpty();
        // and every copy of a database is deleted (a statement left open kept one on Windows)
        if (Directory.Exists(TempNextToProgram))
        {
            Directory.GetDirectories(TempNextToProgram, "SQLECmd-*").Should().BeEmpty();
        }

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
    public void HuntDoesNotReportFoldersAsMissing()
    {
        //every subfolder was passed on as a file and logged "<folder> not found!"
        var input = Path.Combine(_dir, "input");
        var sub = Directory.CreateDirectory(Path.Combine(input, "sub", "deeper")).FullName;
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), Path.Combine(sub, "Contacts.db"));

        var (exitCode, output) = Run("-d", input, "--csv", Csv, "--debug");

        exitCode.Should().Be(0, output);
        output.Should().NotContain("not found!");
        CsvLines($"_Contacts_Users_{ContactsId}.csv").Should().HaveCount(21);
    }

    [Test]
    public void UnreadableSubfolderFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Folder ACLs are Windows only");
            return;
        }

        //a folder under -d that can't be read is reported by name and gives 1; the rest is still processed
        var input = Path.Combine(_dir, "input");
        Directory.CreateDirectory(Path.Combine(input, "good"));
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), Path.Combine(input, "good", "Contacts.db"));
        var denied = Directory.CreateDirectory(Path.Combine(input, "denied"));
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), Path.Combine(denied.FullName, "Contacts.db"));

        var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
        var security = denied.GetAccessControl();
        security.AddAccessRule(rule);
        denied.SetAccessControl(security);
        try
        {
            foreach (var hunt in new[] { "true", "false" })
            {
                var (exitCode, output) = Run("-d", input, "--csv", Path.Combine(_dir, "csv" + hunt), "--hunt", hunt);

                exitCode.Should().Be(1, output);
                output.Should().Contain($"Unable to read folder {denied.FullName}");
                output.Should().Contain("Could not access all files");
                //once, though without --hunt the folder is searched once per map
                output.Split($"Unable to read folder {denied.FullName}").Should().HaveCount(2, output);
                Directory.GetFiles(Path.Combine(_dir, "csv" + hunt), $"*_Contacts_Users_{ContactsId}.csv").Should().ContainSingle();
            }
        }
        finally
        {
            security = denied.GetAccessControl();
            security.RemoveAccessRule(rule);
            denied.SetAccessControl(security);
        }
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
        Run("-d", Path.Combine(_dir, "missing"), "--csv", Csv).ExitCode.Should().Be(1);
        Run("--help").ExitCode.Should().Be(0);
    }

    [Test]
    public void NotASqliteFileIsListedAndExitsWithZero()
    {
        //a bad file is not a failure of SQLECmd (exit code 0), but it is still reported
        var file = Path.Combine(_dir, "notsqlite.db");
        File.WriteAllText(file, "not a database");

        var (exitCode, output) = Run("-f", file, "--csv", Csv);

        exitCode.Should().Be(0, output);
        output.Should().Contain("is not a SQLite file");
        output.Should().Contain("1 file(s) could not be processed");
    }

    [Test]
    public void FailingQueryIsListedAndExitsWithZero()
    {
        //the map matches, but one of its queries reads a table that isn't there
        var maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        var map = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Maps", "TestFiles_Contacts.smap"));
        File.WriteAllText(Path.Combine(maps, "TestFiles_Contacts.smap"),
            map.Replace("SELECT FirstName,LastName from Customers", "SELECT FirstName,LastName from NoSuchTable"));

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", Csv, "--hunt", "false", "--maps", maps);

        exitCode.Should().Be(0, output);
        output.Should().Contain("no such table: NoSuchTable");
        output.Should().Contain("1 query/export error(s)");
    }

    [Test]
    public void CsvThatCantBeWrittenFails()
    {
        //--csv pointing at an existing file can't be created as a folder
        var file = Path.Combine(_dir, "a file");
        File.WriteAllText(file, "x");

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", file, "--hunt", "false");

        exitCode.Should().Be(1, output);
        output.Should().Contain("Unable to create directory");
    }

    [Test]
    public void LockedFileFails()
    {
        if (OperatingSystem.IsWindows() == false)
        {
            Assert.Ignore("FileShare.None locks only on Windows");
        }

        //a file SQLECmd can't open is a failure (exit code 1), unlike a damaged one
        var db = Path.Combine(_dir, "Contacts.db");
        File.Copy(SqLiteFileTests.TestFile("Contacts.db"), db);

        using (new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (exitCode, output) = Run("-f", db, "--csv", Csv, "--hunt", "false");

            exitCode.Should().Be(1, output);
            output.Should().Contain("1 file(s) could not be processed");
        }
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
    public void CorruptDatabaseIsListedAndExitsWithZero()
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

        exitCode.Should().Be(0, output);
        output.Should().Contain("Unable to read database: database disk image is malformed");
        output.Should().Contain("1 file(s) could not be processed");
        output.Should().NotContain("No maps found");
    }
    private static string TempNextToProgram => Path.Combine(TestContext.CurrentContext.TestDirectory, "Temp");

    private static string Sha256(string file) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));

    [Test]
    public void TheEvidenceIsNotChanged()
    {
        //a database with rows only in its -wal: opening it would checkpoint them into the database and delete the -wal
        NativeSqlite.Setup();
        var live = Directory.CreateDirectory(Path.Combine(_dir, "live")).FullName;
        var evidence = Directory.CreateDirectory(Path.Combine(_dir, "evidence")).FullName;
        using (var connection = new SQLiteConnection($"Data Source={Path.Combine(live, "w.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t(a); INSERT INTO t VALUES (1), (2);";
            command.ExecuteNonQuery();
            foreach (var name in new[] { "w.db", "w.db-wal" })
            {
                using var input = new FileStream(Path.Combine(live, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = File.Create(Path.Combine(evidence, name));
                input.CopyTo(target);
            }
        }

        var before = Directory.GetFiles(evidence).ToDictionary(Path.GetFileName, Sha256);
        before.Should().ContainKey("w.db-wal");

        var (exitCode, output) = Run("-d", evidence, "--csv", Csv);

        exitCode.Should().Be(0, output);
        output.Should().Contain("w.db");
        Directory.GetFiles(evidence).ToDictionary(Path.GetFileName, Sha256).Should().BeEquivalentTo(before);
    }

    [Test]
    public void CopiesGoToTempNextToTheProgram()
    {
        var existed = Directory.Exists(TempNextToProgram);

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", Csv, "--hunt", "false");

        exitCode.Should().Be(0, output);
        //the copies' folders are deleted, and Temp too when this run made it and it is empty
        if (Directory.Exists(TempNextToProgram))
        {
            existed.Should().BeTrue("Temp is removed when the run created it and it is empty");
            Directory.GetDirectories(TempNextToProgram, "SQLECmd-*").Should().BeEmpty();
        }
    }

    [Test]
    public void CopiesGoToTheTempGiven()
    {
        var temp = Directory.CreateDirectory(Path.Combine(_dir, "my temp")).FullName;

        var (exitCode, output) = Run("-d", SqLiteFileTests.TestFile(""), "--csv", Csv, "--temp", temp);

        exitCode.Should().Be(0, output);
        Directory.Exists(temp).Should().BeTrue("a folder that was there is kept");
        Directory.GetFileSystemEntries(temp).Should().BeEmpty("the copies are deleted when done");

        //one that wasn't there is created, and removed again
        var newTemp = Path.Combine(_dir, "new temp");
        Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", Csv, "--hunt", "false", "--temp", newTemp).ExitCode.Should().Be(0);
        Directory.Exists(newTemp).Should().BeFalse();
    }

    [Test]
    public void TempThatCantBeWrittenStops()
    {
        //a folder under a file can't be created
        var file = Path.Combine(_dir, "a file");
        File.WriteAllText(file, "x");

        var (exitCode, output) = Run("-f", SqLiteFileTests.TestFile("Contacts.db"), "--csv", Csv, "--temp", Path.Combine(file, "temp"));

        exitCode.Should().Be(1, output);
        output.Should().Contain("Give one with --temp");
        output.Should().NotContain("Processing ");
    }
    [Test]
    public void ConsoleLog()
    {
        // Issues #259: --co saves the console messages to <yyyyMMddHHmmss>_SQLECmd_<version>_ConsoleLog.txt in the first output directory given
        var db = SqLiteFileTests.TestFile("Contacts.db");
        var json = Path.Combine(_dir, "json");

        var (exitCode, output) = Run("-f", db, "--csv", Csv, "--json", json, "--co");
        exitCode.Should().Be(0, output);
        var log = Directory.GetFiles(Csv, "*_ConsoleLog.txt").Should().ContainSingle().Subject;
        Path.GetFileName(log).Should().MatchRegex(@"^\d{14}_SQLECmd_\d+\.\d+\.\d+_ConsoleLog\.txt$");
        Directory.GetFiles(json, "*_ConsoleLog.txt").Should().BeEmpty();
        output.Should().Contain("Console log: " + log);
        // the same time stamp as the JSON output
        Directory.GetFiles(json, "*_SQLECmd_Output.json").Should().ContainSingle().Which.Should().Contain(Path.GetFileName(log)[..14]);
        var text = File.ReadAllText(log);
        text.Should().MatchRegex(@"\[\d\d:\d\d:\d\d\.\d{3} INF\] SQLECmd version ");
        text.Should().Contain("Command line: -f ");

        // --json alone: there
        (exitCode, output) = Run("-f", db, "--json", json, "--co");
        exitCode.Should().Be(0, output);
        Directory.GetFiles(json, "*_ConsoleLog.txt").Should().ContainSingle();

        // without --co, none
        var other = Path.Combine(_dir, "other");
        (exitCode, output) = Run("-f", db, "--csv", other);
        exitCode.Should().Be(0, output);
        Directory.GetFiles(other, "*_ConsoleLog.txt").Should().BeEmpty();
        output.Should().NotContain("Console log:");
    }
}
