#if NET462
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;
#else
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation.Results;
using Serilog;
using ServiceStack;
using ServiceStack.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;


namespace SQLMaps;

public class SqlMap
{
    public static Dictionary<string, MapFile> MapFiles { get; private set; } = new();

    private static bool DisplayValidationResults(ValidationResult result, string source)
    {
        Log.Verbose("Performing validation on {Source}: {Result}", source, result.Dump());
        if (result.Errors.Count == 0)
        {
            return true;
        }

        Console.WriteLine();
        Log.Error("{Source} had validation errors", source);

        foreach (var validationFailure in result.Errors)
        {
            Log.Information("{Val}", validationFailure);
        }

        Console.WriteLine();
        Console.WriteLine();
        Log.Error("Correct the errors and try again. Exiting");
        Console.WriteLine();

        return false;
    }

    public static bool LoadMaps(string mapPath)
    {
        MapFiles = new Dictionary<string, MapFile>();

        IEnumerable<string> files;

#if !NET6_0
  var f = new Alphaleonis.Win32.Filesystem.DirectoryEnumerationFilters
        {
            InclusionFilter = entry => entry.Extension.ToUpperInvariant() == ".SMAP",
            RecursionFilter = null,
            ErrorFilter = (errorCode, errorMessage, pathProcessed) => true
        };

        var dirEnumOptions =
            Alphaleonis.Win32.Filesystem.DirectoryEnumerationOptions.Files |
            Alphaleonis.Win32.Filesystem.DirectoryEnumerationOptions.SkipReparsePoints | Alphaleonis.Win32.Filesystem.DirectoryEnumerationOptions.ContinueOnException |
            Alphaleonis.Win32.Filesystem.DirectoryEnumerationOptions.BasicSearch;

       files =
            Alphaleonis.Win32.Filesystem.Directory.EnumerateFileSystemEntries(mapPath, dirEnumOptions, f).ToList();
#else

        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = true,
            AttributesToSkip = 0
        };

        files = Directory.EnumerateFileSystemEntries(mapPath, "*.SMAP", enumerationOptions);
#endif


        var deserializer = new DeserializerBuilder()
            .Build();

        var validator = new MapFileMapValidator();

        var errorMaps = new List<string>();

        foreach (var mapFile in files.OrderBy(t => t))
        {
            try
            {
                var mf = deserializer.Deserialize<MapFile>(File.ReadAllText(mapFile));

                Log.Verbose("{Mf}", mf.Dump());

                var validate = validator.Validate(mf);

                if (DisplayValidationResults(validate, mapFile))
                {
                    if (MapFiles.ContainsKey(
                            $"{mf.Id.ToUpperInvariant()}") == false)
                    {
                        Log.Debug("{Path} is valid. Id: {Mf}. Adding to maps...", Path.GetFileName(mapFile), mf.Id);
                        MapFiles.Add($"{mf.Id.ToUpperInvariant()}",
                            mf);
                    }
                    else
                    {
                        Log.Warning("A map with Id {Id} already exists (File name: {FileName}). Map {Path} will be skipped", mf.Id, mf.FileName, Path.GetFileName(mapFile));
                    }
                }
                else
                {
                    errorMaps.Add(Path.GetFileName(mapFile));
                }
            }
            catch (SyntaxErrorException se)
            {
                errorMaps.Add(Path.GetFileName(mapFile));

                Console.WriteLine();
                Log.Warning("Syntax error in {MapFile}", mapFile);
                Log.Fatal("{Message}", se.Message);

                var lines = File.ReadLines(mapFile).ToList();
                var fileContents = mapFile.ReadAllText();

                var badLine = lines[(int)(se.Start.Line - 1)];
                Console.WriteLine();
                Log.Fatal("Bad line (or close to it) {BadLine} has invalid data at column {Column}", badLine, se.Start.Column);

                if (fileContents.Contains('\t'))
                {
                    Console.WriteLine();
                    Log.Error("Bad line contains one or more tab characters. Replace them with spaces");
                    Console.WriteLine();
                    Log.Information("{File}", fileContents.Replace("\t", "<TAB>"));
                }
            }
            catch (YamlException ye)
            {
                errorMaps.Add(Path.GetFileName(mapFile));

                Console.WriteLine();
                Log.Warning("Syntax error in {MapFile}", mapFile);

                var fileContents = mapFile.ReadAllText();

                Log.Information("{Contents}", fileContents);

                if (ye.InnerException != null)
                {
                    Log.Fatal("{Ye}", ye.InnerException.Message);
                }

                Console.WriteLine();
                Log.Fatal("Verify all properties against example files or manual and try again");
            }
            catch (Exception e)
            {
                Log.Error(e, "Error loading map file '{MapFile}': {Message}", mapFile, e.Message);
            }
        }

        if (errorMaps.Count > 0)
        {
            Console.WriteLine();
            Log.Error("The following maps had errors. Scroll up to review errors, correct them, and try again");
            foreach (var errorMap in errorMaps)
            {
                Log.Information("{Map}", errorMap);
            }

            Console.WriteLine();
        }

        return errorMaps.Count > 0;
    }
}