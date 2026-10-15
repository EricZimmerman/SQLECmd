using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;

namespace SQLMaps.Test;

[TestFixture]
public class SqLiteFileTests
{
    private string _dir;

    public static string TestFile(string name) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "TestFiles", name);

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

    [TestCase("Contacts.db")]
    [TestCase("CarsDB.db")]
    [TestCase("BlobTest.db")]
    [TestCase("BlobTest_Two.db")]
    [TestCase("BlobTest_Three.db")]
    public void SamplesAreSqLite(string name)
    {
        SqLiteFile.IsSqLiteFile(TestFile(name)).Should().BeTrue();
    }

    [Test]
    public void OtherFilesAreNot()
    {
        var text = Path.Combine(_dir, "text.db");
        File.WriteAllText(text, "this is not a database at all");
        SqLiteFile.IsSqLiteFile(text).Should().BeFalse();

        var empty = Path.Combine(_dir, "empty.db");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        SqLiteFile.IsSqLiteFile(empty).Should().BeFalse();

        var shortFile = Path.Combine(_dir, "short.db");
        File.WriteAllText(shortFile, "SQLite");
        SqLiteFile.IsSqLiteFile(shortFile).Should().BeFalse();

        SqLiteFile.IsSqLiteFile(Path.Combine(_dir, "missing.db")).Should().BeFalse();
    }

    [Test]
    public void FileOpenForWritingIsStillChecked()
    {
        // e.g. a browser database that is in use
        var copy = Path.Combine(_dir, "Contacts.db");
        File.Copy(TestFile("Contacts.db"), copy);

        using var open = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        SqLiteFile.IsSqLiteFile(copy).Should().BeTrue();
    }
}
