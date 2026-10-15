# Running SQLECmd on Linux

SQLECmd runs natively on 64-bit Linux (x64) with .NET 10. Install the .NET 10 runtime (or the SDK, to build it) by following the instructions [here](https://learn.microsoft.com/en-us/dotnet/core/install/linux).

## SQLite library

SQLECmd includes the native SQLite library (`SQLite.Interop.dll`) from the System.Data.SQLite NuGet package for Windows (x64, x86), Linux (x64) and macOS (x64), so there is nothing to download or build. At startup the library for the current platform is extracted to `~/.local/share/SQLECmd/<platform>-<hash>/` (or `$XDG_DATA_HOME/SQLECmd`) and loaded from there. SQLECmd can be run from any directory and writes nothing to the working directory. If that location is not writable, a private temporary directory is used instead and removed when SQLECmd exits.

The Linux library needs glibc 2.14 or later and zlib (`libz.so.1`), which every current distribution has.

## Building

```
git clone https://github.com/EricZimmerman/SQLECmd.git
cd SQLECmd
dotnet publish SQLECmd -c Release -r linux-x64
```

When this finishes, the program is in `SQLECmd/bin/Release/net10.0/linux-x64/publish`. Copy those files to another folder, copy the `Maps` folder from `SQLMap` into the same folder, and run `./SQLECmd`. `./SQLECmd --sync` downloads the latest maps into that `Maps` folder.
