using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using CsvHelper;
using Exceptionless;
using ICSharpCode.SharpZipLib.Zip;
using RawCopy;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceStack;
using ServiceStack.Text;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Dapper;
using SQLMaps;

namespace SQLECmd;

internal class Program
{
    private static string _activeDateTimeFormat;
    private static RootCommand _rootCommand;
    private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    // where the copies of the databases are made (--temp, or Temp next to SQLECmd)
    private static string _tempFolder;

    private static readonly string Header =
        $"SQLECmd version {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/SQLECmd";

    private static readonly string Footer =
        @"Examples: SQLECmd.exe -f ""C:\Temp\someFile.db"" --csv ""c:\temp\out"" " +
        "\r\n\t " +
        @"   SQLECmd.exe -d ""C:\Temp\"" --csv ""c:\temp\out""" + "\r\n\t " +
        @"   SQLECmd.exe -d ""C:\Temp\"" --hunt --csv ""c:\temp\out""" + "\r\n\t " +
        @"   SQLECmd.exe -d ""C:\Temp\"" --csv ""c:\temp\out"" --ifn" + "\r\n\t " +
        "\r\n\t" +
        "    Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes";

    private static readonly List<string> ProcessedFiles = new();

    private static readonly HashSet<string> UnmatchedDbs = new(StringComparer.InvariantCultureIgnoreCase);
    private static readonly HashSet<string> SeenHashes = new(StringComparer.InvariantCultureIgnoreCase);

    //files that could not be processed because they are not SQLite or are damaged: listed, but the exit code stays 0
    private static int _failedFiles;

    //files or folders that could not be read (access denied, locked, IO errors): exit code 1
    private static int _accessErrors;

    //CSV/JSON/blob output that could not be written (exit code 1)
    private static int _exportErrors;

    //map queries that failed on a database (a damaged database, or one the map does not fit): listed, exit code stays 0
    private static int _queryErrors;

    /// <summary>A file or folder that could not be read (exit code 1), as opposed to one whose contents are bad (0)</summary>
    private static bool IsAccessError(Exception e)
    {
        return e is IOException or UnauthorizedAccessException || e.InnerException is IOException or UnauthorizedAccessException;
    }

    /// <summary>true when the file can be opened for reading (as SqLiteFile.IsSqLiteFile opens it)</summary>
    private static bool CanOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Files under a folder, recursively, matching a pattern (case-insensitive). Folders that can't be read are skipped
    ///     and counted (IgnoreInaccessible skipped them silently)
    /// </summary>
    private sealed class EntryFinder : System.IO.Enumeration.FileSystemEnumerator<string>
    {
        private readonly string _pattern;

        //the folder whose handle is opened right after ShouldRecurseIntoEntry returns true: when that fails, ContinueOnError
        //is called before ShouldIncludeEntry for the same entry. Null: the folder being searched (the root, when the base
        //constructor can't open it; field initializers run before the base constructor, so Unreadable is there)
        private string _lastRecursed;

        /// <summary>Folders that could not be read (null: the root folder) and why</summary>
        public readonly List<(string Folder, string Reason)> Unreadable = new();

        public EntryFinder(string directory, string pattern) : base(directory, new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = true, AttributesToSkip = 0 })
        {
            _pattern = pattern;
        }

        protected override bool ShouldRecurseIntoEntry(ref System.IO.Enumeration.FileSystemEntry entry)
        {
            _lastRecursed = entry.ToFullPath();
            return true;
        }

        protected override bool ShouldIncludeEntry(ref System.IO.Enumeration.FileSystemEntry entry)
        {
            _lastRecursed = null;
            //files only: folders were passed on as files, and each one logged "<folder> not found!" (with --hunt, every subfolder)
            return entry.IsDirectory == false && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(_pattern, entry.FileName, true);
        }

        protected override string TransformEntry(ref System.IO.Enumeration.FileSystemEntry entry)
        {
            return entry.ToFullPath();
        }

        protected override bool ContinueOnError(int error)
        {
            Unreadable.Add((_lastRecursed, new System.ComponentModel.Win32Exception(error).Message));
            _lastRecursed = null;
            return true;
        }
    }

    //each folder that can't be read is reported once (without --hunt the folder is searched once per map)
    private static readonly HashSet<string> UnreadableFolders = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> FindEntries(string directory, string pattern)
    {
        using var finder = new EntryFinder(directory, pattern);
        var entries = new List<string>();
        while (finder.MoveNext())
        {
            entries.Add(finder.Current);
        }

        var count = 0;
        foreach (var (folder, reason) in finder.Unreadable)
        {
            if (UnreadableFolders.Add(folder ?? directory))
            {
                Log.Error("Unable to read folder {Folder}: {Reason}", folder ?? directory, reason);
                count += 1;
            }
        }

        if (count > 0)
        {
            _accessErrors += 1;
            Log.Error("Could not access all files in {Directory}: {Count:N0} folder(s) could not be read", directory, count);
        }

        return entries;
    }


    // JSON time stamps: ISO 8601 in UTC (2024-01-02T03:04:05.6780000Z), as JLECmd writes them, instead of ServiceStack's \/Date(ms)\/
    private const string JsonDateFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private static void ConfigureJsonDates()
    {
        JsConfig.DateHandler = DateHandler.ISO8601;
        JsConfig<DateTimeOffset>.SerializeFn = JsonDate;
        JsConfig<DateTimeOffset?>.SerializeFn = d => d.HasValue ? JsonDate(d.Value) : null;
        JsConfig<DateTime>.SerializeFn = JsonDate;
        JsConfig<DateTime?>.SerializeFn = d => d.HasValue ? JsonDate(d.Value) : null;
    }

    private static string JsonDate(DateTimeOffset d)
    {
        return d.UtcDateTime.ToString(JsonDateFormat, CultureInfo.InvariantCulture);
    }

    private static string JsonDate(DateTime d)
    {
        //timestamps without a kind are already UTC; ToUniversalTime would treat them as local time
        if (d.Kind == DateTimeKind.Unspecified)
        {
            d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        }

        return d.ToUniversalTime().ToString(JsonDateFormat, CultureInfo.InvariantCulture);
    }

    private static async Task<int> Main(string[] args)
    {
        ConfigureJsonDates();

        ExceptionlessClient.Default.Startup("DyZCm8aZbNXf2iZ6BV00wY2UoR3U2tymh3cftNZs");

        JsConfig.IncludeNullValues = true;
        JsConfig.IncludeNullValuesInDictionaries = true;
        
        var fOpt = new Option<string>("-f")
        {
            Description = "File to process. Either this or -d is required"
        };
        
        var dOpt = new Option<string>("-d")
        {
            Description = "Directory to recursively process. Either this or -f is required"
        };
        var csvOpt = new Option<string>(
            "--csv")
        {
            Description = "Directory to save CSV formatted results to. Be sure to include the full path in double quotes"
        };
        var jsonOpt = new Option<string>(
            "--json")
        {
            Description = "Directory to save JSON formatted results to. Be sure to include the full path in double quotes"
        };
        var blobdirOpt = new Option<string>(
            "--blobdir")
        {
            Description = "Directory to save blob data to. Be sure to include the full path in double quotes"
        };
        var mapsOpt = new Option<string>(
            "--maps")
        {
            Description = "The path where SQL maps are located. Defaults to 'Maps' folder where program was executed",
            DefaultValueFactory = _ => Path.Combine(BaseDirectory, "Maps")
        };
        
        var dedupeOpt = new Option<bool>("--dedupe")
        {
            Description = "Deduplicate -f or -d files based on SHA-1. First file found wins",
            DefaultValueFactory = _ => true
        };
        var huntOpt = new Option<bool>("--hunt")
        {
            Description = "When true, all files are looked at regardless of name and file header is used to identify SQLite files, else filename in map is used to find databases",
            DefaultValueFactory = _ => true
        };
        
        var debugOpt = new Option<bool>("--debug")
        {
            Description = "Show debug information during processing",
            DefaultValueFactory = _ => false
        };
        var traceOpt = new Option<bool>("--trace")
        {
            Description = "Show trace information during processing",
            DefaultValueFactory = _ => false
        };
        
        var syncOpt = new Option<bool>("--sync")
        {
            Description = "If true, the latest maps from https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps are downloaded and local maps updated",
            DefaultValueFactory = _ => false
        };
        
        var noblobOpt = new Option<bool>("--noblob")
        {
            Description = "If true, disables blob extraction from query results",
            DefaultValueFactory = _ => false
        };

        var tempOpt = new Option<string>("--temp")
        {
            Description = TempFolder.Help
        };

        var ifnOpt = new Option<bool>("--ifn")
        {
            Description = "If true, the name of each database is added to the names of its CSV and blob files, so databases processed with the same map can be told apart",
            DefaultValueFactory = _ => false
        };

        _rootCommand = new RootCommand
        {
           fOpt,
           dOpt,
           csvOpt,
           jsonOpt,
           blobdirOpt,
           noblobOpt,
           ifnOpt,
           dedupeOpt,
           huntOpt,
           mapsOpt,
           tempOpt,
           syncOpt,
           debugOpt,
           traceOpt,
           
         
        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.SetAction(result => DoWork(result.GetValue(fOpt), result.GetValue(dOpt), result.GetValue(csvOpt),
            result.GetValue(jsonOpt), result.GetValue(blobdirOpt), result.GetValue(dedupeOpt), result.GetValue(huntOpt),
            result.GetValue(mapsOpt),result.GetValue(syncOpt),result.GetValue(debugOpt),result.GetValue(traceOpt),result.GetValue(noblobOpt),result.GetValue(ifnOpt),result.GetValue(tempOpt)));
            
        var exitCode = await _rootCommand.Parse(args).InvokeAsync();

        Log.CloseAndFlush();

        return exitCode;
    }

    /// <returns>0 on success, 1 for invalid arguments, map errors or files that could not be processed</returns>
    private static int DoWork(string f, string d, string csv, string json, string blobdir, bool dedupe, bool hunt, string maps, bool sync, bool debug, bool trace, bool noblob, bool ifn, string temp)
    {
        var levelSwitch = new LoggingLevelSwitch();

        _activeDateTimeFormat = "yyyy-MM-dd HH:mm:ss";

        var formatter =
            new DateTimeOffsetFormatter(CultureInfo.CurrentCulture);

        var template = "{Message:lj}{NewLine}{Exception}";

        if (debug)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Debug;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }

        if (trace)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Verbose;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }

        var conf = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: template, formatProvider: formatter)
            .MinimumLevel.ControlledBy(levelSwitch);

        Log.Logger = conf.CreateLogger();


        if (sync)
        {
            var updated = false;
            try
            {
                Log.Information("{Header}", Header);
                updated = Task.Run(UpdateFromRepo).Result;
            }
            catch (Exception e)
            {
                Log.Error(e, "There was an error checking for updates: {Message}", e.GetBaseException().Message);
            }

            Console.WriteLine();
            return updated ? 0 : 1;
        }

        if (string.IsNullOrEmpty(f) &&
            string.IsNullOrEmpty(d))
        {
            var aaa = new CustomHelpAction(new HelpAction());
            aaa.Invoke(_rootCommand.Parse("-f or -d is required. Exiting"));

            
            Console.WriteLine();
            return 1;
        }

        if (string.IsNullOrEmpty(csv) && string.IsNullOrEmpty(json))
        {
            var aaa = new CustomHelpAction(new HelpAction());
            aaa.Invoke(_rootCommand.Parse("--csv or --json is required. Exiting"));

            
            Console.WriteLine();
            return 1;
        }

        Log.Information("{Header}", Header);

        Log.Information("Command line: {Args}", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
        Console.WriteLine();

        try
        {
            NativeSqlite.Setup();
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Unable to load the SQLite library: {Message}. Exiting", e.Message);
            Console.WriteLine();
            return 1;
        }

        var sw = new Stopwatch();
        sw.Start();


        if (Directory.Exists(maps) == false)
        {
            Log.Warning("Maps directory {Maps} does not exist! Database maps will not be loaded!!", maps);
        }
        else
        {
            Log.Debug("Loading maps from {Path}", Path.GetFullPath(maps));
            var errors = SqlMap.LoadMaps(Path.GetFullPath(maps));

            if (errors)
            {
                return 1;
            }

            Log.Information("Maps loaded: {Count:N0}", SqlMap.MapFiles.Count);
        }

        if (!string.IsNullOrEmpty(csv) && Directory.Exists(csv) == false)
        {
            Log.Information("Path to {Csv} doesn't exist. Creating...", csv);

            try
            {
                Directory.CreateDirectory(csv);
            }
            catch (Exception e)
            {
                Log.Fatal(e, "Unable to create directory {Csv}. Does a file with the same name exist? Exiting", csv);
                Console.WriteLine();
                return 1;
            }
        }

        if (!string.IsNullOrEmpty(json) && Directory.Exists(json) == false)
        {
            Log.Information("Path to {Json} doesn't exist. Creating...", json);

            try
            {
                Directory.CreateDirectory(json);
            }
            catch (Exception e)
            {
                Log.Fatal(e, "Unable to create directory {Json}. Does a file with the same name exist? Exiting", json);
                Console.WriteLine();
                return 1;
            }
        }

        if (!string.IsNullOrEmpty(blobdir) && Directory.Exists(blobdir) == false)
        {
            Log.Information("Path to {Blobdir} doesn't exist. Creating...", blobdir);

            try
            {
                Directory.CreateDirectory(blobdir);
            }
            catch (Exception e)
            {
                Log.Fatal(e, "Unable to create directory {Blobdir}. Does a file with the same name exist? Exiting", blobdir);
                Console.WriteLine();
                return 1;
            }
        }

        StreamWriter jsonWriter = null;
        string jsonFullOutName = String.Empty;
        if (!string.IsNullOrEmpty(json))
        {
            var baseTime = DateTimeOffset.UtcNow;
            var jsonOutName = $"{baseTime:yyyyMMddHHmmssffffff}_SQLECmd_Output.json";
            jsonFullOutName = Path.Combine(json, jsonOutName);
            try
            {
                jsonWriter = new StreamWriter(new FileStream(jsonFullOutName, FileMode.CreateNew));
            }
            catch (Exception e)
            {
                Log.Fatal(e, "Unable to create {File}: {Message}. Exiting", jsonFullOutName, e.Message);
                Console.WriteLine();
                return 1;
            }
            Log.Information("\tSaving all JSON results to {FullOutName}", jsonFullOutName);
        }

        if (string.IsNullOrEmpty(f) == false && File.Exists(f) == false)
        {
            Log.Warning("{F} does not exist! Exiting", f);
            Console.WriteLine();
            jsonWriter?.Dispose();
            return 1;
        }

        if (string.IsNullOrEmpty(f) && Directory.Exists(d) == false)
        {
            Log.Warning("Directory {D} does not exist! Exiting", d);
            Console.WriteLine();
            jsonWriter?.Dispose();
            return 1;
        }

        //the databases are read from copies, made in --temp or Temp next to SQLECmd (never the user's %TEMP%)
        _tempFolder = TempFolder.Prepare(temp, out var tempCreated, out var tempError);
        if (_tempFolder == null)
        {
            Log.Error("The folder for the temporary copies of the databases can't be written to ({Error}). Give one with --temp. Exiting", tempError);
            Console.WriteLine();
            jsonWriter?.Dispose();
            return 1;
        }

        try
        {
            ProcessAll(f, d, csv, json, blobdir, dedupe, hunt, noblob, ifn, jsonWriter);
        }
        finally
        {
            TempFolder.RemoveIfEmpty(_tempFolder, tempCreated);
        }

        if (jsonWriter != null)
        {
            jsonWriter.Dispose();
        }

        sw.Stop();

        if (UnmatchedDbs.Any())
        {
            Console.WriteLine();
            Log.Fatal("At least one database was found with no corresponding map (Use {Switch} for more details about discovery process)", "--debug");

            foreach (var unmatchedDb in UnmatchedDbs)
            {
                try
                {
                    DumpUnmatched(unmatchedDb, tempCreated);
                }
                catch (Exception e)
                {
                    Log.Information("\tFile name: {UnmatchedDb}, Tables: (unable to read: {Message})", unmatchedDb, e.Message);
                }
            }
        }

        Console.WriteLine();

        if (ProcessedFiles.Count == 1)
        {
            Log.Information("Processed {Count:N0} file in {TotalSeconds:N4} seconds", ProcessedFiles.Count, sw.Elapsed.TotalSeconds);
        }
        else
        {
            Log.Information("Processed {Count:N0} files in {TotalSeconds:N4} seconds", ProcessedFiles.Count, sw.Elapsed.TotalSeconds);
        }


        Console.WriteLine();

        //exit code 1 only when SQLECmd could not do its job (a file or folder it could not read, output it could not
        //write); files that are not SQLite or are damaged, and queries that fail on a database, are listed but give 0
        if (_failedFiles + _accessErrors > 0)
        {
            Log.Warning("{Count:N0} file(s) could not be processed. See the errors above", _failedFiles + _accessErrors);
            Console.WriteLine();
        }

        if (_exportErrors + _queryErrors > 0)
        {
            Log.Warning("{Count:N0} query/export error(s). See the errors above", _exportErrors + _queryErrors);
            Console.WriteLine();
        }

        return _accessErrors > 0 || _exportErrors > 0 ? 1 : 0;
    }

    /// <summary>-f, or the files under -d</summary>
    private static void ProcessAll(string f, string d, string csv, string json, string blobdir, bool dedupe, bool hunt, bool noblob, bool ifn, StreamWriter jsonWriter)
    {
        if (string.IsNullOrEmpty(f) == false)
        {
            //a file given with -f must be a database (under -d, other files are skipped)
            if (SqLiteFile.IsSqLiteFile(f) == false)
            {
                if (CanOpen(f) == false)
                {
                    //IsSqLiteFile logged why it could not be read
                    _accessErrors += 1;
                    return;
                }

                _failedFiles += 1;
                Log.Error("{File} is not a SQLite file", f);
                return;
            }

            try
            {
                ProcessFile(Path.GetFullPath(f), hunt, dedupe, csv, json, blobdir, jsonWriter, noblob, ifn);
            }
            catch (Exception e)
            {
                if (IsAccessError(e))
                {
                    _accessErrors += 1;
                }
                else
                {
                    _failedFiles += 1;
                }

                Log.Error(e, "Error processing {File}: {Message}", f, e.Message);
            }
        }
        else
        {
            //Directories
            Log.Information("Looking for files in {D}", d);
            Console.WriteLine();

            var files = new List<string>();

            IEnumerable<string> files2;

            if (hunt)
            {
                files2 = FindEntries(d, "*");

                files.AddRange(files2);
            }
            else
            {
                foreach (var mapFile in SqlMap.MapFiles)
                {
                    //search here
                    files2 = FindEntries(d, mapFile.Value.FileName);

                    files.AddRange(files2);
                }
            }

            foreach (var file in files)
            {
                try
                {
                    ProcessFile(file, hunt, dedupe, csv, json, blobdir, jsonWriter, noblob, ifn);
                }
                catch (Exception e)
                {
                    if (IsAccessError(e))
                    {
                        _accessErrors += 1;
                    }
                    else
                    {
                        _failedFiles += 1;
                    }

                    Log.Error(e, "Error processing {File}: {Message}", file, e.Message);
                }
            }
        }
    }

    /// <summary>
    ///     DATETIME values are read as UTC: otherwise System.Data.SQLite converts a value with a zone (2024-01-02T03:04:05Z)
    ///     to the local time of the machine running SQLECmd and leaves its kind unspecified. The builder also quotes paths
    ///     with a ; in them, which a plain Data Source=path does not
    /// </summary>
    private static string ConnectionString(string file)
    {
        return new System.Data.SQLite.SQLiteConnectionStringBuilder { DataSource = file, DateTimeKind = DateTimeKind.Utc }.ToString();
    }

    private static void DumpUnmatched(string unmatchedDb, bool tempCreated)
    {
        //from a copy too (the temporary folder may be gone: it is made again, and removed again when empty)
        Directory.CreateDirectory(_tempFolder);
        var copy = DatabaseCopy.Create(unmatchedDb, _tempFolder);
        try
        {
            DumpTables(unmatchedDb, copy.Path);
        }
        finally
        {
            copy.Dispose();
            TempFolder.RemoveIfEmpty(_tempFolder, tempCreated);
        }
    }

    private static void DumpTables(string unmatchedDb, string copyPath)
    {
        var dbFactory = new OrmLiteConnectionFactory(ConnectionString(copyPath), SqliteDialect.Provider);

        using var db = dbFactory.Open();
        Log.Verbose("\tGetting table names for {UnmatchedDb}", unmatchedDb);
        using var reader = db.ExecuteReader("SELECT name FROM sqlite_master WHERE type='table' order by name");

        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader[0].ToString());
        }

        Log.Information("\tFile name: {UnmatchedDb}, Tables: {Tables}", unmatchedDb, string.Join(",", tables));
    }


    private static string SanitizeFilenamePart(string input, int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string sanitized = input;
        char[] invalidChars = Path.GetInvalidFileNameChars();
        
        foreach (char invalidChar in invalidChars)
        {
            sanitized = sanitized.Replace(invalidChar.ToString(), "_");
        }

        sanitized = sanitized.Replace(" ", "_");

        sanitized = sanitized.Trim('_');

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength);
        }
        sanitized = sanitized.Trim('_');

        if (string.IsNullOrWhiteSpace(sanitized.Replace("_", "")))
        {
            return null;
        }

        return sanitized;
    }

    private static void ProcessFile(string fileName, bool hunt, bool dedupe, string csv, string json, string blobdir, StreamWriter jsonWriter, bool noblob, bool ifn)
    {
        Log.Debug("Checking if {FileName} is a SQLite file", fileName);
        if (SqLiteFile.IsSqLiteFile(fileName) == false)
        {
            if (File.Exists(fileName) && CanOpen(fileName) == false)
            {
                //IsSqLiteFile logged why it could not be read
                _accessErrors += 1;
                return;
            }

            if (hunt == false)
            {
                Log.Warning("\t{FileName} is not a SQLite file! Skipping...", fileName);
            }
            else
            {
                Log.Debug("\t{FileName} is not a SQLite file! Skipping...", fileName);
            }

            return;
        }

        Log.Debug("{FileName} is a SQLite file!", fileName);

        if (dedupe)
        {
            string newSha;
            using (var s1 = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                newSha = Helper.GetSha1FromStream(s1, 0);
            }


            if (SeenHashes.Contains(newSha))
            {
                Log.Warning("Skipping {FileName} as a file with SHA-1 {Sha} has already been processed", fileName, newSha);
                Console.WriteLine();
                return;
            }

            Log.Debug("Adding {FileName} SHA-1 {Sha} to seen hashes collection", fileName, newSha);
            SeenHashes.Add(newSha);
        }

        Log.Information("Processing {FileName}...", fileName);

        ProcessedFiles.Add(fileName);

        List<MapFile> maps;

        if (hunt)
        {
            maps = SqlMap.MapFiles.Values.ToList();
        }
        else
        {
            //only find maps that match the db filename
            maps = SqlMap.MapFiles.Values.Where(t => string.Equals(t.FileName, Path.GetFileName(fileName),
                StringComparison.InvariantCultureIgnoreCase)).ToList();
        }

        var foundMap = false;

        //--ifn (#54): the database's name at the end of the output names, as in _notplaces.sqlite.csv
        var dbNamePart = ifn ? $"_{SanitizeFilenamePart(Path.GetFileName(fileName), 100) ?? "db"}" : string.Empty;

        //only a copy is opened (SQLite opens read-write and would checkpoint a -wal into the evidence); disposed after db
        using var copy = DatabaseCopy.Create(fileName, _tempFolder);
        var dbFactory = new OrmLiteConnectionFactory(ConnectionString(copy.Path), SqliteDialect.Provider);

        using var db = dbFactory.Open();

        var identifyErrors = 0;
        Exception identifyError = null;

        //need to run thru each map and see if we get any IdentityQuery matches
        //process each one that matches
        foreach (var map in maps)
        {
            Log.Debug("Processing map {Description} with Id {Id}", map.Description, map.Id);

            var baseTime = DateTimeOffset.UtcNow;

            Log.Debug("\tVerifying database via {IdentifyQuery}", map.IdentifyQuery);

            string id;
            try
            {
                id = db.ExecuteScalar<string>(map.IdentifyQuery);
            }
            catch (Exception e)
            {
                identifyErrors += 1;
                identifyError = e;
                Log.Write(hunt ? LogEventLevel.Debug : LogEventLevel.Warning,
                    "\tIdentifyQuery for map {Description} with Id {Id} failed: {Message}", map.Description, map.Id, e.Message);
                continue;
            }

            if (string.Equals(id, map.IdentifyValue, StringComparison.InvariantCultureIgnoreCase) == false)
            {
                //when hunting, every map is tried against every database, so a mismatch is normal
                Log.Write(hunt ? LogEventLevel.Debug : LogEventLevel.Warning,
                    "\tFor map w/ description {Description}, got value {Id} from IdentityQuery, but expected {IdentifyValue}. Queries will not be processed!", map.Description, id, map.IdentifyValue);
                continue;
            }

            //if we find any matches, its not unmatched anymore
            foundMap = true;

            Log.Information("\tMap queries found: {Count:N0}. Processing queries...", map.Queries.Count);
            foreach (var queryInfo in map.Queries)
            {
                Log.Debug("Processing query {Name}", queryInfo.Name);

                try
                {
                    var results = db.Query<dynamic>(queryInfo.Query).ToList();

                    if (queryInfo.BlobColumns != null && queryInfo.BlobColumns.Any() && !noblob)
                    {
                        string outDir = null;
                        if (!string.IsNullOrEmpty(blobdir))
                        {
                            outDir = blobdir;
                        }
                        if (!string.IsNullOrEmpty(csv) && string.IsNullOrEmpty(outDir))
                        {
                            outDir = csv;
                        }
                        else if (!string.IsNullOrEmpty(json) && string.IsNullOrEmpty(outDir))
                        {
                            outDir = json;
                        }

                        Log.Information("\tProcessing blobs for query {Name} into directory {OutDir}", queryInfo.Name, outDir);
                        var blobCounter = 0;
                        foreach (var result in results)
                        {
                            foreach (var blobConfig in queryInfo.BlobColumns)
                            {
                                try
                                {
                                    var blobData = ((IDictionary<string, object>)result)[blobConfig.BlobColumn] as byte[];

                                    if (blobData != null && blobData.Length > 0)
                                    {
                                        blobCounter++;
                                        string namePart = null;

                                        if (!string.IsNullOrEmpty(blobConfig.NameColumn))
                                        {
                                            if (((IDictionary<string, object>)result).TryGetValue(blobConfig.NameColumn, out var nameValueRaw) && nameValueRaw != null)
                                            {
                                                string nameValueString = nameValueRaw.ToString();
                                                namePart = SanitizeFilenamePart(nameValueString);
                                            }
                                        }

                                        var uniqueIdentifier = namePart ?? blobCounter.ToString();

                                        string finalExtension = ".blob";
                                        if (!string.IsNullOrEmpty(blobConfig.BlobExtension))
                                        {
                                            finalExtension = "." + blobConfig.BlobExtension;
                                        }

                                        var blobFileNameWithoutExt = $"{baseTime:yyyyMMddHHmmssffffff}_{map.CSVPrefix}_{queryInfo.BaseFileName}_{map.Id}_{uniqueIdentifier}_{blobConfig.BlobColumn}{dbNamePart}";
                                        var blobFileNameWithExt = $"{blobFileNameWithoutExt}{finalExtension}";

                                        var fullBlobPath = Path.Combine(outDir, blobFileNameWithExt);

                                        File.WriteAllBytes(fullBlobPath, blobData);
                                        Log.Information("	Dumping blob from column {BlobColumn} to {FullBlobPath}", blobConfig.BlobColumn, fullBlobPath);
                                    }
                                }
                                catch (KeyNotFoundException)
                                {
                                    Log.Warning("	Data column {BlobColumn} or Name column {NameColumn} not found in query {Name} results.", blobConfig.BlobColumn, blobConfig.NameColumn, queryInfo.Name);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "	Error processing blob column {BlobColumn} for query {Name}: {Message}", blobConfig.BlobColumn, queryInfo.Name, ex.Message);
                                    _exportErrors += 1;
                                }
                            }
                        }
                    }
                    
                    if (queryInfo.BlobColumns != null && queryInfo.BlobColumns.Any())
                    {
                        var blobColumnsForQuery = queryInfo.BlobColumns.ToDictionary(bc => bc.BlobColumn, StringComparer.OrdinalIgnoreCase);

                        foreach (var result in results)
                        {
                            var resultDict = (IDictionary<string, object>)result;
                            var blobKeys = resultDict.Keys
                                .Where(key => blobColumnsForQuery.ContainsKey(key) && resultDict[key] is byte[])
                                .ToList();

                            foreach (var key in blobKeys)
                            {
                                resultDict.Remove(key);
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(csv))
                    {
                        var outName =
                            $"{baseTime:yyyyMMddHHmmssffffff}_{map.CSVPrefix}_{queryInfo.BaseFileName}_{map.Id}{dbNamePart}.csv";

                        var fullOutName = Path.Combine(csv, outName);

                        if (results.Any() == false)
                        {
                            Log.Warning("\t {Name} did not return any results. CSV will not be saved", queryInfo.Name);
                            continue;
                        }

                        Log.Information("\tDumping {Name} to {FullOutName}", queryInfo.Name, fullOutName);

                        using var writer = new StreamWriter(new FileStream(fullOutName, FileMode.CreateNew));
                        using var csvWriter = new CsvHelper.CsvWriter(writer, CultureInfo.InvariantCulture);

                        results.First().SourceFile = fileName;
                        csvWriter.WriteDynamicHeader(results.First());
                        csvWriter.NextRecord();

                        foreach (var result in results)
                        {
                            result.SourceFile = fileName;
                            csvWriter.WriteRecord(result);
                            csvWriter.NextRecord();
                        }

                        csvWriter.Flush();
                        writer.Flush();
                    }
                    if (jsonWriter != null)
                    {
                        if (results.Any() == false)
                        {
                            Log.Warning("\t {Name} did not return any results. JSON will not be saved", queryInfo.Name);
                            continue;
                        }

                        foreach (var result in results)
                        {
                            result.SourceFile = fileName;
                            result.MapDescription = map.Description;
                            var jsonLine = JsonSerializer.SerializeToString(result);
                            jsonWriter.WriteLine(jsonLine);
                        }
                        jsonWriter.Flush();
                    }
                }
                catch (Exception e)
                {
                    Log.Fatal(e, "Error processing map {Description} with Id {Id} for query {Name}", map.Description, map.Id, queryInfo.Name);
                    Log.Error("\t{Replace}", e.Message.Replace("\r\n", "\r\n\t"));
                    //output that can't be written is a failure; a query that fails on this database is not
                    if (IsAccessError(e))
                    {
                        _exportErrors += 1;
                    }
                    else
                    {
                        _queryErrors += 1;
                    }
                }
            }
        }

        if (maps.Count > 0 && identifyErrors == maps.Count)
        {
            //not one IdentifyQuery could run, so the database itself cannot be read (corrupt, encrypted, ...)
            throw new InvalidDataException($"Unable to read database: {identifyError.Message}", identifyError);
        }

        if (foundMap == false)
        {
            Log.Information("\tNo maps found for {FileName}. Adding to unmatched database list", fileName);
            UnmatchedDbs.Add(fileName);
        }

        Console.WriteLine();
    }

    private static async Task<bool> UpdateFromRepo()
    {
        Console.WriteLine();

        Log.Information(
            "Checking for updated maps at {Url}...", "https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps");
        Console.WriteLine();
        var archivePath = Path.Combine(BaseDirectory, "____master.zip");

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }


        using (var client = new HttpClient())
        {
            try
            {
                var response = await client.GetAsync("https://github.com/EricZimmerman/SQLECmd/archive/master.zip");

                if (response.IsSuccessStatusCode == false)
                {
                    Log.Error("Unable to download maps: {Status} {Reason}", (int)response.StatusCode, response.ReasonPhrase);
                    return false;
                }

                var contentStream = await response.Content.ReadAsStreamAsync();

                using (var fileStream = File.Create(archivePath))
                {
                    await contentStream.CopyToAsync(fileStream);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Unable to download maps: {Message}", ex.GetBaseException().Message);
                return false;
            }
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

        var newMapPath = Path.Combine(BaseDirectory, "SQLECmd-master", "SQLMap", "Maps");

        var orgMapPath = Path.Combine(BaseDirectory, "Maps");

        if (Directory.Exists(orgMapPath) == false)
        {
            Directory.CreateDirectory(orgMapPath);
        }

        if (Directory.Exists(newMapPath) == false)
        {
            Log.Error("The downloaded archive does not contain {Path}", "SQLMap/Maps");
            return false;
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

                string newSha;
                string destSha;
                using (var s1 = File.OpenRead(newMap))
                using (var s2 = File.OpenRead(dest))
                {
                    newSha = Helper.GetSha1FromStream(s1, 0);
                    destSha = Helper.GetSha1FromStream(s2, 0);
                }

                if (newSha != destSha)
                {
                    //updated file
                    updatedlocalMaps.Add(mName);
                }
            }

            File.Copy(newMap, dest, true);
        }

        if (newlocalMaps.Count > 0 || updatedlocalMaps.Count > 0)
        {
            Log.Information("Updates found!");
            Console.WriteLine();

            if (newlocalMaps.Count > 0)
            {
                Log.Information("New maps");
                foreach (var newLocalMap in newlocalMaps)
                {
                    Log.Information("{Path}", Path.GetFileNameWithoutExtension(newLocalMap));
                }

                Console.WriteLine();
            }

            if (updatedlocalMaps.Count > 0)
            {
                Log.Information("Updated maps");
                foreach (var um in updatedlocalMaps)
                {
                    Log.Information("{Path}", Path.GetFileNameWithoutExtension(um));
                }

                Console.WriteLine();
            }

            Console.WriteLine();
        }
        else
        {
            Console.WriteLine();
            Log.Information("No new maps available");
            Console.WriteLine();
        }

        Directory.Delete(Path.Combine(BaseDirectory, "SQLECmd-master"), true);

        return true;
    }

    private class DateTimeOffsetFormatter : IFormatProvider, ICustomFormatter
    {
        private readonly IFormatProvider _innerFormatProvider;

        public DateTimeOffsetFormatter(IFormatProvider innerFormatProvider)
        {
            _innerFormatProvider = innerFormatProvider;
        }

        public string Format(string format, object arg, IFormatProvider formatProvider)
        {
            if (arg is DateTimeOffset)
            {
                var size = (DateTimeOffset)arg;
                return size.ToString(_activeDateTimeFormat);
            }

            var formattable = arg as IFormattable;
            if (formattable != null)
            {
                return formattable.ToString(format, _innerFormatProvider);
            }

            return arg.ToString();
        }

        public object GetFormat(Type formatType)
        {
            return formatType == typeof(ICustomFormatter) ? this : _innerFormatProvider.GetFormat(formatType);
        }
    }
    private class CustomHelpAction : SynchronousCommandLineAction
    {
        private readonly HelpAction _defaultHelp;

        public CustomHelpAction(HelpAction action)
        {
            _defaultHelp = action;
        }

        public override int Invoke(ParseResult parseResult)
        {
            var result = _defaultHelp.Invoke(parseResult);

            Log.Warning("{Msg}", string.Join(" ",parseResult.Tokens));

            return result;
        }
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
