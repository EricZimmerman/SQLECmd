using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using FluentAssertions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Dapper;

namespace SQLMaps.Test
{
    public class TestMain
    {
        private readonly Logger _logger = LogManager.GetLogger("Test");

        [OneTimeSetUp]
        public void SetupNLog()
        {
            LogManager.Configuration = null;

            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Debug;

            var layout = @"${message}";

            var consoleTarget = new ColoredConsoleTarget();

            config.AddTarget("console", consoleTarget);

            consoleTarget.Layout = layout;

            var rule1 = new LoggingRule("*", loglevel, consoleTarget);
            config.LoggingRules.Add(rule1);

            LogManager.Configuration = config;
        }


        /// <summary>
        /// For normal mode, the database name is the first check to see if any maps exist.
        /// In hunting mode, the name does not matter and all maps must be checked via IdentityQuery
        /// </summary>

        [Test]
        public void FindSqlFiles()
        {
            LoadMaps();

            _logger.Info("Looking for SQLite databases...");

            var dir = @"..\..\..\TestFiles";

            var foundFiles = 0;

            foreach (var fileSystemEntry in Directory.GetFiles(dir,"*",SearchOption.AllDirectories))
            {
                _logger.Trace($"Checking '{fileSystemEntry}'");

                try
                {
                    if (SQLiteFile.IsSQLiteFile(fileSystemEntry))
                    {
                        _logger.Info($"\t '{fileSystemEntry}' is a sqlite database");


                        var fName = Path.GetFileName(fileSystemEntry);

                        var maps = SQLMap.MapFiles.Values.Where(t =>
                            string.Equals(t.FileName, fName, StringComparison.InvariantCultureIgnoreCase)).ToList();


                        var outPath = @"C:\temp\sqlout";
                        if (Directory.Exists(outPath))
                        {
                            Directory.Delete(outPath,true);
                        }

                        var baseTime = DateTimeOffset.UtcNow;

                        Directory.CreateDirectory(outPath);

                        if (maps.Any())
                        {
                            _logger.Info($"Found at least one map for '{fName}'");

                            foreach (var map in maps)
                            {
                                _logger.Info(map);


                                var dbFactory = new OrmLiteConnectionFactory($"{fileSystemEntry}",SqliteDialect.Provider);

                                using (var db = dbFactory.Open())
                                {
                                    _logger.Info($"Verifying database via '{map.IdentifyQuery}'");
                                    var id = db.ExecuteScalar<string>(map.IdentifyQuery);

                                    if (string.Equals(id,map.IdentifyValue,StringComparison.InvariantCultureIgnoreCase) == false)
                                    {
                                        _logger.Warn($"Got value '{id}' from IdentityQuery, but expected '{map.IdentifyValue}'. Queries will not be processed!");
                                        continue;
                                    }
                                    
                                    _logger.Info($"Map queries found: {map.Queries.Count:N0}. Processing...");
                                    foreach (var queryInfo in map.Queries)
                                    {
                                        try
                                        {
                                            _logger.Info($"Dumping information for '{queryInfo.Name}'. BaseFilename: {queryInfo.BaseFileName}");

                                            var foo = db.Query<dynamic>(queryInfo.Query);

                                            //  var bar = (IDictionary<string, object>) foo.First();

                                            //  _logger.Info($"Headers: {string.Join(",",bar.Keys)}");

                                            var outName =
                                                $"{baseTime:yyyyMMddHHmmss}_{queryInfo.BaseFileName}.csv";

                                            var fullOutName = Path.Combine(outPath, outName);

                                            using (var writer = new StreamWriter(new FileStream(fullOutName,FileMode.CreateNew))) //var writer = new StringWriter()
                                            {
                                                using (var csv = new CsvWriter(writer,CultureInfo.InvariantCulture))
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
                        
                                                    //_logger.Info(writer.ToString());

                                                    csv.Flush();
                                                    writer.Flush();
                                                }
                                                
                                            }
                                        }
                                        catch (Exception e)
                                        {
                                            _logger.Error(e.Message);
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
                    _logger.Error(e);
                    
                }
            }

            foundFiles.Should().Be(1);

        }

        [Test]
        public void LoadMaps()
        {
            _logger.Info("Loading maps!");

            SQLMap.LoadMaps(@"D:\OneDrive\!Projects\sqliteTestFiles");

        }

        [Test]
        public void Test()
        {
            var s = new SQLMap();
            s.Test(@"C:\Temp\Contacts.db");
        }
    }
}
