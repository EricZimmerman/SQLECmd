﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Alphaleonis.Win32.Filesystem;
using Alphaleonis.Win32.Security;
using CsvHelper;
using Exceptionless;
using Fclp;
using ICSharpCode.SharpZipLib.Zip;
using NLog;
using NLog.Config;
using NLog.Targets;
using ServiceStack;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Dapper;
using SQLECmd.Properties;
using SQLMaps;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace SQLECmd
{
    class Program
    {
        private static Logger _logger;

        private static FluentCommandLineParser<ApplicationArguments> _fluentCommandLineParser;

        private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        static void Main(string[] args)
        {
            ExceptionlessClient.Default.Startup("DyZCm8aZbNXf2iZ6BV00wY2UoR3U2tymh3cftNZs");

            SetupNLog();

            _logger = LogManager.GetLogger("EvtxECmd");

            _fluentCommandLineParser = new FluentCommandLineParser<ApplicationArguments>
            {
                IsCaseSensitive = false
            };

            _fluentCommandLineParser.Setup(arg => arg.File)
                .As('f')
                .WithDescription("File to process. This or -d is required\r\n");
            _fluentCommandLineParser.Setup(arg => arg.Directory)
                .As('d')
                .WithDescription("Directory to process that contains SQLite files. This or -f is required");

            _fluentCommandLineParser.Setup(arg => arg.CsvDirectory)
                .As("csv")
                .WithDescription(
                    "Directory to save CSV formatted results to."); // This, --json, or --xml required

            _fluentCommandLineParser.Setup(arg => arg.JsonDirectory)
                .As("json")
                .WithDescription(
                    "Directory to save JSON formatted results to.\r\n"); // This, --csv, or --xml required
          
            _fluentCommandLineParser.Setup(arg => arg.Dedupe)
                .As("dedupe")
                .WithDescription(
                    "Deduplicate -f or -d files based on SHA-1. First file found wins. Default is TRUE")
                .SetDefault(true);

            _fluentCommandLineParser.Setup(arg => arg.Hunt)
                .As("hunt")
                .WithDescription(
                    "When true, all files are looked at regardless of name and file header is used to identify SQLite files, else filename in map is used to find databases. Default is FALSE\r\n  ")
                .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.MapsDirectory)
                .As("maps")
                .WithDescription(
                    "The path where event maps are located. Defaults to 'Maps' folder where program was executed\r\n  ")
                .SetDefault(Path.Combine(BaseDirectory, "Maps"));

            _fluentCommandLineParser.Setup(arg => arg.Sync)
                .As("sync")
                .WithDescription(
                    "If true, the latest maps from https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps are downloaded and local maps updated. Default is FALSE\r\n")
                .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.Debug)
                .As("debug")
                .WithDescription("Show debug information during processing").SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.Trace)
                .As("trace")
                .WithDescription("Show trace information during processing\r\n").SetDefault(false);

            var header =
                $"SQLECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/SQLECmd";

            var footer =
                @"Examples: SQLECmd.exe -f ""C:\Temp\someFile.db"" --csv ""c:\temp\out"" " +
                "\r\n\t " +
                @" SQLECmd.exe -d ""C:\Temp\"" --csv ""c:\temp\out""" + "\r\n\t " +
                @" SQLECmd.exe -d ""C:\Temp\"" --hunt --csv ""c:\temp\out""" + "\r\n\t " +
                "\r\n\t" +
                "  Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes\r\n";

            _fluentCommandLineParser.SetupHelp("?", "help")
                .WithHeader(header)
                .Callback(text => _logger.Info(text + "\r\n" + footer));

            var result = _fluentCommandLineParser.Parse(args);

            if (result.HelpCalled)
            {
                return;
            }

            if (result.HasErrors)
            {
                _logger.Error("");
                _logger.Error(result.ErrorText);

                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                return;
            }

            if (_fluentCommandLineParser.Object.Sync)
            {
                try
                {
                    _logger.Info(header);
                    UpdateFromRepo();
                }
                catch (Exception e)
                {
                    _logger.Error(e, $"There was an error checking for updates: {e.Message}");
                }

                Environment.Exit(0);
            }

            if (_fluentCommandLineParser.Object.File.IsNullOrEmpty() &&
                _fluentCommandLineParser.Object.Directory.IsNullOrEmpty())
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("-f or -d is required. Exiting");
                return;
            }

            if (_fluentCommandLineParser.Object.CsvDirectory.IsNullOrEmpty() )
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("--csv is required. Exiting");
                return;
            }

            _logger.Info(header);
            _logger.Info("");
            _logger.Info($"Command line: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}\r\n");

            if (_fluentCommandLineParser.Object.Debug)
            {
                LogManager.Configuration.LoggingRules.First().EnableLoggingForLevel(LogLevel.Debug);
            }

            if (_fluentCommandLineParser.Object.Trace)
            {
                LogManager.Configuration.LoggingRules.First().EnableLoggingForLevel(LogLevel.Trace);
            }

            LogManager.ReconfigExistingLoggers();

            DumpSqliteDll();

            var sw = new Stopwatch();
            sw.Start();

            var ts = DateTimeOffset.UtcNow;

            if (Directory.Exists(_fluentCommandLineParser.Object.MapsDirectory) == false)
            {
                _logger.Warn(
                    $"Maps directory '{_fluentCommandLineParser.Object.MapsDirectory}' does not exist! Database maps will not be loaded!!");
            }
            else
            {
                _logger.Debug($"Loading maps from '{Path.GetFullPath(_fluentCommandLineParser.Object.MapsDirectory)}'");
                var errors =  SQLMap.LoadMaps(Path.GetFullPath(_fluentCommandLineParser.Object.MapsDirectory));
                
                if (errors)
                {
                    return;
                }

                _logger.Info($"Maps loaded: {SQLMap.MapFiles.Count:N0}");
            }

            if (Directory.Exists(_fluentCommandLineParser.Object.CsvDirectory) == false)
            {
                _logger.Warn(
                    $"Path to '{_fluentCommandLineParser.Object.CsvDirectory}' doesn't exist. Creating...");

                try
                {
                    Directory.CreateDirectory(_fluentCommandLineParser.Object.CsvDirectory);
                }
                catch (Exception)
                {
                    _logger.Fatal(
                        $"Unable to create directory '{_fluentCommandLineParser.Object.CsvDirectory}'. Does a file with the same name exist? Exiting");
                    return;
                }
            }

            if (_fluentCommandLineParser.Object.File.IsNullOrEmpty() == false)
            {
                if (File.Exists(_fluentCommandLineParser.Object.File) == false)
                {
                    _logger.Warn($"'{_fluentCommandLineParser.Object.File}' does not exist! Exiting");
                    return;
                }

                ProcessFile(Path.GetFullPath(_fluentCommandLineParser.Object.File));
            }
            else
            {
                //Directories
                _logger.Info($"Looking for files in '{_fluentCommandLineParser.Object.Directory}'");
                _logger.Info("");

                Privilege[] privs = {Privilege.EnableDelegation, Privilege.Impersonate, Privilege.Tcb};
                using (new PrivilegeEnabler(Privilege.Backup, privs))
                {
                    var dirEnumOptions =
                    DirectoryEnumerationOptions.Files | DirectoryEnumerationOptions.Recursive |
                    DirectoryEnumerationOptions.SkipReparsePoints | DirectoryEnumerationOptions.ContinueOnException |
                    DirectoryEnumerationOptions.BasicSearch;

                    var f = new DirectoryEnumerationFilters
                    {
                        RecursionFilter = entryInfo => !entryInfo.IsMountPoint && !entryInfo.IsSymbolicLink,
                        ErrorFilter = (errorCode, errorMessage, pathProcessed) => true
                    };

                    var dbNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

                    if (_fluentCommandLineParser.Object.Hunt)
                    {
                        f.InclusionFilter = fsei => true;
                    }
                    else
                    {
                        foreach (var mapFile in SQLMap.MapFiles)
                        {
                            dbNames.Add(mapFile.Value.FileName);
                        }

                        f.InclusionFilter = fsei => dbNames.Contains(fsei.FileName);
                    }

                    var files2 =
                        Directory.EnumerateFileSystemEntries(Path.GetFullPath(_fluentCommandLineParser.Object.Directory), dirEnumOptions, f);

                    foreach (var file in files2)
                    {
                        try
                        {
                            ProcessFile(file);
                        }
                        catch (Exception e)
                        {
                            _logger.Error($"Error processing '{file}': {e.Message}");
                        }
                    }

                }
            }

            sw.Stop();

            if (UnmatchedDbs.Any())
            {
                Console.WriteLine();
                _logger.Fatal($"At least one database was found with no corresponding map (Use --debug for more details about discovery process)");

                foreach (var unmatchedDb in UnmatchedDbs)
                {
                    DumpUnmatched(unmatchedDb);
                }
            }
            
            var extra = string.Empty;

            if (ProcessedFiles.Count > 1)
            {
                extra = "s";
            }

            _logger.Info($"\r\nProcessed {ProcessedFiles.Count:N0} file{extra} in {sw.Elapsed.TotalSeconds:N4} seconds\r\n");

            if (!File.Exists("SQLite.Interop.dll"))
            {
                return;
            }

            try
            {
                File.Delete("SQLite.Interop.dll");
            }
            catch (Exception)
            {
                _logger.Warn("Unable to delete 'SQLite.Interop.dll'. Delete manually if needed.\r\n");
            }

        }

        private static void DumpUnmatched(string unmatchedDb)
        {
            var dbFactory = new OrmLiteConnectionFactory($"{unmatchedDb}",SqliteDialect.Provider);

            using (var db = dbFactory.Open())
            {
                _logger.Trace($"\tGetting table names for '{unmatchedDb}'");
                var reader=   db.ExecuteReader("SELECT name FROM sqlite_master WHERE type='table' order by name");

                var tables = new List<string>();
                while (reader.Read())
                {
                    tables.Add(reader[0].ToString());
                }
                
                _logger.Info($"\tFile name: '{unmatchedDb}', Tables: {string.Join(",",tables)}");                
                     
            }
        }

        private static readonly List<string> ProcessedFiles  = new List<string>();

        private static void DumpSqliteDll()
        {
            var sqllitefile = "SQLite.Interop.dll";
            File.WriteAllBytes(sqllitefile, Resources.SQLite_Interop);
        }

        private static void ProcessFile(string fileName)
        {
            _logger.Debug($"Checking if '{fileName}' is a SQLite file");
            if (SQLiteFile.IsSQLiteFile(fileName) == false)
            {
                if (_fluentCommandLineParser.Object.Hunt == false)
                {
                    _logger.Error($"\t'{fileName}' is not a SQLite file! Skipping...");
                }
                else
                {
                    _logger.Debug($"\t'{fileName}' is not a SQLite file! Skipping...");
                }
                
                return;
            }

            _logger.Debug($"'{fileName}' is a SQLite file!");

            if (_fluentCommandLineParser.Object.Dedupe)
            {
                var sha = File.GetHash(fileName, HashType.SHA1);

                if (SeenHashes.Contains(sha))
                {
                    _logger.Error($"Skipping '{fileName}' as a file with SHA-1 '{sha}' has already been processed");
                    Console.WriteLine();
                    return;
                }

                _logger.Debug($"Adding '{fileName}' SHA-1 '{sha}' to seen hashes collection");
                SeenHashes.Add(sha);
            }

            _logger.Warn($"Processing '{fileName}'...");

            ProcessedFiles.Add(fileName);

            var maps = new List<MapFile>();

            if (_fluentCommandLineParser.Object.Hunt)
            {
                maps = SQLMap.MapFiles.Values.ToList();
            }
            else
            {
                //only find maps tht match the db filename
                maps = SQLMap.MapFiles.Values.Where(t => string.Equals(t.FileName, Path.GetFileName(fileName),
                    StringComparison.InvariantCultureIgnoreCase)).ToList();
            }

            var foundMap = false;

            //need to run thru each map and see if we get any IdentityQuery matches
            //process each one that matches
            foreach (var map in maps)
            {
                _logger.Debug($"Processing map '{map.Description}' with Id '{map.Id}'");

                var dbFactory = new OrmLiteConnectionFactory($"{fileName}",SqliteDialect.Provider);

                var baseTime = DateTimeOffset.UtcNow;

                 using (var db = dbFactory.Open())
                 {
                     _logger.Debug($"\tVerifying database via '{map.IdentifyQuery}'");

                     var id = db.ExecuteScalar<string>(map.IdentifyQuery);

                     if (string.Equals(id,map.IdentifyValue,StringComparison.InvariantCultureIgnoreCase) == false)
                     {
                         _logger.Error($"\tFor map w/ description '{map.Description}', got value '{id}' from IdentityQuery, but expected '{map.IdentifyValue}'. Queries will not be processed!");
                         continue;
                     }

                     //if we find any matches, its not unmatched anymore
                     foundMap = true;
                                    
                     _logger.Error($"\tMap queries found: {map.Queries.Count:N0}. Processing queries...");
                     foreach (var queryInfo in map.Queries)
                     {
                         _logger.Debug($"Processing query '{queryInfo.Name}'");

                         try
                         {
                             var results = db.Query<dynamic>(queryInfo.Query).ToList();
                             
                             var outName =
                                 $"{baseTime:yyyyMMddHHmmssffffff}_{map.CSVPrefix}_{queryInfo.BaseFileName}_{map.Id}.csv";

                             var fullOutName = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);

                             if (results.Any() == false)
                             {
                                _logger.Warn($"\t '{queryInfo.Name}' did not return any results. CSV will not be saved.");
                                continue;
                             }

                             _logger.Info($"\tDumping '{queryInfo.Name}' to '{fullOutName}'");

                             using (var writer = new StreamWriter(new FileStream(fullOutName,FileMode.CreateNew)))
                             {
                                 using (var csv = new CsvWriter(writer,CultureInfo.InvariantCulture))
                                 {
                                    
                                  //   csv.WriteRecords(results);

                                     foreach (var result in results)
                                     {
                                         result.SourceFile = fileName;
                                         csv.WriteRecord(result);
                                         csv.NextRecord();
                                     }

                                     // csv.WriteComment("");
                                     // csv.NextRecord();
                                     // csv.WriteComment($"SourceFile: {fileName}");
                                     // csv.NextRecord();
                                     csv.Flush();
                                     writer.Flush();
                                 }
                             }
                         }
                         catch (Exception e)
                         {
                             _logger.Fatal($"Error processing map '{map.Description}' with Id '{map.Id}' for query '{queryInfo.Name}':");
                             _logger.Error($"\t{e.Message.Replace("\r\n","\r\n\t")}");
                         }

                     }
                 }
            }

            if (foundMap == false)
            {
                _logger.Info($"\tNo maps found for '{fileName}'. Adding to unmatched database list");
                UnmatchedDbs.Add(fileName);
            }

            Console.WriteLine();
        }

        private static readonly HashSet<string> UnmatchedDbs = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private static readonly HashSet<string> SeenHashes = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

        private static void UpdateFromRepo()
        {
            Console.WriteLine();

            _logger.Info(
                "Checking for updated maps at https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps...");
            Console.WriteLine();
            var archivePath = Path.Combine(BaseDirectory, "____master.zip");

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            using (var client = new WebClient())
            {
                ServicePointManager.Expect100Continue = true;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                client.DownloadFile("https://github.com/EricZimmerman/SQLECmd/archive/master.zip", archivePath);
            }

            var fff = new FastZip();

            var directoryFilter = "Maps";

            // Will prompt to overwrite if target file names already exist
            fff.ExtractZip(archivePath, BaseDirectory, FastZip.Overwrite.Always, null,
                null, directoryFilter, true);

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            var newMapPath = Path.Combine(BaseDirectory, "SQLECmd-master","SQLMap", "Maps");

            var orgMapPath = Path.Combine(BaseDirectory, "Maps");

            if (Directory.Exists(orgMapPath) == false)
            {
                Directory.CreateDirectory(orgMapPath);
            }

            var newMaps = Directory.GetFiles(newMapPath);

            var newlocalMaps = new List<string>();

            var updatedlocalMaps = new List<string>();

            foreach (var newMap in newMaps)
            {
                var mName = Path.GetFileName(newMap);
                var dest = Path.Combine(orgMapPath, mName);

                if (File.Exists(dest) == false)
                {
                    //new target
                    newlocalMaps.Add(mName);
                }
                else
                {
                    //current destination file exists, so compare to new
                    var fiNew = new FileInfo(newMap);
                    var fi = new FileInfo(dest);

                    if (fiNew.GetHash(HashType.SHA1) != fi.GetHash(HashType.SHA1))
                    {
                        //updated file
                        updatedlocalMaps.Add(mName);
                    }
                }

                File.Copy(newMap, dest, CopyOptions.None);
            }

            if (newlocalMaps.Count > 0 || updatedlocalMaps.Count > 0)
            {
                _logger.Fatal("Updates found!");
                Console.WriteLine();

                if (newlocalMaps.Count > 0)
                {
                    _logger.Error("New maps");
                    foreach (var newLocalMap in newlocalMaps)
                    {
                        _logger.Info(Path.GetFileNameWithoutExtension(newLocalMap));
                    }

                    Console.WriteLine();
                }

                if (updatedlocalMaps.Count > 0)
                {
                    _logger.Error("Updated maps");
                    foreach (var um in updatedlocalMaps)
                    {
                        _logger.Info(Path.GetFileNameWithoutExtension(um));
                    }

                    Console.WriteLine();
                }

                Console.WriteLine();
            }
            else
            {
                Console.WriteLine();
                _logger.Info("No new maps available");
                Console.WriteLine();
            }

            Directory.Delete(Path.Combine(BaseDirectory, "SQLECmd-master"), true);
        }

        private static void SetupNLog()
        {
            if (File.Exists(Path.Combine(BaseDirectory, "Nlog.config")))
            {
                return;
            }

            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Info;

            var layout = @"${message}";

            var consoleTarget = new ColoredConsoleTarget();

            config.AddTarget("console", consoleTarget);

            consoleTarget.Layout = layout;

            var rule1 = new LoggingRule("*", loglevel, consoleTarget);
            config.LoggingRules.Add(rule1);

            LogManager.Configuration = config;
        }
    }

    internal class ApplicationArguments
    {
        public string File { get; set; }
        public string Directory { get; set; }

        public string CsvDirectory { get; set; }
        public string JsonDirectory { get; set; }



        public bool Hunt { get; set; }
        public bool Debug { get; set; }
        public bool Trace { get; set; }

        public string CsvName { get; set; }
        public string JsonName { get; set; }
        
        public string MapsDirectory { get; set; }



 //       public bool Vss { get; set; }
        public bool Dedupe { get; set; }
        public bool Sync { get; set; }
    }
}
