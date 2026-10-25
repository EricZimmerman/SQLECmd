# SQLECmd

Finds SQLite databases from any OS and runs the queries in its maps against them: browsers (Chromium, Edge, Firefox),
Dropbox, the Windows Activities Cache, Android and iOS databases and many more. As long as a map exists for the
database, SQLECmd parses it. Results are exported to CSV and JSON, and blobs (such as Edge's history screenshots) to
files.

| | |
|---|---|
| **Version** | 2026.10.3 |
| **Download** | [.NET 10](https://download.ericzimmermanstools.com/net/SQLECmd.zip) |
| **All tools** | [ericzimmerman.github.io](https://ericzimmerman.github.io) or [Get-ZimmermanTools](https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip), which downloads them all and keeps them current |
| **Changes** | [ChangeLog](https://ericzimmerman.github.io/changelog/) |
| **Issues** | [EricZimmerman/SQLECmd](https://github.com/EricZimmerman/SQLECmd/issues) or [EricZimmerman/Issues](https://github.com/EricZimmerman/Issues/issues) |

## Requirements

- **.NET 10 build**: the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Run `SQLECmd.exe` on
  Windows, or `dotnet SQLECmd.dll` on any system with the runtime. The native SQLite libraries for Windows (x64, x86),
  Linux (x64) and macOS (x64) are included, in `runtimes\<rid>\native` next to SQLECmd. [LINUX.md](LINUX.md) has step
  by step instructions for Linux.

## Command line

```text
Usage:
  SQLECmd [options]

Options:
  -f <f>               File to process. Either this or -d is required
  -d <d>               Directory to recursively process. Either this or -f is required
  --csv <csv>          Directory to save CSV formatted results to. Be sure to include the full path in double quotes
  --json <json>        Directory to save JSON formatted results to. Be sure to include the full path in double quotes
  --blobdir <blobdir>  Directory to save blob data to. Be sure to include the full path in double quotes
  --noblob             If true, disables blob extraction from query results
  --ifn                If true, the name of each database is added to the names of its CSV and blob files, so databases processed with the same map can be told apart
  --dedupe             Deduplicate -f or -d files based on SHA-1. First file found wins
  --hunt               When true, all files are looked at regardless of name and file header is used to identify SQLite files, else filename in map is used to find databases
  --maps <maps>        The path where SQL maps are located. Defaults to 'Maps' folder where program was executed
  --temp <temp>        Folder for the temporary copies of the SQLite databases, which are opened instead of the evidence and deleted when done. Default: a Temp folder next to SQLECmd. A folder that isn't there is created, and removed again when done. If that folder can't be written to, SQLECmd stops: give a writable folder with --temp
  --sync               If true, the latest maps from https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps are downloaded and local maps updated
  --debug              Show debug information during processing
  --trace              Show trace information during processing
  --co                 When true, save all console messages to <timestamp>_SQLECmd_<version>_ConsoleLog.txt in the output directory (the first of --csv, --json, --blobdir given). Use --co false to turn it off
  -?, -h, --help       Show help and usage information
  --version            Show version information
```

Short options (a single letter) start with one dash, long options with two.

## Examples

```text
SQLECmd.exe -f "C:\Temp\someFile.db" --csv "c:\temp\out"
SQLECmd.exe -d "C:\Temp\" --csv "c:\temp\out"
SQLECmd.exe -d "C:\Temp\" --hunt --csv "c:\temp\out"
SQLECmd.exe -d "C:\Temp\" --csv "c:\temp\out" --ifn
```

## Output

- **Console**: each database, the maps that match it and the queries run.
- **CSV** (`--csv`): one file per map query, `<timestamp>_<CSVPrefix>_<BaseFileName>_<map Id>.csv`, with a `SourceFile`
  column added. With `--ifn` the database's file name is added to the name, so databases processed with the same map
  can be told apart.
- **JSON** (`--json`): one file, `<timestamp>_SQLECmd_Output.json`, with one object per row (plus `SourceFile` and
  `MapDescription`). Dates are ISO 8601 UTC (`2024-01-02T03:04:05.6780000Z`).
- **Blobs**: columns a map names as blob columns are written to files in `--blobdir` (or the `--csv` or `--json`
  folder) and left out of the CSV and JSON. `--noblob` turns this off.

The evidence is never changed: each database is copied, with its `-wal` and `-journal`, to a `Temp` folder next to
SQLECmd (or the folder given with `--temp`), and only the copy is opened. The copies are deleted when done. If that
folder can't be written to, SQLECmd stops and asks for `--temp`.

With `--hunt` (on by default), every file under `-d` whose header says it is SQLite is tried against every map, and a
map applies when its identify query returns the value the map expects. Without it, only databases whose file name
matches a map's are processed. `--dedupe` (on by default) skips files whose SHA-1 was already seen.

## Maps

Maps (`.smap`, YAML) are named `OS_Application_Description.smap`, one per database type. Each says how to recognize the
database and which queries to run on it, and how to name the output.

- SQLECmd loads the maps from the `Maps` folder next to it (and its subfolders), or the folder given with `--maps`.
- They live in this repository, in [SQLMap/Maps](https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps).
  `SQLECmd.exe --sync` downloads the latest maps from there into the `Maps` folder next to SQLECmd, so maps merged into
  the repository reach you without a new release.
- To write a map, use the
  [Guide](https://github.com/EricZimmerman/SQLECmd/blob/master/SQLMap/Maps/!OS_Application_OptionalDescription.guide)
  and the
  [Template](https://github.com/EricZimmerman/SQLECmd/blob/master/SQLMap/Maps/!OS_Application_OptionalDescription.template)
  in the Maps folder. If a map is missing, create an issue or submit your own in a pull request.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. Databases that are damaged or not SQLite, map queries that fail on a database and databases no map matches are reported but are not a failure |
| `1` | The tool failed: invalid arguments or maps, a missing `-f` or `-d` path, `--sync` failing, a `--temp` folder that can't be written to, output that can't be written, or files or folders it can't read. Folders under `-d` that can't be read are reported (`Unable to read folder <path>: <reason>`, once per folder, then `Could not access all files`) and the rest is still processed |

## What's new in 2026.10

- 2026.10.3: `--blobdir` is in the list of folders for the console log (after `--csv` and `--json`), like every other output folder.
- 2026.10.2: `--co` (on by default; `--co false` turns it off) saves everything written to the console to `<timestamp>_SQLECmd_<version>_ConsoleLog.txt` in
  the output directory (Issues #259); `--debug` and `--trace` show the time of each message again (they showed the
  date in the default format).
- One connection per database instead of one per map (faster).
- The evidence is no longer changed: databases were opened in place, which wrote the `-wal` into the database and
  deleted the `-wal` and `-shm` files. Only a copy is opened now (new `--temp`).
- New `--ifn`: adds the database's file name to its CSV and blob file names (#54).
- JSON dates are ISO 8601 UTC instead of `\/Date(milliseconds)\/`, which was also shifted by the time zone of the
  machine; DATETIME values stored with a time zone are no longer converted to local time in CSV and JSON.
- No longer writes `SQLite.Interop.dll` into the working directory: the SQLite natives for Windows x64/x86, Linux and
  macOS are in the `runtimes` folder and loaded from there.
- A map whose identify query fails no longer skips the remaining maps; hunt mode no longer warns about every
  non-matching map and non-SQLite file (only with `--debug`). `--sync` verifies TLS certificates (PR #101).
- Exit code 1 when the tool fails (it was always 0). Error reports include source file and line numbers.

The full list is in the [ChangeLog](https://ericzimmerman.github.io/changelog/).

## Building

```text
dotnet build SQLECmd.slnx -c Release
```

Builds `net10.0`. Map loading and validation is in the `SQLMaps` library in this repository
([NuGet](https://www.nuget.org/packages/SQLMaps)).

## Special thanks

Open source development funding and support provided by the [SANS Institute](https://www.sans.org/) and
[SANS DFIR](https://www.sans.org/digital-forensics-incident-response/).

## License

[MIT](LICENSE), Copyright (c) Eric R. Zimmerman 2027.
