# Building SQLECmd on Linux

Recent changes have allowed SQLECmd to run natively on Linux. This has been tested on Ubuntu 22.04 LTS and .NET 9

In order to do this you will need to download .NET 9 SDK on Linux. I followed the instructions [here](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install?tabs=dotnet9&pivots=os-linux-ubuntu-2404). 

In the case of Ubuntu you will need to install build-essential if you wish to compile the libSQLite.Interop.so yourself by doing sudo apt-get update and sudo apt install build-essential

If you have issues with the bundled libSQLite.Interop.so the steps to build your own are as follows:
Download the source code for the library by navigating [here] (https://system.data.sqlite.org/index.html/doc/trunk/www/downloads.wiki) and downloading the one that says sqlite-netFx-full-source-1.0.119.0.zip

Uncompress the zip file and issue the following commands in a Linux terminal:
cd LOCATIONOFSOURCE/Setup
chmod +x compile-interop-assembly-release.sh
./compile-interop-assembly-release.sh

Now, you will have a freshly built library file called libSQLite.Interop.so in the LOCATIONOFSOURCE/bin/2013/Release/bin folder.

Download the SQLECmd source code if you haven't already [here](https://github.com/EricZimmerman/SQLECmd/archive/refs/heads/master.zip) and copy and replace the libSQLite.Interop.so file located in SQLEcmd/Dependencies/x64/libSQLite.Interop.so

To build the native Linux Binary issue the following commands in a Linux terminal:
cd LOCATIONOFSQLECMDMASTERFOLDER - This will have SQLECmd.sln 
dotnet publish -f net9.0 -r linux-x64

When this finshes the resulting files will be located in LOCATIONOFSQLECMDMASTERFOLDER/SQLECmd/bin/Release/net9.0/linux-x64/publish
All you need to do is copy these files to another folder and copy the Maps folder from LOCATIONOFSQLECMDMASTERFOLDER/SQLMap into the same folder and run it in terminal with ./SQLECmd