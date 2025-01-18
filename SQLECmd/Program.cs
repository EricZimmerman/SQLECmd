
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
#if NET6_0_OR_GREATER
using System.Runtime.InteropServices;
#endif
using CsvHelper;
using Exceptionless;
using ICSharpCode.SharpZipLib.Zip;
using RawCopy;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Dapper;
using SQLECmd.Properties;
using SQLMaps;
#if NET462
using Alphaleonis.Win32.Filesystem;
using Alphaleonis.Win32.Security;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;
#else
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;
#endif

namespace SQLECmd;

internal class Program
{
    private static string _activeDateTimeFormat;
    private static RootCommand _rootCommand;
    private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    private static readonly string Header =
        $"SQLECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/SQLECmd";

    private static readonly string Footer =
        @"Examples: SQLECmd.exe -f ""C:\Temp\someFile.db"" --csv ""c:\temp\out"" " +
        "\r\n\t " +
        @"   SQLECmd.exe -d ""C:\Temp\"" --csv ""c:\temp\out""" + "\r\n\t " +
        @"   SQLECmd.exe -d ""C:\Temp\"" --hunt --csv ""c:\temp\out""" + "\r\n\t " +
        "\r\n\t" +
        "    Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes";

    private static readonly List<string> ProcessedFiles = new();

    private static readonly HashSet<string> UnmatchedDbs = new(StringComparer.InvariantCultureIgnoreCase);
    private static readonly HashSet<string> SeenHashes = new(StringComparer.InvariantCultureIgnoreCase);


    private static async Task Main(string[] args)
    {
        ExceptionlessClient.Default.Startup("DyZCm8aZbNXf2iZ6BV00wY2UoR3U2tymh3cftNZs");

        _rootCommand = new RootCommand
        {
            new Option<string>(
                "-f",
                "File to process. This or -d is required"),
            new Option<string>(
                "-d",
                "Directory to process that contains SQLite files. This or -f is required"),
            new Option<string>(
                "--csv",
                "Directory to save CSV formatted results to"),
            new Option<string>(
                "--json",
                "Directory to save JSON formatted results to"),

            new Option<bool>(
                "--dedupe",
                () => true,
                "Deduplicate -f or -d files based on SHA-1. First file found wins"),

            new Option<bool>(
                "--hunt",
                () => false,
                "When true, all files are looked at regardless of name and file header is used to identify SQLite files, else filename in map is used to find databases"),

            new Option<string>(
                "--maps",
                () => Path.Combine(BaseDirectory, "Maps"),
                "The path where event maps are located. Defaults to 'Maps' folder where program was executed"),

            new Option<bool>(
                "--sync",
                () => false,
                "If true, the latest maps from https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps are downloaded and local maps updated"),

            new Option<bool>(
                "--debug",
                () => false,
                "Show debug information during processing"),

            new Option<bool>(
                "--trace",
                () => false,
                "Show trace information during processing")
        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.Handler = CommandHandler.Create(DoWork);

        await _rootCommand.InvokeAsync(args);

        Log.CloseAndFlush();
    }

    private static void DoWork(string f, string d, string csv, string json, bool dedupe, bool hunt, string maps, bool sync, bool debug, bool trace)
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
            try
            {
                Log.Information("{Header}", Header);
                UpdateFromRepo();
            }
            catch (Exception e)
            {
                Log.Error(e, "There was an error checking for updates: {Message}", e.Message);
            }

            Console.WriteLine();
            Environment.Exit(0);
        }

        if (string.IsNullOrEmpty(f) &&
            string.IsNullOrEmpty(d))
        {
            var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
            var hc = new HelpContext(helpBld, _rootCommand, Console.Out);

            helpBld.Write(hc);

            Log.Warning("-f or -d is required. Exiting");
            Console.WriteLine();
            return;
        }

        if (string.IsNullOrEmpty(csv))
        {
            var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
            var hc = new HelpContext(helpBld, _rootCommand, Console.Out);

            helpBld.Write(hc);

            Log.Warning("--csv is required. Exiting");
            Console.WriteLine();
            return;
        }

        Log.Information("{Header}", Header);

        Log.Information("Command line: {Args}", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
        Console.WriteLine();

        DumpSqliteDll();

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
                return;
            }

            Log.Information("Maps loaded: {Count:N0}", SqlMap.MapFiles.Count);
        }

        if (Directory.Exists(csv) == false)
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
                return;
            }
        }

        if (string.IsNullOrEmpty(f) == false)
        {
            if (File.Exists(f) == false)
            {
                Log.Warning("{F} does not exist! Exiting", f);
                Console.WriteLine();
                return;
            }

            ProcessFile(Path.GetFullPath(f), hunt, dedupe, csv);
        }
        else
        {
            //Directories
            Log.Information("Looking for files in {D}", d);
            Console.WriteLine();

            var files = new List<string>();

#if NET462
            Privilege[] privs = {Privilege.EnableDelegation, Privilege.Impersonate, Privilege.Tcb};
            using var enabler = new PrivilegeEnabler(Privilege.Backup, privs);
            var dirEnumOptions =
                DirectoryEnumerationOptions.Files | DirectoryEnumerationOptions.Recursive |
                DirectoryEnumerationOptions.SkipReparsePoints | DirectoryEnumerationOptions.ContinueOnException |
                DirectoryEnumerationOptions.BasicSearch;

            var directoryEnumerationFilters = new DirectoryEnumerationFilters
            {
                RecursionFilter = entryInfo => !entryInfo.IsMountPoint && !entryInfo.IsSymbolicLink,
                ErrorFilter = (_, errorMessage, pathProcessed) => true
            };

            var dbNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            if (hunt)
            {
                directoryEnumerationFilters.InclusionFilter = _ => true;
            }
            else
            {
                foreach (var mapFile in SqlMap.MapFiles)
                {
                    dbNames.Add(mapFile.Value.FileName);
                }

                directoryEnumerationFilters.InclusionFilter = fsei => dbNames.Contains(fsei.FileName);
            }

            files =
                Directory.EnumerateFileSystemEntries(Path.GetFullPath(d), dirEnumOptions, directoryEnumerationFilters).ToList();
#else
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseSensitive,
                RecurseSubdirectories = true,
                AttributesToSkip = 0
            };

            IEnumerable<string> files2;

            if (hunt)
            {
                files2 = Directory.EnumerateFileSystemEntries(d, "*", enumerationOptions);

                files.AddRange(files2);
            }
            else
            {
                foreach (var mapFile in SqlMap.MapFiles)
                {
                    //search here
                    files2 = Directory.EnumerateFileSystemEntries(d, mapFile.Value.FileName, enumerationOptions);

                    files.AddRange(files2);
                }
            }

#endif
            foreach (var file in files)
            {
                try
                {
                    ProcessFile(file, hunt, dedupe, csv);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error processing {File}: {Message}", file, e.Message);
                }
            }
        }

        sw.Stop();

        if (UnmatchedDbs.Any())
        {
            Console.WriteLine();
            Log.Fatal("At least one database was found with no corresponding map (Use {Switch} for more details about discovery process)", "--debug");

            foreach (var unmatchedDb in UnmatchedDbs)
            {
                DumpUnmatched(unmatchedDb);
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

#if NET6_0_OR_GREATER
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)){ 
        if (!File.Exists("libSQLite.Interop.so"))
        {
            return;
        }

        try
        {
            File.Delete("libSQLite.Interop.so");
        }
        catch (Exception)
        {
            Log.Warning("Unable to delete {File}. Delete manually if needed", "libSQLite.Interop.so");
            Console.WriteLine();
        }
        } else {
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
            Log.Warning("Unable to delete {File}. Delete manually if needed", "SQLite.Interop.dll");
            Console.WriteLine();
        }
        }
#else
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
            Log.Warning("Unable to delete {File}. Delete manually if needed", "SQLite.Interop.dll");
            Console.WriteLine();
        }
#endif        
    }

    private static void DumpUnmatched(string unmatchedDb)
    {
        var dbFactory = new OrmLiteConnectionFactory($"{unmatchedDb}", SqliteDialect.Provider);

        using var db = dbFactory.Open();
        Log.Verbose("\tGetting table names for {UnmatchedDb}", unmatchedDb);
        var reader = db.ExecuteReader("SELECT name FROM sqlite_master WHERE type='table' order by name");

        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader[0].ToString());
        }

        Log.Information("\tFile name: {UnmatchedDb}, Tables: {Tables}", unmatchedDb, string.Join(",", tables));
    }

    private static void DumpSqliteDll()
    {
#if NET6_0_OR_GREATER
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)){
        var sqllitefile = "libSQLite.Interop.so"; 

        if (Environment.Is64BitProcess)
        {
            File.WriteAllBytes(sqllitefile, Resources.x64SQLite_Interop_linux);
        }
        else
        {
            //32 Bit Not Tested on Linux
            //File.WriteAllBytes(sqllitefile, Resources.x86SQLite_Interop_linux);
        }
        } else {

        var sqllitefile = "SQLite.Interop.dll";

        if (Environment.Is64BitProcess)
        {
            File.WriteAllBytes(sqllitefile, Resources.x64SQLite_Interop);
        }
        else
        {
            File.WriteAllBytes(sqllitefile, Resources.x86SQLite_Interop);
        }
        }
#else               
        var sqllitefile = "SQLite.Interop.dll";

        if (Environment.Is64BitProcess)
        {
            File.WriteAllBytes(sqllitefile, Resources.x64SQLite_Interop);
        }
        else
        {
            File.WriteAllBytes(sqllitefile, Resources.x86SQLite_Interop);
        }
#endif        
    }

    private static void ProcessFile(string fileName, bool hunt, bool dedupe, string csv)
    {
        Log.Debug("Checking if {FileName} is a SQLite file", fileName);
        if (SqLiteFile.IsSqLiteFile(fileName) == false)
        {
            if (hunt == false)
            {
                Log.Warning("\t{FileName} is not a SQLite file! Skipping...", fileName);
            }
            else
            {
                Log.Warning("\t{FileName} is not a SQLite file! Skipping...", fileName);
            }

            return;
        }

        Log.Debug("{FileName} is a SQLite file!", fileName);

        if (dedupe)
        {
            var s1 = new StreamReader(fileName);
            var newSha = Helper.GetSha1FromStream(s1.BaseStream, 0);


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

        //need to run thru each map and see if we get any IdentityQuery matches
        //process each one that matches
        foreach (var map in maps)
        {
            Log.Debug("Processing map {Description} with Id {Id}", map.Description, map.Id);

            var dbFactory = new OrmLiteConnectionFactory($"{fileName}", SqliteDialect.Provider);

            var baseTime = DateTimeOffset.UtcNow;

            using var db = dbFactory.Open();
            Log.Debug("\tVerifying database via {IdentifyQuery}", map.IdentifyQuery);

            var id = db.ExecuteScalar<string>(map.IdentifyQuery);

            if (string.Equals(id, map.IdentifyValue, StringComparison.InvariantCultureIgnoreCase) == false)
            {
                Log.Warning("\tFor map w/ description {Description}, got value {Id} from IdentityQuery, but expected {IdentifyValue}. Queries will not be processed!", map.Description, id, map.IdentifyValue);
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

                    var outName =
                        $"{baseTime:yyyyMMddHHmmssffffff}_{map.CSVPrefix}_{queryInfo.BaseFileName}_{map.Id}.csv";

                    var fullOutName = Path.Combine(csv, outName);

                    if (results.Any() == false)
                    {
                        Log.Warning("\t {Name} did not return any results. CSV will not be saved", queryInfo.Name);
                        continue;
                    }

                    Log.Information("\tDumping {Name} to {FullOutName}", queryInfo.Name, fullOutName);

                    using var writer = new StreamWriter(new FileStream(fullOutName, FileMode.CreateNew));
                    using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);

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
                catch (Exception e)
                {
                    Log.Fatal(e, "Error processing map {Description} with Id {Id} for query {Name}", map.Description, map.Id, queryInfo.Name);
                    Log.Error("\t{Replace}", e.Message.Replace("\r\n", "\r\n\t"));
                }
            }
        }

        if (foundMap == false)
        {
            Log.Information("\tNo maps found for {FileName}. Adding to unmatched database list", fileName);
            UnmatchedDbs.Add(fileName);
        }

        Console.WriteLine();
    }

    private static void UpdateFromRepo()
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

        var newMapPath = Path.Combine(BaseDirectory, "SQLECmd-master", "SQLMap", "Maps");

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

                var s1 = new StreamReader(newMap);
                var newSha = Helper.GetSha1FromStream(s1.BaseStream, 0);

                var s2 = new StreamReader(dest);

                var destSha = Helper.GetSha1FromStream(s2.BaseStream, 0);

                s2.Close();
                s1.Close();

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