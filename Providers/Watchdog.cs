using System.Diagnostics;
using System.Globalization;

namespace Limits;

internal sealed partial class TrayApplication
{
    private LimitWatchdog? _limitWatchdog;
    private volatile bool _limitWatchdogInFlight;

    partial void NotifyClaudeSnapshotChanged(ClaudeUsageSnapshot? snapshot)
    {
        if (_limitWatchdogInFlight)
        {
            return;
        }

        _limitWatchdogInFlight = true;
        _limitWatchdog ??= new LimitWatchdog(_diskMonitor);
        Task.Run(() =>
        {
            try
            {
                _limitWatchdog.Check(snapshot);
            }
            finally
            {
                _limitWatchdogInFlight = false;
            }
        });
    }

    partial void NotifyDiskSnapshotChanged()
    {
        NotifyClaudeSnapshotChanged(_lastClaudeSnapshot);
    }
}

internal sealed class LimitWatchdog

{

    private const double ThresholdRemainingPercent = 5.0d;



    private static readonly string PauseBatchPath =

        @"C:\Users\flcl\Desktop\Pause Strategy Hunt.bat";



    private static readonly string RunBatchPath =

        @"C:\Users\flcl\Desktop\Run Strategy Hunt.bat";



    private static readonly string AppDataPath = Path.Combine(

        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),

        "limits");



    private static readonly string LogPath = Path.Combine(AppDataPath, "limits.log");

    private readonly DiskMonitor _diskMonitor;

    private string _state = "unknown";



    public LimitWatchdog(DiskMonitor diskMonitor)

    {

        _diskMonitor = diskMonitor;

    }



    public void Check(ClaudeUsageSnapshot? snapshot)

    {

        Directory.CreateDirectory(AppDataPath);



        string state = _state;

        LimitUsage? usage = snapshot is null ? null : LimitUsage.FromSnapshot(snapshot);

        DiskSpaceSnapshot disk = _diskMonitor.ReadSnapshot();



        if (usage is null && !disk.HasLowDisk)

        {

            Log($"No action: state={state}, Claude usage unavailable, disk {disk.Summary}.");

            return;

        }



        bool usageBelowLimit = usage is not null &&

            (usage.FiveHourRemainingPercent < ThresholdRemainingPercent ||

             usage.WeeklyRemainingPercent < ThresholdRemainingPercent);

        bool belowLimit = usageBelowLimit || disk.HasLowDisk;



        bool usageAvailableAgain = usage is not null &&

            usage.FiveHourRemainingPercent > ThresholdRemainingPercent &&

            usage.WeeklyRemainingPercent > ThresholdRemainingPercent;

        bool availableAgain = usageAvailableAgain && !disk.HasLowDisk;



        if (belowLimit && !string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase))

        {

            Log(

                $"Below threshold: {FormatUsage(usage)}, disk {disk.Summary}. " +

                "Starting pause batch.");

            StartBatch(PauseBatchPath);

            _state = "paused";

            return;

        }



        if (availableAgain && string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase))

        {

            Log(

                $"Usage and disk available again: {FormatUsage(usage)}, disk {disk.Summary}. " +

                "Starting run batch.");

            StartBatch(RunBatchPath);

            _state = "running";

            return;

        }



        if (string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase) && availableAgain)

        {

            _state = "running";

        }



        Log($"No action: state={state}, {FormatUsage(usage)}, disk {disk.Summary}.");

    }



    private static void StartBatch(string batchPath)

    {

        if (!File.Exists(batchPath))

        {

            Log($"Missing batch file: {batchPath}");

            return;

        }



        ProcessStartInfo startInfo = new()

        {

            FileName = "cmd.exe",

            UseShellExecute = false,

            CreateNoWindow = true,

            WorkingDirectory = Path.GetDirectoryName(batchPath) ?? AppContext.BaseDirectory

        };

        startInfo.ArgumentList.Add("/d");

        startInfo.ArgumentList.Add("/c");

        startInfo.ArgumentList.Add("call");

        startInfo.ArgumentList.Add(batchPath);



        Process? process = Process.Start(startInfo);

        Log($"Started '{batchPath}' pid={process?.Id.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");

    }



    private static void Log(string message)

    {

        try

        {

            Directory.CreateDirectory(AppDataPath);

            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");

        }

        catch

        {

            // Logging must not bring down the tray app.

        }

    }



    private static string FormatUsage(LimitUsage? usage)

    {

        return usage is null

            ? "Claude usage unavailable"

            : $"5h left {usage.FiveHourRemainingPercent:0.##}%, weekly left {usage.WeeklyRemainingPercent:0.##}%";

    }



    private sealed record LimitUsage(

        double FiveHourRemainingPercent,

        double WeeklyRemainingPercent,

        DateTimeOffset? FiveHourResetAt,

        DateTimeOffset? WeeklyResetAt)

    {

        public static LimitUsage FromSnapshot(ClaudeUsageSnapshot snapshot)

        {

            return new LimitUsage(

                ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent),

                ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent),

                snapshot.FiveHourResetAt,

                snapshot.SevenDayResetAt);

        }

    }



}

