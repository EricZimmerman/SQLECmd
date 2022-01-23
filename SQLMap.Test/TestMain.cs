using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using FluentAssertions;
using NUnit.Framework;
using Serilog;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Dapper;

namespace SQLMaps.Test;

public class TestMain
{
    [OneTimeSetUp]
    public void SetupNLog()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Information()
            .CreateLogger();
    }


    /// <summary>
    ///     For normal mode, the database name is the first check to see if any maps exist.
    ///     In hunting mode, the name does not matter and all maps must be checked via IdentityQuery
    /// </summary>
    [Test]
    public void FindSqlFiles()
    {
        LoadMaps();

        Log.Information("Looking for SQLite databases...");

        var dir = @"..\..\..\TestFiles";

        var foundFiles = 0;

        foreach (var fileSystemEntry in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            Log.Verbose($"Checking '{fileSystemEntry}'");

            try
            {
                if (SqLiteFile.IsSqLiteFile(fileSystemEntry))
                {
                    Log.Information($"\t '{fileSystemEntry}' is a sqlite database");


                    var fName = Path.GetFileName(fileSystemEntry);

                    var maps = SqlMap.MapFiles.Values.Where(t =>
                        string.Equals(t.FileName, fName, StringComparison.InvariantCultureIgnoreCase)).ToList();


                    var outPath = @"C:\temp\sqlout";
                    if (Directory.Exists(outPath))
                    {
                        Directory.Delete(outPath, true);
                    }

                    var baseTime = DateTimeOffset.UtcNow;

                    Directory.CreateDirectory(outPath);

                    if (maps.Any())
                    {
                        Log.Information($"Found at least one map for '{fName}'");

                        foreach (var map in maps)
                        {
                            Log.Information("{@Map}", map);


                            var dbFactory = new OrmLiteConnectionFactory($"{fileSystemEntry}", SqliteDialect.Provider);

                            using (var db = dbFactory.Open())
                            {
                                Log.Information($"Verifying database via '{map.IdentifyQuery}'");
                                var id = db.ExecuteScalar<string>(map.IdentifyQuery);

                                if (string.Equals(id, map.IdentifyValue, StringComparison.InvariantCultureIgnoreCase) == false)
                                {
                                    Log.Warning($"Got value '{id}' from IdentityQuery, but expected '{map.IdentifyValue}'. Queries will not be processed!");
                                    continue;
                                }

                                Log.Information($"Map queries found: {map.Queries.Count:N0}. Processing...");
                                foreach (var queryInfo in map.Queries)
                                {
                                    try
                                    {
                                        Log.Information($"Dumping information for '{queryInfo.Name}'. BaseFilename: {queryInfo.BaseFileName}");

                                        var foo = db.Query<dynamic>(queryInfo.Query);

                                        //  var bar = (IDictionary<string, object>) foo.First();

                                        //  Log.Information($"Headers: {string.Join(",",bar.Keys)}");

                                        var outName =
                                            $"{baseTime:yyyyMMddHHmmss}_{queryInfo.BaseFileName}.csv";

                                        var fullOutName = Path.Combine(outPath, outName);

                                        using (var writer = new StreamWriter(new FileStream(fullOutName, FileMode.CreateNew))) //var writer = new StringWriter()
                                        {
                                            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
                                            {
                                                //  csv.WriteDynamicHeader(foo.First());
                                                //   csv.NextRecord();

                                                // foreach(IDictionary<string, object> row in foo) {
                                                //     Console.WriteLine("row:");
                                                //     foreach(var pair in row) {
                                                //         Console.WriteLine("  {0} = {1}", pair.Key, pair.Value);
                                                //     }
                                                // }

                                                csv.WriteRecords(foo);

                                                //Log.Information(writer.ToString());

                                                csv.Flush();
                                                writer.Flush();
                                            }
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        Log.Error(e.Message);
                                    }
                                }
                            }
                        }
                    }


                    foundFiles += 1;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "{Error}", e.Message);
            }
        }

        foundFiles.Should().Be(1);
    }

    [Test]
    public void LoadMaps()
    {
        Log.Information("Loading maps!");

        SqlMap.LoadMaps(@"D:\OneDrive\!Projects\sqliteTestFiles");
    }
}