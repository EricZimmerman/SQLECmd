# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Part of Eric Zimmerman's DFIR tool suite. Three things in one repo:
- **`SQLMap/` (`SQLMaps`)**: a `netstandard2.0;net10.0` NuGet library that loads and validates map files (`.smap`, YAML). Released to NuGet from a GitHub release.
- **`SQLMap/Maps/`**: the maps themselves, one per database type. `--sync` downloads this folder from `master`, so a merged map reaches users without a release. `.github/workflows/verify.yml` runs yamllint (`.yamllint`) on them. `!OS_Application_OptionalDescription.guide` and `.template` document the format.
- **`SQLECmd/`**: the CLI (`net10.0`, Windows, Linux and macOS). It references the library as a project and runs each matching map's queries against SQLite databases, writing CSV, JSON lines and blobs. Built, signed and staged manually.

Input may be corrupt or hostile: report problems and keep going with the other files and maps.

## Commands

The SDK is pinned in `global.json` (.NET 10). All projects use `LangVersion 10`, so avoid newer C# syntax. Tests use NUnit 5 + FluentAssertions 7 (pinned to `[7.2.2,8.0.0)`, since 8.x requires a commercial license).

```bash
dotnet build -c Release                                  # builds SQLECmd.slnx
dotnet test -c Release                                   # all tests
dotnet test --filter "FullyQualifiedName~CliTests"       # one fixture
dotnet test --filter "Name=EveryShippedMapLoads"         # one test
dotnet pack -c Release                                   # SQLMaps package (README.md and icon.png from the repo root)
dotnet run --project SQLECmd -- -d SQLMap.Test/TestFiles --csv out --maps SQLMap/Maps
```

## Maps (`SQLMap/`)

- `MapFile` and `QueryInfo` are the YAML schema; YamlDotNet rejects unknown properties. `MapFileMapValidator` (FluentValidation 11; 12 dropped netstandard2.0) requires the identity fields and at least one query. `BlobColumns` is optional; `BlobExtension` is alphanumeric/underscore only, because it ends up in file names.
- `SqlMap.LoadMaps(dir)` loads every `*.smap` under `dir`, recursively, into the static `MapFiles` (keyed by upper-case `Id`). It returns true if any map had errors; the CLI then exits. A duplicate `Id` is a warning and the first one wins.
- `SqLiteFile.IsSqLiteFile` checks the 8 byte `SQLite f` header and opens with `FileShare.ReadWrite | Delete`, since databases are often in use.

## CLI (`SQLECmd/Program.cs`)

- `Main` awaits `InvokeAsync` and returns `DoWork`'s exit code (Eric's rule, 2026-10-22: 1 only when the tool fails): 1 for invalid arguments (including a missing `-f` file or `-d` folder), map errors, `--sync` failing, files or folders that can't be read (access denied, locked, IO errors; a `-d` folder that can't be searched completely) or CSV/JSON/blob output that can't be written. Damaged databases, a `-f` file that is not SQLite and map queries that fail on a database are listed ("could not be processed", "query/export error(s)") but give 0, as do databases with no matching map.
- With `--hunt` (the default), every file under `-d` whose header says SQLite is tried against every map; a map applies when its `IdentifyQuery` returns `IdentifyValue` (compared as strings). Without it, only maps whose `FileName` matches are tried. When hunting, mismatches and failing `IdentifyQuery`s are only logged with `--debug`. If every map's `IdentifyQuery` fails, the database can't be read and the file counts as failed.
- One connection per database. A failing query is logged and the other queries still run. `--dedupe` (default) skips files with a SHA-1 already seen.
- Output names are `<timestamp>_<CSVPrefix>_<BaseFileName>_<map Id>.csv`, with a `SourceFile` column added. JSON is one file with one object per row (plus `SourceFile` and `MapDescription`), with dates in ISO 8601 UTC (`ConfigureJsonDates`, as in JLECmd). Connections use `DateTimeKind=Utc` (`ConnectionString`): without it System.Data.SQLite converts DATETIME text with a zone to the local time of the machine. Blob columns are written to files (named from `NameColumn`, sanitized) and removed from the CSV/JSON.
- **Native SQLite (`NativeSqlite.cs`)**: System.Data.SQLite needs `SQLite.Interop.dll`, which has the same name on every OS. The four from the `Stub.System.Data.SQLite.Core.NetStandard` package (win-x64, win-x86, linux-x64, osx-x64) are files next to SQLECmd in the package's layout, `runtimes\<rid>\native\SQLite.Interop.dll`: the build output has them there, and Build-ZimmermanTools stages them so (`Natives` in its table). At startup the one for this process is loaded from `AppContext.BaseDirectory` (then `SQLite.Interop.dll` right next to SQLECmd.dll, as a `dotnet publish -r <rid>` has it) through `NativeLibrary.SetDllImportResolver` on the System.Data.SQLite assembly, with System.Data.SQLite's own pre-loader off (`No_PreLoadSQLite`). Nothing is extracted or written anywhere (Eric, 2026-10-20: nothing outside the program's folder; before, they were embedded and extracted to `%LOCALAPPDATA%\SQLECmd`); a missing file is an error that says where it was looked for. `CliTests` checks that nothing is written to the working directory or to LOCALAPPDATA/XDG_DATA_HOME/TEMP, `NativeSqliteTests` that the library is loaded from the program's folder. The Stub package version must equal the System.Data.SQLite.Core version from ServiceStack.OrmLite.Sqlite; NuGet and the `CheckSqliteNatives` target fail the build otherwise, so update them together.
- Only copies of the databases are opened (`DatabaseCopy`, with their `-wal` and `-journal`): SQLite opens read-write (OrmLite default) and would checkpoint a `-wal` into the evidence and delete it (it did before; `CliTests.TheEvidenceIsNotChanged`). The copies go to a new folder per database in `--temp`, or by default `Temp` next to SQLECmd (`TempFolder.cs`; Eric, 2026-10-20: nothing in the user's %TEMP%), created when needed and removed when empty; if it can't be written to, SQLECmd stops with exit code 1 (no fallback: the user gives `--temp`). The same in AmcacheParser and WxTCmd. Dispose every reader: System.Data.SQLite keeps the file open while a statement is alive, and Windows then can't delete the copy (`DatabaseCopy` retries after a GC).

## Tests

`SQLMap.Test` (net10.0) references the library and the CLI, and copies `TestFiles/` and all of `SQLMap/Maps/` to its output.
- `MapTests`: every shipped map loads with a unique Id, plus validation cases. Non-parallelizable, because `MapFiles` is static.
- `SqLiteFileTests`, `NativeSqliteTests` (every platform's library is in `runtimes/`, and the one for this process loads from there).
- `CliTests`: runs the built `SQLECmd.dll` with `dotnet` from an empty working directory against `TestFiles/` (the `TestFiles_*.smap` maps): CSV/JSON/blob output, dedupe, unmatched and corrupt databases, exit codes.

## Versioning and release

- **Library:** MinVer sets the version from the git tag (bare tags like `2026.10.0`); there is no `<Version>` in its csproj. To release: push to `master`, then `git tag <ver> && git push origin <ver> && gh release create <ver> --verify-tag --generate-notes`.
- **CLI:** `<Version>` in `SQLECmd/SQLECmd.csproj`, bumped by hand per release.
- Versions are `yyyy.mm.x`.
- `.github/workflows/publish.yml` (identical to GuidMapping's) packs, validates and tests on PRs and pushes to `master`. When a GitHub Release is published, it pushes to NuGet.org using trusted publishing (`NuGet/login` with the `NUGET_USER` repo variable).
- Dependabot (`.github/dependabot.yml`) opens monthly PRs for GitHub Actions and NuGet updates.
- Release builds are staged, signed and zipped by `Build-ZimmermanTools.ps1` in the get-zimmermantools repository (cloned next to this one). Its program table says what goes in this tool's download zip (the files taken from the build, folders copied from the repositories, files kept from the current release zip); a new output file, a content folder or a change in how the tool is built (single file, another framework) needs that entry updated.
- pdbs are embedded in the assemblies (`DebugType embedded`, `EmbedUntrackedSources`) and Release builds set `ContinuousIntegrationBuild` (source paths `/_/...`), both in `Directory.Build.props`: stack traces and Exceptionless reports have file and line numbers, and no .pdb file ships. Don't add `DebugType` (or `DebugSymbols`, `IncludeSymbols`, snupkg) to project files.
