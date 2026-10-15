using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Serilog;

namespace SQLMaps.Test;

/// <summary>
///     Map loading and validation. <see cref="SqlMap.MapFiles" /> is static, so these do not run in parallel
/// </summary>
[TestFixture]
[NonParallelizable]
public class MapTests
{
    private string _dir;

    /// <summary>The shipped maps (SQLMap/Maps), copied next to the tests</summary>
    public static string ShippedMaps => Path.Combine(TestContext.CurrentContext.TestDirectory, "Maps");

    [OneTimeSetUp]
    public void SetupLog()
    {
        Log.Logger = new LoggerConfiguration().WriteTo.Console().MinimumLevel.Warning().CreateLogger();
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sqlmap-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_dir, true);
    }

    private const string ValidMap = @"Description: Test map
Author: Test
Email: test@test.com
Id: 11111111-2222-3333-4444-555555555555
Version: 1.0
CSVPrefix: Test
FileName: test.db
IdentifyQuery: SELECT count(*) FROM sqlite_master WHERE type='table' AND name='t'
IdentifyValue: 1
Queries:
    -
        Name: Everything
        Query: SELECT * FROM t
        BaseFileName: All
";

    private void WriteMap(string name, string contents)
    {
        File.WriteAllText(Path.Combine(_dir, name), contents);
    }

    [Test]
    public void EveryShippedMapLoads()
    {
        var smapCount = Directory.GetFiles(ShippedMaps, "*.smap").Length;
        smapCount.Should().BeGreaterThan(90);

        var errors = SqlMap.LoadMaps(ShippedMaps);

        errors.Should().BeFalse("every shipped map must load without errors");
        SqlMap.MapFiles.Should().HaveCount(smapCount, "map Ids must be unique");
        SqlMap.MapFiles.Values.Should().OnlyContain(t => t.Queries.Count > 0);
    }

    [Test]
    public void TestMapValues()
    {
        SqlMap.LoadMaps(ShippedMaps).Should().BeFalse();

        var contacts = SqlMap.MapFiles["83B99299-2D84-4844-AF25-C727D3440B19"];
        contacts.FileName.Should().Be("Contacts.db");
        contacts.CSVPrefix.Should().Be("Contacts");
        contacts.IdentifyValue.Should().Be("2");
        contacts.Queries.Select(t => t.BaseFileName).Should()
            .Equal("Users", "StateIdInfo", "JoinExample", "AnotherExample");

        var blobs = SqlMap.MapFiles["A1B2C3D4-E5F6-7890-1234-567890ABCDEH"];
        blobs.Queries.Single().BlobColumns.Select(t => (t.BlobColumn, t.NameColumn, t.BlobExtension)).Should()
            .Equal(("Data", "Name", "txt"), ("DataTwo", "Name", null));
    }

    [Test]
    public void ValidMapLoads()
    {
        WriteMap("Test.smap", ValidMap);

        SqlMap.LoadMaps(_dir).Should().BeFalse();
        SqlMap.MapFiles.Should().ContainKey("11111111-2222-3333-4444-555555555555");
    }

    [Test]
    public void MapWithoutQueriesIsAnError()
    {
        WriteMap("Test.smap", ValidMap.Substring(0, ValidMap.IndexOf("Queries:", StringComparison.Ordinal)));

        SqlMap.LoadMaps(_dir).Should().BeTrue();
        SqlMap.MapFiles.Should().BeEmpty();
    }

    [TestCase("Author: Test\n", "")]
    [TestCase("IdentifyQuery: SELECT count(*) FROM sqlite_master WHERE type='table' AND name='t'\n", "")]
    [TestCase("        BaseFileName: All\n", "")]
    [TestCase("        BaseFileName: All\n",
        "        BaseFileName: All\n        BlobColumns:\n            - BlobColumn: Data\n              BlobExtension: ../exe\n")]
    public void InvalidMapIsAnError(string remove, string replacement)
    {
        WriteMap("Test.smap", ValidMap.Replace(remove, replacement));

        SqlMap.LoadMaps(_dir).Should().BeTrue();
        SqlMap.MapFiles.Should().BeEmpty();
    }

    [Test]
    public void TabsAreASyntaxError()
    {
        WriteMap("Test.smap", ValidMap.Replace("    -\n", "\t-\n"));

        SqlMap.LoadMaps(_dir).Should().BeTrue();
    }

    [Test]
    public void UnknownPropertyIsAnError()
    {
        WriteMap("Test.smap", ValidMap.Replace("CSVPrefix:", "Nonsense: 1\nCSVPrefix:"));

        SqlMap.LoadMaps(_dir).Should().BeTrue();
    }

    [Test]
    public void DuplicateIdKeepsTheFirst()
    {
        WriteMap("A.smap", ValidMap);
        WriteMap("B.smap", ValidMap.Replace("Description: Test map", "Description: Duplicate"));

        SqlMap.LoadMaps(_dir).Should().BeFalse("a duplicate is skipped with a warning, not an error");
        SqlMap.MapFiles.Should().ContainSingle().Which.Value.Description.Should().Be("Test map");
    }

    [Test]
    public void QueryInfoToStringHandlesShortQueries()
    {
        new QueryInfo { Name = "n", Query = "SELECT 1", BaseFileName = "b" }.ToString().Should()
            .Contain("SELECT 1");
    }
}
