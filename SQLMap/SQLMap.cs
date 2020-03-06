using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Alphaleonis.Win32.Filesystem;
using CsvHelper;
using FluentValidation.Results;
using NLog;
using ServiceStack;
using ServiceStack.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using CsvWriter = CsvHelper.CsvWriter;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;


namespace SQLMaps
{
    public class SQLMap
    {
        public static Dictionary<string, MapFile> MapFiles { get; private set; } =
            new Dictionary<string, MapFile>();

        private static bool DisplayValidationResults(ValidationResult result, string source)
        {
            var l = LogManager.GetLogger("LoadMaps");
            l.Trace($"Performing validation on '{source}': {result.Dump()}");
            if (result.Errors.Count == 0)
            {
                return true;
            }

            Console.WriteLine();
            l.Error($"{source} had validation errors:");

            //   _loggerCopyLog.Error($"\r\n{source} had validation errors:");

            foreach (var validationFailure in result.Errors)
            {
                l.Error(validationFailure);
            }

            Console.WriteLine();
            l.Error("\r\nCorrect the errors and try again. Exiting");
            //   _loggerCopyLog.Error("Correct the errors and try again. Exiting");

            return false;
        }

        public static bool LoadMaps(string mapPath)
        {
            MapFiles = new Dictionary<string, MapFile>();

            var f = new DirectoryEnumerationFilters
            {
                InclusionFilter = entry => entry.Extension.ToUpperInvariant() == ".SMAP",
                RecursionFilter = null,
                ErrorFilter = (errorCode, errorMessage, pathProcessed) => true
            };

            var dirEnumOptions =
                DirectoryEnumerationOptions.Files |
                DirectoryEnumerationOptions.SkipReparsePoints | DirectoryEnumerationOptions.ContinueOnException |
                DirectoryEnumerationOptions.BasicSearch;

            var mapFiles =
                Directory.EnumerateFileSystemEntries(mapPath, dirEnumOptions, f).ToList();

            var l = LogManager.GetLogger("LoadMaps");

            var deserializer = new DeserializerBuilder()
                .Build();

            var validator = new MapFileMapValidator();

            var errorMaps = new List<string>();

            foreach (var mapFile in mapFiles.OrderBy(t => t))
            {
                try
                {
                    var mf = deserializer.Deserialize<MapFile>(File.ReadAllText(mapFile));

                    l.Trace(mf.Dump());

                    var validate = validator.Validate(mf);

                    if (DisplayValidationResults(validate, mapFile))
                    {
                        if (MapFiles.ContainsKey(
                                $"{mf.Id.ToUpperInvariant()}") == false)
                        {
                            l.Debug($"'{Path.GetFileName(mapFile)}' is valid. Id: '{mf.Id}'. Adding to maps...");
                            MapFiles.Add($"{mf.Id.ToUpperInvariant()}",
                                mf);
                        }
                        else
                        {
                            l.Warn(
                                $"A map with Id '{mf.Id}' already exists (File name: '{mf.FileName}'). Map '{Path.GetFileName(mapFile)}' will be skipped");
                        }
                    }
                    else
                    {
                        errorMaps.Add(Path.GetFileName(mapFile));
                    }
                }
                catch (YamlDotNet.Core.SyntaxErrorException se)
                {
                    errorMaps.Add(Path.GetFileName(mapFile));

                    Console.WriteLine();
                    l.Warn($"Syntax error in '{mapFile}':");
                    l.Fatal(se.Message);

                    var lines = File.ReadLines(mapFile).ToList();
                    var fileContents = mapFile.ReadAllText();

                    var badLine = lines[se.Start.Line - 1];
                    Console.WriteLine();
                    l.Fatal(
                        $"Bad line (or close to it) '{badLine}' has invalid data at column '{se.Start.Column}'");

                    if (fileContents.Contains('\t'))
                    {
                        Console.WriteLine();
                        l.Error(
                            "Bad line contains one or more tab characters. Replace them with spaces");
                        Console.WriteLine();
                        l.Info(fileContents.Replace("\t", "<TAB>"));
                    }
                }
                catch (YamlException ye)
                {
                    errorMaps.Add(Path.GetFileName(mapFile));

                    Console.WriteLine();
                    l.Warn($"Syntax error in '{mapFile}':");

                    var fileContents = mapFile.ReadAllText();

                    l.Info(fileContents);

                    if (ye.InnerException != null)
                    {
                        l.Fatal(ye.InnerException.Message);
                    }

                    Console.WriteLine();
                    l.Fatal("Verify all properties against example files or manual and try again.");
                }
                catch (Exception e)
                {
                    l.Error($"Error loading map file '{mapFile}': {e.Message}");
                }
            }

            if (errorMaps.Count > 0)
            {
                l.Error("\r\nThe following maps had errors. Scroll up to review errors, correct them, and try again.");
                foreach (var errorMap in errorMaps)
                {
                    l.Info(errorMap);
                }

                l.Info("");
            }

            return errorMaps.Count > 0;
        }



    }
}
