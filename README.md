# SQLECmd

This repo that contains all the Maps used by Eric Zimmerman's SQLECmd. 

## Ongoing Projects

 * [SQLECmd Map Ideas](https://github.com/EricZimmerman/SQLECmd/projects/1) - Development roadmap for SQLECmd Maps. Please feel free to contribute by adding ideas or by finishing tasks in the `To Do` column. Any help is appreciated! 

## Command Line Interface

    SQLECmd version 0.5.0.0
    
    Author: Eric Zimmerman (saericzimmerman@gmail.com)
    https://github.com/EricZimmerman/SQLECmd
    
            d               Directory to process that contains SQLite files. This or -f is required
            f               File to process. This or -d is required
    
            csv             Directory to save CSV formatted results to.
            json            Directory to save JSON formatted results to.
    
            dedupe          Deduplicate -f or -d files based on SHA-1. First file found wins. Default is TRUE
            hunt            When true, all files are looked at regardless of name and file header is used to identify SQLite files, else filename in map is used to find databases. Default is FALSE
    
            maps            The path where event maps are located. Defaults to 'Maps' folder where program was executed
    
            sync            If true, the latest maps from https://github.com/EricZimmerman/SQLECmd/tree/master/SQLMap/Maps are downloaded and local maps updated. Default is FALSE
    
            debug           Show debug information during processing
            trace           Show trace information during processing
    
    
    Examples: SQLECmd.exe -f "C:\Temp\someFile.db" --csv "c:\temp\out"
              SQLECmd.exe -d "C:\Temp\" --csv "c:\temp\out"
              SQLECmd.exe -d "C:\Temp\" --hunt --csv "c:\temp\out"
    
              Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes

## Documentation

SQLECmd parses any SQLite database from any OS. As long as a Map exists for the database, SQLECmd will parse it! If there's a Map that's missing, please create an issue or submit your own via a Pull Request. 

# Download Eric Zimmerman's Tools

All of Eric Zimmerman's tools can be downloaded [here](https://ericzimmerman.github.io/#!index.md).

# Special Thanks

Open Source Development funding and support provided by the following contributors: 
- [SANS Institute](http://sans.org/) and [SANS DFIR](http://dfir.sans.org/).
- [Tines](https://www.tines.com/?utm_source=oss&utm_medium=sponsorship&utm_campaign=ericzimmerman)
