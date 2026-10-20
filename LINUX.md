# Running SQLECmd on Linux

SQLECmd runs natively on 64-bit Linux (x64) with .NET 10. There are two ways to get it: use the download as it is (step by step below; nothing to build), or build it from source.

## SQLite library

SQLECmd comes with the native SQLite library (`SQLite.Interop.dll`) from the System.Data.SQLite NuGet package for Windows (x64, x86), Linux (x64) and macOS (x64), so there is nothing to download or build. They are files in the `runtimes` folder next to `SQLECmd.dll` (`runtimes/linux-x64/native/SQLite.Interop.dll` and so on), and the one for the current platform is loaded from there. Nothing is extracted or written anywhere else (earlier 2026.10 builds extracted it to `~/.local/share/SQLECmd`; that folder can be deleted). SQLECmd can be run from any directory and writes nothing to the working directory.

The Linux library needs glibc 2.14 or later and zlib (`libz.so.1`), which every current distribution has.

## Using the download

1. Install the .NET 10 runtime. Your distribution's packages work (see [Install .NET on Linux](https://learn.microsoft.com/en-us/dotnet/core/install/linux)), or, without root, Microsoft's install script:

   ```
   curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
   bash dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir ~/.dotnet
   export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH
   ```

   (add the `export` line to `~/.bashrc` to keep it). `dotnet --list-runtimes` should show `Microsoft.NETCore.App 10.x`.

2. Download and unzip the .NET 10 build of SQLECmd (`net/SQLECmd.zip` on the [download page](https://ericzimmerman.github.io/)):

   ```
   curl -sSLO https://download.ericzimmermanstools.com/net/SQLECmd.zip
   unzip SQLECmd.zip
   cd SQLECmd
   ```

   The folder has `SQLECmd.dll` (the program), `SQLECmd.runtimeconfig.json`, `SQLECmd.exe` (Windows only), `runtimes` (the SQLite libraries) and `Maps`.

3. Run it with `dotnet`:

   ```
   dotnet SQLECmd.dll -d /path/to/evidence --csv /path/to/output
   dotnet SQLECmd.dll --sync
   ```

   The options are the same as on Windows (`dotnet SQLECmd.dll --help`). `--sync` downloads the latest maps into the `Maps` folder next to `SQLECmd.dll`, so that folder must be writable.

## Building from source

1. Install the .NET 10 SDK: as above, with `--channel 10.0` and without `--runtime dotnet` for the script, or `dotnet-sdk-10.0` from your distribution.

2. Build:

   ```
   git clone https://github.com/EricZimmerman/SQLECmd.git
   cd SQLECmd
   dotnet publish SQLECmd -c Release -r linux-x64
   ```

3. The program is in `SQLECmd/bin/Release/net10.0/linux-x64/publish`. Copy it, with the maps, to a folder of its own:

   ```
   mkdir -p ~/SQLECmd-build
   cp -r SQLECmd/bin/Release/net10.0/linux-x64/publish/. ~/SQLECmd-build/
   cp -r SQLMap/Maps ~/SQLECmd-build/Maps
   ```

4. Run it:

   ```
   cd ~/SQLECmd-build
   ./SQLECmd -d /path/to/evidence --csv /path/to/output
   ./SQLECmd --sync
   ```

## Problems

- `Couldn't find a valid ICU package installed on the system`: .NET needs ICU (`libicu`), which a minimal system (a container, a fresh WSL) may not have. Install it with the package manager, or set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.
- `dotnet-SQLECmd.dll does not exist`: run `dotnet` from the `SQLECmd` folder inside the zip (where `SQLECmd.dll` is), or give the full path to `SQLECmd.dll`.
- `SQLECmd.exe` is the Windows program: on Linux, use `dotnet SQLECmd.dll` (the download) or `./SQLECmd` (built from source).

Both ways were tested on Ubuntu 26.04 (WSL) with .NET 10: their CSVs for the test databases match the Windows build's.
