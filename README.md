.net 9 console project
1. Set values in appsettings.json
2. Run

appsettings.json:
MaxThreads => Processor dependent
HandBrakeCLIExePath => Path to Handbrake CLI
VlcExePath => Path to VLC
IsoFilesListPath => One ISO file path, per line

Overall workflow:
1. Load ISO files in VLC
2. Scan the ISO files for titles
3. Convert the ISO files titles, one by one

All in parallel/async mode
