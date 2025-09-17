using ConvertISOs.Models;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Text.RegularExpressions;

class Program
{
    #region Private variables
    private static readonly object _consoleLock = new object();
    private readonly IConfiguration AppConfig;
    private static int MaxParallelEncodes;
    private static string HandBrakeCLIExePath;
    private static string VlcExePath;
    private static string IsoFilesListPath;
    private static List<string> IsoFileList = new List<string>();
    #endregion

    #region Main
    static async Task Main(string[] args)
    {
        Stopwatch GlobalStopwatch = Stopwatch.StartNew();
        bool InitializationSuccess = InitializeApp();
        ConcurrentDictionary<string, string> dictFilesAndDrivesList = new ConcurrentDictionary<string, string>();
        ConcurrentDictionary<string, List<TitleDetails>> dictIsoDetails = new ConcurrentDictionary<string, List<TitleDetails>>();

        if (InitializationSuccess == true)
        {
            # region Load the ISO files in VLC
            using (SemaphoreSlim VlcThrottler = new SemaphoreSlim(MaxParallelEncodes))
            {
                var VlcLoaderTasks = IsoFileList.Select(async IsoFilePath =>
                {
                    await VlcThrottler.WaitAsync();
                    try
                    {
                        SafeWriteLine($"[START] Loading {IsoFilePath} into VLC");
                        await LoadIsoFileWithVlcAsync(IsoFilePath);
                        SafeWriteLine($"[DONE] Loading {IsoFilePath} into VLC");

                    }
                    finally
                    {
                        VlcThrottler.Release();
                    }
                });

                await Task.WhenAll(VlcLoaderTasks);
            }
            //Parallel.ForEach(IsoFileList, IsoFile =>
            //{
            //    LoadIsoFileWithVLC(IsoFile);
            //});
            #endregion

            # region Scan the ISO files to get the titles
            using (SemaphoreSlim HandbrakeScannerThrottler = new SemaphoreSlim(MaxParallelEncodes))
            {
                var HandbrakeScannerTasks = IsoFileList.Select(async IsoFilePath =>
                {
                    await HandbrakeScannerThrottler.WaitAsync();
                    try
                    {
                        List<TitleDetails> TitleDetails = await ScanIsoFilesForTitlesAsync(IsoFilePath);
                        dictIsoDetails.TryAdd(IsoFilePath, TitleDetails);
                        SafeWriteLine(string.Format("These were the titles for file: {0}", IsoFilePath));

                        foreach (TitleDetails Title in TitleDetails)
                        {
                            SafeWriteLine(string.Format("Title: {0}, duration: {1}", Title.Title, Title.Duration));
                        }
                    }
                    finally
                    {
                        HandbrakeScannerThrottler.Release();
                    }
                });

                await Task.WhenAll(HandbrakeScannerTasks);
            }
            #endregion

            #region Convert the ISO file
            using (SemaphoreSlim ConversionThrottler = new SemaphoreSlim(MaxParallelEncodes))
            {
                var ConversionTasks = dictIsoDetails.Select(async IsoFileWithDetails =>
                {
                    await ConversionThrottler.WaitAsync();
                    try
                    {
                        SafeWriteLine($"[START] Encoding {IsoFileWithDetails.Key}");
                        await ConvertIsoFileWithHandbrakeAsync(IsoFileWithDetails);
                        SafeWriteLine($"[DONE] Encoding {IsoFileWithDetails.Key}");
                    }
                    finally
                    {
                        ConversionThrottler.Release();
                    }
                });
                await Task.WhenAll(ConversionTasks);
            }
            #endregion
                       
        }     

        SafeWriteLine("All encodes finished.");
        GlobalStopwatch.Stop();
        Console.WriteLine($"Total elapsed time: {GlobalStopwatch.Elapsed.TotalMinutes} minutes");
        Console.ReadLine();
    }
    #endregion

    #region Load the ISO file in VLC to decrypt the data
    private static async Task LoadIsoFileWithVlcAsync(string DriveLetter)
    {
        //SafeWriteLine(string.Format("Starting 'LoadIsoFileWithVLC' for ISO mounted on drive: {0}", DriveLetter));

        var VlcProcessInfo = new ProcessStartInfo
        {
            FileName = VlcExePath,
            Arguments = $"\"{DriveLetter}\" --play-and-exit",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var VlcLoadProcess = Process.Start(VlcProcessInfo))
        {
            if (VlcLoadProcess == null)
                return;

            // wait up to 5 seconds or until process exits
            Task exited = VlcLoadProcess.WaitForExitAsync();
            Task delay = Task.Delay(TimeSpan.FromSeconds(5));

            Task finished = await Task.WhenAny(exited, delay);

            if (finished == delay && !VlcLoadProcess.HasExited)
            {
                try { VlcLoadProcess.Kill(); } catch { }
            }

            // ensure process is really done
            await exited;
        }

        //SafeWriteLine(string.Format("Exiting 'LoadIsoFileWithVLC' for ISO mounted on drive: {0}", DriveLetter));
    }
    #endregion

    #region Get the titles of DVD/ISO file
    private static async Task<List<TitleDetails>> ScanIsoFilesForTitlesAsync(string IsoFilePath)
    {
        SafeWriteLine(string.Format("Starting 'ScanIsoFilesForTitles' for file {0}", IsoFilePath));
        List<TitleDetails> IsoTitleDetails = new List<TitleDetails>();
        TitleDetails CurrentTitleDetails = null;

        ProcessStartInfo ProcessInfo = new ProcessStartInfo
        {
            FileName = HandBrakeCLIExePath,
            Arguments = $"--title 0 -i \"{IsoFilePath}\" --scan 2>1 | Out-String",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process ScanProcess = new Process { StartInfo = ProcessInfo, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<List<TitleDetails>>();

        ScanProcess.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                // Look for lines like: "+ title 1:"
                var TitleMatch = Regex.Match(e.Data, @"\+ title (\d+):");
                if (TitleMatch.Success && int.TryParse(TitleMatch.Groups[1].Value, out int title))
                {
                    CurrentTitleDetails = new TitleDetails
                    {
                        Title = int.Parse(TitleMatch.Groups[1].Value)
                    };

                    IsoTitleDetails.Add(CurrentTitleDetails);
                }
            }
        };

        ScanProcess.ErrorDataReceived += (s, e) =>
        {
            // Sometimes useful info comes on stderr too (HandBrake quirks)
            if (!string.IsNullOrEmpty(e.Data))
            {
                var TitleMatch = Regex.Match(e.Data, @"\+ title (\d+):");
                if (TitleMatch.Success && int.TryParse(TitleMatch.Groups[1].Value, out int title))
                {
                    CurrentTitleDetails = new TitleDetails
                    {
                        Title = int.Parse(TitleMatch.Groups[1].Value)
                    };

                    IsoTitleDetails.Add(CurrentTitleDetails);
                }

                // Match: + duration: 01:52:30
                Match DurationMatch = Regex.Match(e.Data, @"^\s+\+ duration:\s+(\d+:\d+:\d+)");
                if (DurationMatch.Success && CurrentTitleDetails != null)
                {
                    CurrentTitleDetails.Duration = DurationMatch.Groups[1].Value;
                }
            }
        };

        ScanProcess.Exited += (s, e) =>
        {
            tcs.TrySetResult(IsoTitleDetails);
            ScanProcess.Dispose();
        };

        ScanProcess.Start();
        ScanProcess.BeginOutputReadLine();
        ScanProcess.BeginErrorReadLine();

        SafeWriteLine(string.Format("Exiting 'ScanIsoFilesForTitles' for file {0}", IsoFilePath));

        return await tcs.Task;
    }
    #endregion

    #region Mount the ISO file
    public static string MountIsoFile(string IsoFilePath)
    {
        SafeWriteLine(string.Format("Starting 'MountIsoFile' for file: {0}", IsoFilePath));

        string MountedDrive;

        using (PowerShell ps = PowerShell.Create())
        {
            ps.AddScript("Set-ExecutionPolicy -Scope LocalMachine -ExecutionPolicy Unrestricted -Force");
            ps.AddScript($@"Mount-DiskImage -ImagePath '{IsoFilePath}' -PassThru | Out-Null
                            (Get-DiskImage -ImagePath '{IsoFilePath}' | Get-Volume).DriveLetter");

            var results = ps.Invoke();
            if (ps.HadErrors || results.Count == 0)
            {
                throw new Exception("Failed to mount ISO: " + IsoFilePath);
            }
            else
            {
                SafeWriteLine(string.Format("Exiting 'MountIsoFile' for file: {0}", IsoFilePath));
                return MountedDrive = results[0].ToString() + ":\\";                
            }
        }        
    }
    #endregion

    #region Dismount ISO file
    public static void DismountIso(string IsoFilePath)
    {
        SafeWriteLine(string.Format("Starting 'DismountIso' for file: {0}", IsoFilePath));

        using (PowerShell ps = PowerShell.Create())
        {
            ps.AddScript($@"Dismount-DiskImage -ImagePath '{IsoFilePath}'");
            ps.Invoke();

            if (ps.HadErrors)
            {
                throw new Exception("Failed to unmount ISO: " + IsoFilePath);
            }
        }

        SafeWriteLine(string.Format("Exiting 'DismountIso' for file: {0}", IsoFilePath));
    }
    #endregion

    #region Initialization
    private static bool InitializeApp()
    {
        bool bSuccess = false;
        IConfigurationRoot AppSettings;

        SafeWriteLine("Starting 'InitializeApp'....");

        try
        {
            AppSettings = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

            MaxParallelEncodes = int.Parse(AppSettings["AppSettings:MaxThreads"]);
            if (MaxParallelEncodes == null)
            {
                throw new Exception("The degree of parallelism was not specified. \nPlease enter a valid value in the configuration file before proceeding.");
            }
            else
            {
                SafeWriteLine(string.Format("Threads specified: {0}", MaxParallelEncodes.ToString()));
            }

            VlcExePath = AppSettings["AppSettings:VlcExePath"];
            if (string.IsNullOrEmpty(VlcExePath) == true)
            {
                throw new Exception("A path to the VLC program was not found in the configuration file. \nPlease review the configuration and enter a value before proceeding.");
            }
            else if (File.Exists(VlcExePath) != true)
            {
                throw new Exception("The specified file path to VLC program is not valid. \nPlease review the configuration and enter a valid path before proceeding."); 
            }
            else
            {
                SafeWriteLine(string.Format("Path to VLC: {0}", VlcExePath));
            }

            HandBrakeCLIExePath = AppSettings["AppSettings:HandBrakeCLIExePath"];
            if (string.IsNullOrEmpty(HandBrakeCLIExePath) == true)
            {
                throw new Exception("A path to the Handbrake CLI program was not found in the configuration file. \nPlease review the configuration and enter a value before proceeding.");
            }
            else if (File.Exists(HandBrakeCLIExePath) != true)
            {
                throw new Exception("The specified file path to Handbrake CLI program is not valid. \nPlease review the configuration and enter a valid path before proceeding.");
            }
            else
            {
                SafeWriteLine(string.Format("Path to Handbrake CLI: {0}", HandBrakeCLIExePath));
            }

            IsoFilesListPath = AppSettings["AppSettings:IsoFilesListPath"];
            if (string.IsNullOrEmpty(IsoFilesListPath) == true)
            {
                throw new Exception("A path to the ISO file list was not found in the configuration file. \nPlease review the configuration and enter a value before proceeding.");
            }
            else if (File.Exists(IsoFilesListPath) != true)
            {
                throw new Exception("The specified file path for the ISO file list is not valid. \nPlease review the configuration and enter a valid path before proceeding.");
            }
            else if (File.Exists(IsoFilesListPath) == true)
            {
                SafeWriteLine(string.Format("Path to ISO file: {0}", IsoFilesListPath));
                IsoFileList = GetIsoFileList();
            }

            //All is well, so, display the values to the user...
            SafeWriteLine(string.Format("The configuration values set seem valid! The program will proceed to the next step..."));
            
            bSuccess = true;
        }
        catch (Exception eException)
        {
            SafeWriteLine(eException.Message);
        }
        
        
        SafeWriteLine("Exiting 'InitializeApp'....");

        return bSuccess;
    }
    #endregion

    #region Get the list of ISO files to work on
    private static List<string> GetIsoFileList()
    {
        SafeWriteLine("Starting 'GetIsoFileList'....");

        try
        {
            string[] strIsoFileList = (File.ReadAllLines(IsoFilesListPath));
            foreach (string IsoFile in strIsoFileList)
            {
                string CleanedUpIsoFile = IsoFile.Replace("\"", "");
                if (File.Exists(CleanedUpIsoFile))
                {
                    IsoFileList.Add(CleanedUpIsoFile);
                    SafeWriteLine(string.Format("This file was added to list of files to work with: {0}", IsoFile));
                }
                else
                {
                    SafeWriteLine(string.Format("This file was NOT added to list of files to work with: {0}", IsoFile));
                }
            }
        }
        catch (Exception eException)
        {
            SafeWriteLine(string.Format("An error occured while reading the file containing the list of ISO files to work with.\nHere are the details: {0}", eException.Message));
        }

        SafeWriteLine("Exiting 'GetIsoFileList'....");

        return IsoFileList;
    }
    #endregion

    #region Convert ISO files with Handbrake CLI
    private static async Task ConvertIsoFileWithHandbrakeAsync(KeyValuePair<string, List<TitleDetails>> IsoFileDetails)
    {
        SafeWriteLine(string.Format("Starting 'ConvertIsoFileWithHandbrake' for file {0}", IsoFileDetails.Key));
        string IsoFilePath = IsoFileDetails.Key;
        string IsoFileName = Path.GetFileNameWithoutExtension(IsoFilePath);
        string ConversionOutputDirectory = Path.Combine(Path.GetDirectoryName(IsoFilePath), "Converted");

        if (!Directory.Exists(ConversionOutputDirectory))
        {
            Directory.CreateDirectory(ConversionOutputDirectory);
        }

        foreach(TitleDetails TitleDetails in IsoFileDetails.Value)
        {
            string TitleFileName = $"{IsoFileName}-{TitleDetails.Title}.mkv";
            string CounvertedTitleFilePath = Path.Combine(ConversionOutputDirectory, TitleFileName);
            SafeWriteLine(string.Format("Now converting: {0}", CounvertedTitleFilePath));

            string ConversionArguments = string.Join(" ", new[]
            {
                $"--input \"{IsoFilePath}\"",
                $"--title {TitleDetails.Title}",
                "--preset \"H.265 MKV 2160p60 4K\"",
                "--markers",
                "--audio-lang-list eng",
                "--all-audio",
                "--subtitle scan",
                "--subtitle-forced=1",
                "--subtitle-burned=1",
                $"--output \"{CounvertedTitleFilePath}\""
            });

            ProcessStartInfo ProcessInfo = new ProcessStartInfo
            {
                FileName = HandBrakeCLIExePath,
                Arguments = ConversionArguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process ConversionProcess = new Process { StartInfo = ProcessInfo, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>();

            ConversionProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    SafeWriteLine($"[{IsoFileName} - Title {TitleDetails.Title}] {e.Data}");
                }
            };

            ConversionProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    SafeWriteLine($"[{IsoFileName} - Title {TitleDetails.Title}] {e.Data}");
                }
            };

            ConversionProcess.Exited += (s, e) =>
            {
                tcs.TrySetResult(ConversionProcess.ExitCode);
                ConversionProcess.Dispose();
            };
            ConversionProcess.Start();
            ConversionProcess.BeginOutputReadLine();
            ConversionProcess.BeginErrorReadLine();
            await ConversionProcess.WaitForExitAsync();

            int exitCode = await tcs.Task;

            if (exitCode != 0)
            {
                throw new Exception($"HandBrakeCLI failed for {IsoFilePath} (title {TitleDetails.Title}), exit code {exitCode}");
            }
        }

        SafeWriteLine(string.Format("Exiting 'ConvertIsoFileWithHandbrake' for file {0}", IsoFileDetails.Key));
    }
    #endregion

    #region Convert with Handbrake CLI
    private static async Task RunHandBrakeJobAsync(string input)
    {
        string output = Path.ChangeExtension(input, ".mkv");

        var psi = new ProcessStartInfo
        {
            FileName = HandBrakeCLIExePath,
            Arguments = $"--input \"{input}\" --output \"{output}\" " +
                        $"--preset \"H.265 MKV 2160p60 4K\" " +
                        $"--markers --audio-lang-list eng --all-audio " +
                        $"--subtitle scan --subtitle-forced=1 --subtitle-burned=1",
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        using (var process = new Process { StartInfo = psi })
        {
            //process.OutputDataReceived += (s, e) => { if (e.Data != null) SafeWriteLine(e.Data); };
            //process.ErrorDataReceived += (s, e) => { if (e.Data != null) SafeWriteLine(e.Data); };

            process.Start();
            //process.BeginOutputReadLine();
            //process.BeginErrorReadLine();

            await process.WaitForExitAsync();
        }
    }
    #endregion

    #region Thread safe write to console
    private static void SafeWriteLine(string message)
    {
        lock (_consoleLock)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
    #endregion
}
