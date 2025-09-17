.net 9 console project
1. Set values in appsettings.json
2. Run

appsettings.json:
1. MaxThreads => Processor dependent
2. HandBrakeCLIExePath => Path to Handbrake CLI
3. VlcExePath => Path to VLC
4. IsoFilesListPath => One ISO file path, per line

Overall workflow:
1. Load ISO files in VLC
2. Scan the ISO files for titles
3. Convert the ISO files titles, one by one, presets hardcoded.

All in parallel/async mode
