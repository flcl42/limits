using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Limits;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, "--shutdown", StringComparison.OrdinalIgnoreCase)))
        {
            return TrayApplication.RequestShutdown();
        }

        if (args.Any(argument => string.Equals(argument, "--disk-settings", StringComparison.OrdinalIgnoreCase)))
        {
            return TrayApplication.RequestOpenDiskSettings();
        }

        using TrayApplication application = new();
        return application.Run();
    }
}

internal enum TrayIconKind
{
    Codex,
    Claude,
    Kimi,
    DeepSeek,
    Disk,
    OpenCode,
    Temperature,
    CpuGpuLoad,
    Devin,
    OpenRouter
}

internal sealed class TrayApplication : IDisposable
{
    private const uint CodexTrayIconId = 1;
    private const uint ClaudeTrayIconId = 2;
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 1;
    private const uint CodexResultMessage = NativeMethods.WM_APP + 2;
    private const uint ClaudeResultMessage = NativeMethods.WM_APP + 3;
    private const uint ShutdownMessage = NativeMethods.WM_APP + 4;
    private const uint KimiResultMessage = NativeMethods.WM_APP + 5;
    private const uint DeepSeekResultMessage = NativeMethods.WM_APP + 6;
    private const uint DiskResultMessage = NativeMethods.WM_APP + 7;
    private const uint OpenDiskSettingsMessage = NativeMethods.WM_APP + 8;
    private const uint OpenCodeResultMessage = NativeMethods.WM_APP + 9;
    private const uint HardwareResultMessage = NativeMethods.WM_APP + 10;
    private const uint UnetResultMessage = NativeMethods.WM_APP + 11;
    private const uint DevinResultMessage = NativeMethods.WM_APP + 12;
    private const uint OpenRouterResultMessage = NativeMethods.WM_APP + 13;
    private const uint KimiTrayIconId = 3;
    private const uint DeepSeekTrayIconId = 4;
    private const uint DiskTrayIconId = 5;
    private const uint OpenCodeTrayIconId = 6;
    private const uint TemperatureTrayIconId = 7;
    private const uint MemoryTrayIconId = 8;
    private const uint DevinTrayIconId = 9;
    private const uint OpenRouterTrayIconId = 10;
    private const nuint RefreshTimerId = 1;
    private const nuint HardwareRefreshTimerId = 2;
    private const nuint UsageBalloonDelayTimerId = 3;
    private const nuint UsageBalloonMonitorTimerId = 4;
    private const uint RefreshIntervalMs = 300_000;
    private const uint HardwareRefreshIntervalMs = 10_000;
    private const uint UsageBalloonDelayMs = 250;
    private const uint UsageBalloonMonitorIntervalMs = 100;
    private const int ClaudeRefreshTimeoutSeconds = 25;
    private const uint CommandRefresh = 1001;
    private const uint CommandOpenCodexSessions = 1002;
    private const uint CommandExit = 1003;
    private const uint CommandOpenClaudeUsage = 1004;
    private const uint CommandOpenKimiSessions = 1005;
    private const uint CommandOpenDeepSeekBilling = 1006;
    private const uint CommandOpenDiskSettings = 1007;
    private const uint CommandOpenOpenCodeData = 1008;
    private const uint CommandOpenTaskManager = 1009;
    private const uint CommandOpenDevinUsage = 1010;
    private const uint CommandOpenOpenRouterCredits = 1011;
    private const uint CommandToggleFirstTrayIcon = 1100;
    private const uint CommandToggleLastTrayIcon = CommandToggleFirstTrayIcon + (uint)TrayIconKind.OpenRouter;
    private const string ShutdownEventName = @"Local\Limits.Shutdown";
    private const string OpenDiskSettingsEventName = @"Local\Limits.OpenDiskSettings";

    private static readonly Guid CodexTrayIconGuid = new("2a642a8d-169a-4035-ad86-ea43b5e87764");
    private static readonly Guid ClaudeTrayIconGuid = new("4654b565-47c7-49af-a257-8f26d82c0ec0");
    private static readonly Guid KimiTrayIconGuid = new("918bd040-6a80-4b43-ae66-13a8f5bb1d57");
    private static readonly Guid DeepSeekTrayIconGuid = new("36f5599d-63ad-4d36-b75d-8498b2df37bf");
    private static readonly Guid DiskTrayIconGuid = new("7f0a7c1f-5d91-4c97-aebf-6e0b4d4e2e1c");
    private static readonly Guid OpenCodeTrayIconGuid = new("c8d1d0c3-5b6a-4c0e-9a73-96abf4c7785e");
    private static readonly Guid TemperatureTrayIconGuid = new("a1d68e24-7e92-4bcb-a2bf-13f8a7e8c6d1");
    private static readonly Guid MemoryTrayIconGuid = new("b2e79f35-8fa3-4cdc-b3c0-24a9b8f9d7e2");
    private static readonly Guid DevinTrayIconGuid = new("d3b6c0a4-7e21-4b50-9e1f-8e7d8dc33b2a");
    private static readonly Guid OpenRouterTrayIconGuid = new("d239a2d5-61f8-41a3-ae06-320bda542c1c");
    private static readonly TrayIconKind[] TrayIconsInVisibilityMenu =
    [
        TrayIconKind.Codex,
        TrayIconKind.Claude,
        TrayIconKind.Kimi,
        TrayIconKind.DeepSeek,
        TrayIconKind.Disk,
        TrayIconKind.OpenCode,
        TrayIconKind.Temperature,
        TrayIconKind.CpuGpuLoad,
        TrayIconKind.Devin,
        TrayIconKind.OpenRouter
    ];

    private static readonly NativeMethods.WndProcDelegate WindowProcedure = HandleWindowMessage;
    private static TrayApplication? Current;

    private readonly object _shellNotifyQueueLock = new();
    private readonly CodexUsageReader _codexUsageReader = new();
    private readonly ClaudeUsageReader _claudeUsageReader = new();
    private readonly KimiUsageReader _kimiUsageReader = new();
    private readonly DeepSeekBalanceReader _deepSeekBalanceReader = new();
    private readonly UnetBalanceReader _unetBalanceReader = new();
    private readonly OpenCodeUsageReader _openCodeUsageReader = new();
    private readonly DevinUsageReader _devinUsageReader = new();
    private readonly OpenRouterBalanceReader _openRouterBalanceReader = new();
    private readonly HardwareMonitor _hardwareMonitor = new();
    private readonly DiskMonitor _diskMonitor = new();
    private readonly TrayIconSettingsStore _trayIconSettingsStore = new();
    private readonly CounterWebSocketServer _counterWebSocketServer = new();
    private readonly LimitWatchdog _limitWatchdog;
    private readonly string _windowClassName = $"limits.{Environment.ProcessId}";
    private readonly string _usageBalloonWindowClassName = $"limits.usage-balloon.{Environment.ProcessId}";
    private readonly EventWaitHandle _shutdownEvent;
    private readonly RegisteredWaitHandle _shutdownRegistration;
    private readonly EventWaitHandle _openDiskSettingsEvent;
    private readonly RegisteredWaitHandle _openDiskSettingsRegistration;
    private readonly uint _taskbarCreatedMessage;
    private Task _shellNotifyQueue = Task.CompletedTask;
    private NativeMethods.WndProcDelegate? _usageBalloonWindowProcedure;

    private IntPtr _windowHandle;
    private IntPtr _usageBalloonWindowHandle;
    private IntPtr _codexIconHandle;
    private IntPtr _claudeIconHandle;
    private IntPtr _kimiIconHandle;
    private IntPtr _deepSeekIconHandle;
    private IntPtr _diskIconHandle;
    private IntPtr _openCodeIconHandle;
    private IntPtr _temperatureIconHandle;
    private IntPtr _memoryIconHandle;
    private IntPtr _devinIconHandle;
    private IntPtr _openRouterIconHandle;
    private bool _codexTrayIconAdded;
    private bool _claudeTrayIconAdded;
    private bool _kimiTrayIconAdded;
    private bool _deepSeekTrayIconAdded;
    private bool _diskTrayIconAdded;
    private bool _openCodeTrayIconAdded;
    private bool _temperatureTrayIconAdded;
    private bool _memoryTrayIconAdded;
    private bool _devinTrayIconAdded;
    private bool _openRouterTrayIconAdded;
    private bool _windowClassRegistered;
    private bool _usageBalloonWindowClassRegistered;
    private uint _hoverIconId;
    private uint _usageBalloonIconId;
    private UsageBalloonContent? _usageBalloonContent;
    private string? _codexIconKey;
    private string? _claudeIconKey;
    private string? _kimiIconKey;
    private string? _deepSeekIconKey;
    private string? _diskIconKey;
    private string? _openCodeIconKey;
    private string? _temperatureIconKey;
    private string? _memoryIconKey;
    private string? _devinIconKey;
    private string? _openRouterIconKey;
    private string? _codexAppliedTooltip;
    private string? _claudeAppliedTooltip;
    private string? _kimiAppliedTooltip;
    private string? _deepSeekAppliedTooltip;
    private string? _diskAppliedTooltip;
    private string? _openCodeAppliedTooltip;
    private string? _temperatureAppliedTooltip;
    private string? _memoryAppliedTooltip;
    private string? _devinAppliedTooltip;
    private string? _openRouterAppliedTooltip;
    private string _codexTooltip = "limits";
    private string _codexStatusText = "Loading Codex usage...";
    private string _codexDetailText = "Reading Codex account usage.";
    private string _codexSparkUsageText = "Spark usage: loading...";
    private string _codexUpdatedText = string.Empty;
    private string _codexSourceText = string.Empty;
    private CodexUsageSnapshot? _lastCodexSnapshot;
    private CodexUsageSnapshot? _lastCodexSparkSnapshot;
    private string _claudeTooltip = "limits";
    private string _claudeStatusText = "Loading Claude usage...";
    private string _claudeDetailText = "Reading Claude OAuth usage.";
    private string _claudeUpdatedText = string.Empty;
    private string _claudeSourceText = string.Empty;
    private ClaudeUsageSnapshot? _lastClaudeSnapshot;
    private string _kimiTooltip = "limits";
    private string _kimiStatusText = "Loading Kimi usage...";
    private string _kimiDetailText = "Reading Kimi Code quota.";
    private string _kimiTokenUsageText = "Kimi tokens: loading...";
    private string _kimiUpdatedText = string.Empty;
    private string _kimiSourceText = string.Empty;
    private KimiUsageSnapshot? _lastKimiSnapshot;
    private string _deepSeekTooltip = "limits";
    private string _deepSeekStatusText = "Loading DeepSeek balance...";
    private string _deepSeekDetailText = "Reading DeepCode configuration.";
    private string _deepSeekUpdatedText = string.Empty;
    private string _deepSeekSourceText = string.Empty;
    private string _diskTooltip = "limits";
    private string _diskStatusText = "Loading disk space...";
    private string _diskDetailText = "Reading selected disk space limits.";
    private string _diskUpdatedText = string.Empty;
    private string _diskSourceText = string.Empty;
    private string _openCodeTooltip = "limits";
    private string _openCodeStatusText = "Loading OpenCode Go quota...";
    private string _openCodeDetailText = "Reading OpenCode token statistics and Go quota.";
    private string _openCodeUpdatedText = string.Empty;
    private string _openCodeSourceText = string.Empty;
    private string _temperatureTooltip = "limits";
    private string _temperatureStatusText = "Loading CPU/GPU temperature...";
    private string _temperatureDetailText = "Reading hardware temperature sensors.";
    private string _temperatureUpdatedText = string.Empty;
    private string _temperatureSourceText = string.Empty;
    private string _memoryTooltip = "limits";
    private string _memoryStatusText = "Loading CPU/GPU/RAM/VRAM usage...";
    private string _memoryDetailText = "Reading CPU, GPU, RAM, and VRAM utilization.";
    private string _memoryUpdatedText = string.Empty;
    private string _memorySourceText = string.Empty;
    private string _devinTooltip = "limits";
    private string _devinStatusText = "Loading Devin quota...";
    private string _devinDetailText = "Reading Devin daily and weekly quota.";
    private string _devinUpdatedText = string.Empty;
    private string _devinSourceText = string.Empty;
    private string _openRouterTooltip = "limits";
    private string _openRouterStatusText = "Loading OpenRouter balance...";
    private string _openRouterDetailText = "Reading OpenRouter credits and key limit.";
    private string _openRouterUpdatedText = string.Empty;
    private string _openRouterSourceText = string.Empty;
    private DeepSeekBalanceSnapshot? _lastDeepSeekSnapshot;
    private DiskSpaceSnapshot? _lastDiskSnapshot;
    private OpenCodeUsageSnapshot? _lastOpenCodeSnapshot;
    private OpenCodeGoUsageSnapshot? _lastOpenCodeGoSnapshot;
    private string? _openCodeGoUsageError;
    private DevinUsageSnapshot? _lastDevinSnapshot;
    private OpenRouterBalanceSnapshot? _lastOpenRouterSnapshot;
    private HardwareSnapshot? _lastHardwareSnapshot;
    private IReadOnlyList<RamProcessUsage> _lastTopRamProcesses = [];
    private UnetBalanceSnapshot? _lastUnetSnapshot;
    private volatile bool _codexRefreshInFlight;
    private volatile UsageReadResult? _pendingCodexResult;
    private volatile bool _claudeRefreshInFlight;
    private volatile ClaudeUsageReadResult? _pendingClaudeResult;
    private volatile bool _kimiRefreshInFlight;
    private volatile KimiUsageReadResult? _pendingKimiResult;
    private volatile bool _deepSeekRefreshInFlight;
    private volatile DeepSeekBalanceReadResult? _pendingDeepSeekResult;
    private volatile bool _unetRefreshInFlight;
    private volatile UnetBalanceReadResult? _pendingUnetResult;
    private volatile DiskSpaceSnapshot? _pendingDiskResult;
    private volatile bool _openCodeRefreshInFlight;
    private volatile OpenCodeUsageReadResult? _pendingOpenCodeResult;
    private volatile bool _hardwareRefreshInFlight;
    private volatile HardwareReadResult? _pendingHardwareResult;
    private volatile bool _devinRefreshInFlight;
    private volatile DevinUsageReadResult? _pendingDevinResult;
    private volatile bool _openRouterRefreshInFlight;
    private volatile OpenRouterBalanceReadResult? _pendingOpenRouterResult;
    private volatile bool _limitWatchdogInFlight;
    private volatile bool _diskRefreshInFlight;
    private readonly Dictionary<string, DateTimeOffset> _lastDiskAlertAt = new(StringComparer.OrdinalIgnoreCase);
    private DiskSettingsWindow? _diskSettingsWindow;
    private TrayIconSettings _trayIconSettings;

    public TrayApplication()
    {
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
            _shutdownEvent,
            static (state, timedOut) =>
            {
                if (timedOut || state is not TrayApplication application)
                {
                    return;
                }

                IntPtr windowHandle = application._windowHandle;
                if (windowHandle != IntPtr.Zero)
                {
                    NativeMethods.PostMessage(windowHandle, ShutdownMessage, IntPtr.Zero, IntPtr.Zero);
                }
            },
            this,
            -1,
            executeOnlyOnce: false);
        _openDiskSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenDiskSettingsEventName);
        _openDiskSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(
            _openDiskSettingsEvent,
            static (state, timedOut) =>
            {
                if (timedOut || state is not TrayApplication application)
                {
                    return;
                }

                IntPtr windowHandle = application._windowHandle;
                if (windowHandle != IntPtr.Zero)
                {
                    NativeMethods.PostMessage(windowHandle, OpenDiskSettingsMessage, IntPtr.Zero, IntPtr.Zero);
                }
            },
            this,
            -1,
            executeOnlyOnce: false);
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _trayIconSettings = _trayIconSettingsStore.Load();
        _limitWatchdog = new LimitWatchdog(_diskMonitor);
    }

    public static int RequestShutdown()
    {
        try
        {
            using EventWaitHandle shutdownEvent = EventWaitHandle.OpenExisting(ShutdownEventName);
            shutdownEvent.Set();
            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }
        catch (IOException)
        {
            return 3;
        }
    }

    public static int RequestOpenDiskSettings()
    {
        try
        {
            using EventWaitHandle openSettingsEvent = EventWaitHandle.OpenExisting(OpenDiskSettingsEventName);
            openSettingsEvent.Set();
            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }
        catch (IOException)
        {
            return 3;
        }
    }

    public int Run()
    {
        Current = this;
        RegisterWindowClass();
        CreateMessageWindow();
        _counterWebSocketServer.Start(BuildCounterJson());
        RefreshUsage();
        NativeMethods.SetTimer(_windowHandle, RefreshTimerId, RefreshIntervalMs, IntPtr.Zero);
        NativeMethods.SetTimer(_windowHandle, HardwareRefreshTimerId, HardwareRefreshIntervalMs, IntPtr.Zero);

        while (NativeMethods.GetMessage(out NativeMethods.MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        return 0;
    }

    public void Dispose()
    {
        _counterWebSocketServer.Dispose();

        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_windowHandle);
        }

        CleanupNativeResources();
        _shutdownRegistration.Unregister(null);
        _shutdownEvent.Dispose();
        _openDiskSettingsRegistration.Unregister(null);
        _openDiskSettingsEvent.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RegisterWindowClass()
    {
        NativeMethods.WNDCLASSEX windowClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = _windowClassName
        };

        ushort atom = NativeMethods.RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _windowClassRegistered = true;

        _usageBalloonWindowProcedure = HandleUsageBalloonWindowMessage;
        NativeMethods.WNDCLASSEX usageBalloonWindowClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_usageBalloonWindowProcedure),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = _usageBalloonWindowClassName
        };

        ushort usageBalloonAtom = NativeMethods.RegisterClassEx(ref usageBalloonWindowClass);
        if (usageBalloonAtom == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx for usage balloon failed: {Marshal.GetLastWin32Error()}");
        }

        _usageBalloonWindowClassRegistered = true;
    }

    private void CreateMessageWindow()
    {
        _windowHandle = NativeMethods.CreateWindowEx(
            0,
            _windowClassName,
            "limits",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        _usageBalloonWindowHandle = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
            _usageBalloonWindowClassName,
            string.Empty,
            NativeMethods.WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_usageBalloonWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx for usage balloon failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private void RefreshUsage()
    {
        RefreshCodexUsage();
        RefreshClaudeUsage();
        RefreshKimiUsage();
        RefreshDeepSeekBalance();
        RefreshUnetBalances();
        RefreshDiskSpace();
        RefreshOpenCodeUsage();
        RefreshDevinUsage();
        RefreshOpenRouterBalance();
        RefreshHardware();
    }

    private void RefreshUnetBalances()
    {
        if (_unetRefreshInFlight)
        {
            return;
        }

        _unetRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            UnetBalanceReadResult result;
            try
            {
                result = _unetBalanceReader.ReadLatestSnapshot();
            }
            catch (Exception)
            {
                result = new UnetBalanceReadResult(null, "UNET balance refresh failed.");
            }

            _pendingUnetResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, UnetResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _unetRefreshInFlight = false;
            }
        });
    }

    private void ApplyUnetResult()
    {
        UnetBalanceReadResult? result = _pendingUnetResult;
        _pendingUnetResult = null;
        _unetRefreshInFlight = false;

        if (result is not null)
        {
            _lastUnetSnapshot = result.Snapshot;
        }
    }

    private void RefreshDiskSpace()
    {
        if (_diskRefreshInFlight)
        {
            return;
        }

        _diskRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            DiskSpaceSnapshot result;
            try
            {
                result = _diskMonitor.ReadSnapshot();
            }
            catch (Exception exception)
            {
                result = _diskMonitor.CreateErrorSnapshot(exception.Message);
            }

            _pendingDiskResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, DiskResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _diskRefreshInFlight = false;
            }
        });
    }

    private void ApplyDiskResult()
    {
        DiskSpaceSnapshot? result = _pendingDiskResult;
        _pendingDiskResult = null;
        _diskRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        _lastDiskSnapshot = result;
        _diskTooltip = BuildDiskTooltip(result);
        _diskStatusText = BuildDiskHeadline(result);
        _diskDetailText = BuildDiskDetail(result);
        _diskUpdatedText = $"Checked {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
        _diskSourceText = _diskMonitor.SettingsPath;
        // Keep the notification area quiet unless a selected drive is actually
        // at or below its red free-space limit.
        if (!string.IsNullOrWhiteSpace(result.LowDriveLetters))
        {
            UpdateDiskTrayIcon(TrayIconRenderer.CreateDiskIcon(result), TrayIconRenderer.GetDiskIconKey(result));
        }
        else
        {
            HideDiskTrayIcon();
        }
        AlertDiskLimits(result);
        RunLimitWatchdog(_lastClaudeSnapshot);
    }

    private void RefreshCodexUsage()
    {
        if (_codexRefreshInFlight)
        {
            return;
        }

        _codexRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        // Local session folders can contain many JSONL files. Keep that scan off the
        // tray window thread so Explorer is never blocked by notification callbacks.
        Task.Run(() =>
        {
            UsageReadResult result;
            try
            {
                result = _codexUsageReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new UsageReadResult(null, null, exception.Message);
            }

            _pendingCodexResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, CodexResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _codexRefreshInFlight = false;
            }
        });
    }

    private void ApplyCodexResult()
    {
        UsageReadResult? result = _pendingCodexResult;
        _pendingCodexResult = null;
        _codexRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            if (result.SparkSnapshot is not null)
            {
                _lastCodexSparkSnapshot = result.SparkSnapshot;
            }

            _codexTooltip = "Codex: usage unavailable";
            _codexStatusText = "No Codex usage data found";
            _codexDetailText = result.ErrorMessage ?? "No token_count events were found.";
            _codexSparkUsageText = BuildSparkUsage(result.SparkSnapshot);
            _codexUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _codexSourceText = _codexUsageReader.SessionsPath;
            UpdateCodexTrayIcon(TrayIconRenderer.CreateUnavailableIcon(), TrayIconRenderer.CodexUnavailableIconKey);
            return;
        }

        CodexUsageSnapshot snapshot = result.Snapshot;
        _lastCodexSnapshot = snapshot;
        if (result.SparkSnapshot is not null)
        {
            _lastCodexSparkSnapshot = result.SparkSnapshot;
        }
        _codexTooltip = BuildCodexTooltip(snapshot);
        _codexStatusText = BuildCodexHeadline(snapshot);
        _codexDetailText = BuildCodexDetail(snapshot);
        _codexSparkUsageText = BuildSparkUsage(result.SparkSnapshot);
        _codexUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _codexSourceText = snapshot.SourceFile;
        UpdateCodexTrayIcon(TrayIconRenderer.CreateUsageIcon(snapshot), TrayIconRenderer.GetCodexIconKey(snapshot));
    }

    private void RefreshClaudeUsage()
    {
        if (_claudeRefreshInFlight)
        {
            return;
        }

        _claudeRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        // Reading Claude usage is a network call that can take seconds. Doing it on the
        // message-loop thread would freeze both tray icons and stall the shell, because
        // Explorer SendMessages to notification-icon owner windows and blocks when the
        // owner stops pumping. Fetch off-thread and post the result back to the UI thread.
        Task.Run(() =>
        {
            ClaudeUsageReadResult result = ReadClaudeUsageWithTimeout();

            _pendingClaudeResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, ClaudeResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _claudeRefreshInFlight = false;
            }
        });
    }

    private ClaudeUsageReadResult ReadClaudeUsageWithTimeout()
    {
        Task<ClaudeUsageReadResult> readTask = Task.Run(() =>
        {
            try
            {
                return _claudeUsageReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                return new ClaudeUsageReadResult(null, exception.Message);
            }
        });

        try
        {
            return readTask.Wait(TimeSpan.FromSeconds(ClaudeRefreshTimeoutSeconds))
                ? readTask.Result
                : new ClaudeUsageReadResult(null, $"Claude usage request timed out after {ClaudeRefreshTimeoutSeconds}s.");
        }
        catch (AggregateException exception)
        {
            return new ClaudeUsageReadResult(null, exception.InnerException?.Message ?? exception.Message);
        }
    }

    private void ApplyClaudeResult()
    {
        ClaudeUsageReadResult? result = _pendingClaudeResult;
        _pendingClaudeResult = null;
        _claudeRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            if (_lastClaudeSnapshot is { } lastSnapshot)
            {
                _claudeTooltip = BuildClaudeStaleTooltip(lastSnapshot);
                _claudeStatusText = $"{BuildClaudeHeadline(lastSnapshot)} - refresh failed";
                _claudeDetailText = result.ErrorMessage ?? "Claude usage refresh failed.";
                _claudeUpdatedText = $"Last good {lastSnapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                _claudeSourceText = _claudeUsageReader.CredentialsPath;
                UpdateClaudeTrayIcon(
                    TrayIconRenderer.CreateClaudeIcon(lastSnapshot),
                    TrayIconRenderer.GetClaudeIconKey(lastSnapshot));
                RunLimitWatchdog(lastSnapshot);
                return;
            }

            _claudeTooltip = "Claude: usage unavailable";
            _claudeStatusText = "No Claude usage data found";
            _claudeDetailText = result.ErrorMessage ?? "Claude usage limits were not found.";
            _claudeUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _claudeSourceText = _claudeUsageReader.CredentialsPath;
            UpdateClaudeTrayIcon(TrayIconRenderer.CreateClaudeUnavailableIcon(), TrayIconRenderer.ClaudeUnavailableIconKey);
            RunLimitWatchdog(null);
            return;
        }

        ClaudeUsageSnapshot snapshot = result.Snapshot;
        _lastClaudeSnapshot = snapshot;
        _claudeTooltip = BuildClaudeTooltip(snapshot);
        _claudeStatusText = BuildClaudeHeadline(snapshot);
        _claudeDetailText = BuildClaudeDetail(snapshot);
        _claudeUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _claudeSourceText = snapshot.SourceFile;
        UpdateClaudeTrayIcon(TrayIconRenderer.CreateClaudeIcon(snapshot), TrayIconRenderer.GetClaudeIconKey(snapshot));
        RunLimitWatchdog(snapshot);
    }

    private void RefreshKimiUsage()
    {
        if (_kimiRefreshInFlight)
        {
            return;
        }

        _kimiRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            KimiUsageReadResult result;
            try
            {
                result = _kimiUsageReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new KimiUsageReadResult(null, exception.Message);
            }

            _pendingKimiResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, KimiResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _kimiRefreshInFlight = false;
            }
        });
    }

    private void ApplyKimiResult()
    {
        KimiUsageReadResult? result = _pendingKimiResult;
        _pendingKimiResult = null;
        _kimiRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            if (_lastKimiSnapshot is { } lastSnapshot)
            {
                _kimiTooltip = BuildKimiStaleTooltip(lastSnapshot);
                _kimiStatusText = $"{BuildKimiHeadline(lastSnapshot)} - refresh failed";
                _kimiDetailText = result.ErrorMessage ?? "Kimi Code quota refresh failed.";
                _kimiTokenUsageText = BuildKimiTokenUsage(lastSnapshot);
                _kimiUpdatedText = $"Last seen {lastSnapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}; checked {DateTimeOffset.Now:HH:mm:ss}";
                _kimiSourceText = _kimiUsageReader.UsageEndpoint;
                UpdateKimiTrayIcon(TrayIconRenderer.CreateKimiIcon(lastSnapshot), TrayIconRenderer.GetKimiIconKey(lastSnapshot));
                return;
            }

            _kimiTooltip = BuildKimiUnavailableTooltip(result.ErrorMessage);
            _kimiStatusText = "No Kimi usage data found";
            _kimiDetailText = result.ErrorMessage ?? "Kimi Code quota was not found.";
            _kimiTokenUsageText = "Kimi tokens: unavailable";
            _kimiUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _kimiSourceText = _kimiUsageReader.UsageEndpoint;
            UpdateKimiTrayIcon(TrayIconRenderer.CreateKimiUnavailableIcon(), TrayIconRenderer.KimiUnavailableIconKey);
            return;
        }

        KimiUsageSnapshot snapshot = result.Snapshot;
        _lastKimiSnapshot = snapshot;
        _kimiTooltip = BuildKimiTooltip(snapshot);
        _kimiStatusText = BuildKimiHeadline(snapshot);
        _kimiDetailText = BuildKimiDetail(snapshot);
        _kimiTokenUsageText = BuildKimiTokenUsage(snapshot);
        _kimiUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _kimiSourceText = snapshot.SourceFile;
        UpdateKimiTrayIcon(TrayIconRenderer.CreateKimiIcon(snapshot), TrayIconRenderer.GetKimiIconKey(snapshot));
    }

    private void RefreshDeepSeekBalance()
    {
        if (_deepSeekRefreshInFlight)
        {
            return;
        }

        _deepSeekRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            DeepSeekBalanceReadResult result;
            try
            {
                result = _deepSeekBalanceReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new DeepSeekBalanceReadResult(null, exception.Message);
            }

            _pendingDeepSeekResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, DeepSeekResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _deepSeekRefreshInFlight = false;
            }
        });
    }

    private void ApplyDeepSeekResult()
    {
        DeepSeekBalanceReadResult? result = _pendingDeepSeekResult;
        _pendingDeepSeekResult = null;
        _deepSeekRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            if (_lastDeepSeekSnapshot is { } lastSnapshot)
            {
                _deepSeekTooltip = $"{BuildDeepSeekTooltip(lastSnapshot)} (last known)";
                _deepSeekStatusText = $"{BuildDeepSeekHeadline(lastSnapshot)} - refresh failed";
                _deepSeekDetailText = result.ErrorMessage ?? "DeepSeek balance refresh failed.";
                _deepSeekUpdatedText = $"Last seen {lastSnapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}; checked {DateTimeOffset.Now:HH:mm:ss}";
                _deepSeekSourceText = _deepSeekBalanceReader.SettingsPath;
                UpdateDeepSeekTrayIcon(
                    TrayIconRenderer.CreateDeepSeekIcon(lastSnapshot),
                    TrayIconRenderer.GetDeepSeekIconKey(lastSnapshot));
                return;
            }

            _deepSeekTooltip = BuildDeepSeekUnavailableTooltip(result.ErrorMessage);
            _deepSeekStatusText = "No DeepSeek balance data found";
            _deepSeekDetailText = result.ErrorMessage ?? "DeepCode configuration was not found.";
            _deepSeekUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _deepSeekSourceText = _deepSeekBalanceReader.SettingsPath;
            UpdateDeepSeekTrayIcon(
                TrayIconRenderer.CreateDeepSeekUnavailableIcon(),
                TrayIconRenderer.DeepSeekUnavailableIconKey);
            return;
        }

        DeepSeekBalanceSnapshot snapshot = result.Snapshot;
        _lastDeepSeekSnapshot = snapshot;
        _deepSeekTooltip = BuildDeepSeekTooltip(snapshot);
        _deepSeekStatusText = BuildDeepSeekHeadline(snapshot);
        _deepSeekDetailText = BuildDeepSeekDetail(snapshot);
        _deepSeekUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _deepSeekSourceText = snapshot.SourceFile;
        UpdateDeepSeekTrayIcon(
            TrayIconRenderer.CreateDeepSeekIcon(snapshot),
            TrayIconRenderer.GetDeepSeekIconKey(snapshot));
    }

    private void RefreshOpenCodeUsage()
    {
        if (_openCodeRefreshInFlight)
        {
            return;
        }

        _openCodeRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            OpenCodeUsageReadResult result;
            try
            {
                result = _openCodeUsageReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new OpenCodeUsageReadResult(
                    null,
                    null,
                    "OpenCode Go quota refresh failed.",
                    exception.Message);
            }

            _pendingOpenCodeResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, OpenCodeResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _openCodeRefreshInFlight = false;
            }
        });
    }

    private void ApplyOpenCodeResult()
    {
        OpenCodeUsageReadResult? result = _pendingOpenCodeResult;
        _pendingOpenCodeResult = null;
        _openCodeRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.GoUsage is not null)
        {
            _lastOpenCodeGoSnapshot = result.GoUsage;
            _openCodeGoUsageError = null;
        }
        else if (!string.IsNullOrWhiteSpace(result.GoUsageError))
        {
            _openCodeGoUsageError = result.GoUsageError;
        }

        OpenCodeGoUsageSnapshot? goSnapshot = _lastOpenCodeGoSnapshot;
        if (result.Snapshot is null)
        {
            if (goSnapshot is not null)
            {
                _openCodeTooltip = BuildOpenCodeGoTooltip(goSnapshot, isStale: result.GoUsage is null);
                _openCodeStatusText = BuildOpenCodeGoHeadline(goSnapshot);
                _openCodeDetailText = result.ErrorMessage is null
                    ? BuildOpenCodeGoDetail(goSnapshot)
                    : $"{result.ErrorMessage}; {BuildOpenCodeGoDetail(goSnapshot)}";
                _openCodeUpdatedText = $"Seen {goSnapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                _openCodeSourceText = goSnapshot.Source;
                UpdateOpenCodeTrayIcon(
                    TrayIconRenderer.CreateOpenCodeIcon(goSnapshot),
                    TrayIconRenderer.GetOpenCodeIconKey(goSnapshot));
                return;
            }

            _openCodeTooltip = BuildOpenCodeUnavailableTooltip(result.ErrorMessage);
            _openCodeStatusText = "No OpenCode usage data found";
            _openCodeDetailText = result.ErrorMessage ?? "OpenCode statistics were not found.";
            _openCodeUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _openCodeSourceText = _openCodeUsageReader.DatabasePath;
            UpdateOpenCodeTrayIcon(
                TrayIconRenderer.CreateOpenCodeUnavailableIcon(),
                TrayIconRenderer.OpenCodeUnavailableIconKey);
            return;
        }

        OpenCodeUsageSnapshot snapshot = result.Snapshot;
        _lastOpenCodeSnapshot = snapshot;
        _openCodeTooltip = BuildOpenCodeTooltip(snapshot, goSnapshot, _openCodeGoUsageError);
        _openCodeStatusText = BuildOpenCodeHeadline(snapshot, goSnapshot);
        _openCodeDetailText = BuildOpenCodeDetail(snapshot, goSnapshot, _openCodeGoUsageError);
        _openCodeUpdatedText = $"Checked {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _openCodeSourceText = goSnapshot?.Source ?? snapshot.SourceFile;
        if (goSnapshot is not null)
        {
            UpdateOpenCodeTrayIcon(
                TrayIconRenderer.CreateOpenCodeIcon(goSnapshot),
                TrayIconRenderer.GetOpenCodeIconKey(goSnapshot));
        }
        else
        {
            UpdateOpenCodeTrayIcon(
                TrayIconRenderer.CreateOpenCodeUnavailableIcon(),
                TrayIconRenderer.OpenCodeUnavailableIconKey);
        }
    }

    private void RefreshDevinUsage()
    {
        if (_devinRefreshInFlight)
        {
            return;
        }

        _devinRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            DevinUsageReadResult result;
            try
            {
                result = _devinUsageReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new DevinUsageReadResult(null, exception.Message);
            }

            _pendingDevinResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, DevinResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _devinRefreshInFlight = false;
            }
        });
    }

    private void ApplyDevinResult()
    {
        DevinUsageReadResult? result = _pendingDevinResult;
        _pendingDevinResult = null;
        _devinRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            if (_lastDevinSnapshot is { } lastSnapshot)
            {
                _devinTooltip = BuildDevinTooltip(lastSnapshot, isStale: true);
                _devinStatusText = $"{BuildDevinHeadline(lastSnapshot)} - refresh failed";
                _devinDetailText = result.ErrorMessage ?? "Devin quota refresh failed.";
                _devinUpdatedText = $"Last known {lastSnapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                _devinSourceText = lastSnapshot.Source;
                UpdateDevinTrayIcon(
                    TrayIconRenderer.CreateDevinIcon(lastSnapshot),
                    TrayIconRenderer.GetDevinIconKey(lastSnapshot));
                return;
            }

            _devinTooltip = BuildDevinUnavailableTooltip(result.ErrorMessage);
            _devinStatusText = "No Devin quota data found";
            _devinDetailText = result.ErrorMessage ?? "Devin daily and weekly quota was not found.";
            _devinUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _devinSourceText = _devinUsageReader.UsageEndpoint;
            UpdateDevinTrayIcon(
                TrayIconRenderer.CreateDevinUnavailableIcon(),
                TrayIconRenderer.DevinUnavailableIconKey);
            return;
        }

        DevinUsageSnapshot snapshot = result.Snapshot;
        _lastDevinSnapshot = snapshot;
        _devinTooltip = BuildDevinTooltip(snapshot, isStale: false);
        _devinStatusText = BuildDevinHeadline(snapshot);
        _devinDetailText = BuildDevinDetail(snapshot);
        _devinUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _devinSourceText = snapshot.Source;
        UpdateDevinTrayIcon(
            TrayIconRenderer.CreateDevinIcon(snapshot),
            TrayIconRenderer.GetDevinIconKey(snapshot));
    }

    private void RefreshOpenRouterBalance()
    {
        if (_openRouterRefreshInFlight)
        {
            return;
        }

        _openRouterRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            OpenRouterBalanceReadResult result;
            try
            {
                result = _openRouterBalanceReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new OpenRouterBalanceReadResult(null, exception.Message);
            }

            _pendingOpenRouterResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, OpenRouterResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _openRouterRefreshInFlight = false;
            }
        });
    }

    private void ApplyOpenRouterResult()
    {
        OpenRouterBalanceReadResult? result = _pendingOpenRouterResult;
        _pendingOpenRouterResult = null;
        _openRouterRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            if (_lastOpenRouterSnapshot is { } lastSnapshot)
            {
                _openRouterTooltip = $"{BuildOpenRouterTooltip(lastSnapshot)} (last known)";
                _openRouterStatusText = $"{BuildOpenRouterHeadline(lastSnapshot)} - refresh failed";
                _openRouterDetailText = result.ErrorMessage ?? "OpenRouter balance refresh failed.";
                _openRouterUpdatedText = $"Last known {lastSnapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                _openRouterSourceText = lastSnapshot.Source;
                UpdateOpenRouterTrayIcon(
                    TrayIconRenderer.CreateOpenRouterIcon(lastSnapshot),
                    TrayIconRenderer.GetOpenRouterIconKey(lastSnapshot));
                return;
            }

            _openRouterTooltip = BuildOpenRouterUnavailableTooltip(result.ErrorMessage);
            _openRouterStatusText = "No OpenRouter balance data found";
            _openRouterDetailText = result.ErrorMessage ?? "OpenRouter credits were not found.";
            _openRouterUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _openRouterSourceText = _openRouterBalanceReader.UsageEndpoint;
            UpdateOpenRouterTrayIcon(
                TrayIconRenderer.CreateOpenRouterUnavailableIcon(),
                TrayIconRenderer.OpenRouterUnavailableIconKey);
            return;
        }

        OpenRouterBalanceSnapshot snapshot = result.Snapshot;
        _lastOpenRouterSnapshot = snapshot;
        _openRouterTooltip = BuildOpenRouterTooltip(snapshot);
        _openRouterStatusText = BuildOpenRouterHeadline(snapshot);
        _openRouterDetailText = BuildOpenRouterDetail(snapshot);
        _openRouterUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _openRouterSourceText = snapshot.Source;
        UpdateOpenRouterTrayIcon(
            TrayIconRenderer.CreateOpenRouterIcon(snapshot),
            TrayIconRenderer.GetOpenRouterIconKey(snapshot));
    }

    private void RefreshHardware()
    {
        if (_hardwareRefreshInFlight)
        {
            return;
        }

        _hardwareRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            HardwareReadResult result;
            try
            {
                result = _hardwareMonitor.ReadSnapshot();
                result = result with { TopRamProcesses = RamProcessReader.ReadTopProcesses() };
            }
            catch (Exception exception)
            {
                result = new HardwareReadResult(null, exception.Message);
            }

            _pendingHardwareResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, HardwareResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _hardwareRefreshInFlight = false;
            }
        });
    }

    private void ApplyHardwareResult()
    {
        HardwareReadResult? result = _pendingHardwareResult;
        _pendingHardwareResult = null;
        _hardwareRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.TopRamProcesses is not null)
        {
            _lastTopRamProcesses = result.TopRamProcesses;
        }

        if (result.Snapshot is null)
        {
            string error = result.ErrorMessage ?? "Hardware readings were not available.";
            _temperatureTooltip = "Temperature: unavailable";
            _temperatureStatusText = "CPU/GPU temperature unavailable";
            _temperatureDetailText = error;
            _temperatureUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _temperatureSourceText = _hardwareMonitor.SourceDescription;
            _memoryTooltip = "Usage: unavailable";
            _memoryStatusText = "CPU/GPU/RAM/VRAM usage unavailable";
            _memoryDetailText = error;
            _memoryUpdatedText = _temperatureUpdatedText;
            _memorySourceText = _hardwareMonitor.SourceDescription;
            UpdateTemperatureTrayIcon(
                TrayIconRenderer.CreateTemperatureUnavailableIcon(),
                TrayIconRenderer.TemperatureUnavailableIconKey);
            UpdateMemoryTrayIcon(
                TrayIconRenderer.CreateMemoryUnavailableIcon(),
                TrayIconRenderer.MemoryUnavailableIconKey);
            return;
        }

        HardwareSnapshot snapshot = result.Snapshot;
        _lastHardwareSnapshot = snapshot;
        _temperatureTooltip = BuildTemperatureTooltip(snapshot);
        _temperatureStatusText = BuildTemperatureHeadline(snapshot);
        _temperatureDetailText = BuildTemperatureDetail(snapshot);
        _temperatureUpdatedText = $"Checked {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _temperatureSourceText = snapshot.SourceDescription;
        _memoryTooltip = BuildUsageTooltip(snapshot);
        _memoryStatusText = BuildUsageHeadline(snapshot);
        _memoryDetailText = BuildUsageDetail(snapshot);
        _memoryUpdatedText = _temperatureUpdatedText;
        _memorySourceText = snapshot.SourceDescription;
        UpdateTemperatureTrayIcon(
            TrayIconRenderer.CreateTemperatureIcon(snapshot),
            TrayIconRenderer.GetTemperatureIconKey(snapshot));
        UpdateMemoryTrayIcon(
            TrayIconRenderer.CreateMemoryIcon(snapshot),
            TrayIconRenderer.GetMemoryIconKey(snapshot));
    }

    private void PublishCounterState()
    {
        _counterWebSocketServer.Publish(BuildCounterJson());
    }

    private string BuildCounterJson()
    {
        JsonObject root = new()
        {
            ["type"] = "limits.counters",
            ["version"] = 1,
            ["updatedAt"] = FormatCounterTimestamp(DateTimeOffset.UtcNow)
        };
        root["codex"] = BuildCodexCounterJson(_lastCodexSnapshot, _lastCodexSparkSnapshot);
        root["claude"] = BuildClaudeCounterJson(_lastClaudeSnapshot);
        root["kimi"] = BuildKimiCounterJson(_lastKimiSnapshot);
        root["deepSeek"] = BuildDeepSeekCounterJson(_lastDeepSeekSnapshot);
        root["unet"] = BuildUnetCounterJson(_lastUnetSnapshot);
        root["openCode"] = BuildOpenCodeCounterJson(
            _lastOpenCodeSnapshot,
            _lastOpenCodeGoSnapshot,
            _openCodeGoUsageError);
        root["devin"] = BuildDevinCounterJson(_lastDevinSnapshot);
        root["openRouter"] = BuildOpenRouterCounterJson(_lastOpenRouterSnapshot);
        root["hardware"] = BuildHardwareCounterJson(_lastHardwareSnapshot);
        root["disk"] = BuildDiskCounterJson(_lastDiskSnapshot);
        return root.ToJsonString();
    }

    private static JsonObject? BuildCodexCounterJson(
        CodexUsageSnapshot? snapshot,
        CodexUsageSnapshot? sparkSnapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonObject result = BuildCodexLimitCounterJson(snapshot);
        result["spark"] = sparkSnapshot is null ? null : BuildCodexLimitCounterJson(sparkSnapshot);
        return result;
    }

    private static JsonObject BuildCodexLimitCounterJson(CodexUsageSnapshot snapshot)
    {
        JsonObject result = new()
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["primaryWindowMinutes"] = snapshot.PrimaryWindowMinutes,
            ["secondaryWindowMinutes"] = snapshot.SecondaryWindowMinutes,
            ["primaryResetAt"] = FormatCounterTimestamp(snapshot.PrimaryResetAt),
            ["secondaryResetAt"] = FormatCounterTimestamp(snapshot.SecondaryResetAt),
            ["planType"] = snapshot.PlanType,
            ["model"] = snapshot.Model
        };
        SetPercent(result, "primaryUsedPercent", snapshot.PrimaryUsedPercent);
        SetPercent(result, "primaryRemainingPercent", 100d - snapshot.PrimaryUsedPercent);
        SetPercent(result, "secondaryUsedPercent", snapshot.SecondaryUsedPercent);
        SetPercent(result, "secondaryRemainingPercent", 100d - snapshot.SecondaryUsedPercent);
        return result;
    }

    private static JsonObject? BuildClaudeCounterJson(ClaudeUsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonObject result = new()
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["fiveHourResetAt"] = FormatCounterTimestamp(snapshot.FiveHourResetAt),
            ["sevenDayResetAt"] = FormatCounterTimestamp(snapshot.SevenDayResetAt),
            ["fiveHourRemainingPercent"] = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent),
            ["sevenDayRemainingPercent"] = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent)
        };
        SetPercent(result, "fiveHourUsedPercent", snapshot.FiveHourUsedPercent);
        SetPercent(result, "sevenDayUsedPercent", snapshot.SevenDayUsedPercent);
        return result;
    }

    private static JsonObject? BuildKimiCounterJson(KimiUsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonObject result = new()
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["fiveHourResetAt"] = FormatCounterTimestamp(snapshot.FiveHourResetAt),
            ["sevenDayResetAt"] = FormatCounterTimestamp(snapshot.SevenDayResetAt),
            ["spentTokens"] = snapshot.SpentTokens,
            ["inputTokens"] = snapshot.InputTokens,
            ["outputTokens"] = snapshot.OutputTokens,
            ["cacheCreationTokens"] = snapshot.CacheCreationTokens,
            ["cachedReadTokens"] = snapshot.CachedReadTokens,
            ["recordCount"] = snapshot.RecordCount
        };
        SetPercent(result, "fiveHourUsedPercent", snapshot.FiveHourUsedPercent);
        SetPercent(result, "sevenDayUsedPercent", snapshot.SevenDayUsedPercent);
        SetPercent(result, "fiveHourRemainingPercent", snapshot.FiveHourRemainingPercent);
        SetPercent(result, "sevenDayRemainingPercent", snapshot.SevenDayRemainingPercent);
        return result;
    }

    private static JsonObject? BuildDeepSeekCounterJson(DeepSeekBalanceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["totalBalance"] = snapshot.TotalBalance,
            ["grantedBalance"] = snapshot.GrantedBalance,
            ["toppedUpBalance"] = snapshot.ToppedUpBalance,
            ["isAvailable"] = snapshot.IsAvailable,
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp)
        };
    }

    private static JsonObject? BuildUnetCounterJson(UnetBalanceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonArray accounts = new();
        foreach (UnetAccountBalance account in snapshot.Accounts)
        {
            JsonObject accountJson = new()
            {
                ["username"] = account.Username,
                ["currency"] = account.Currency,
                ["isAvailable"] = account.IsAvailable,
                ["error"] = account.Error
            };
            SetNullableDecimal(accountJson, "balance", account.Balance);
            accounts.Add((JsonNode)accountJson);
        }

        return new JsonObject
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["isAvailable"] = snapshot.Accounts.Any(account => account.IsAvailable),
            ["accounts"] = accounts
        };
    }

    private static JsonObject? BuildOpenCodeCounterJson(
        OpenCodeUsageSnapshot? snapshot,
        OpenCodeGoUsageSnapshot? goUsage,
        string? goUsageError)
    {
        if (snapshot is null && goUsage is null && string.IsNullOrWhiteSpace(goUsageError))
        {
            return null;
        }

        JsonObject result = new();
        if (snapshot is not null)
        {
            result["sessionCount"] = snapshot.SessionCount;
            result["inputTokens"] = snapshot.InputTokens;
            result["outputTokens"] = snapshot.OutputTokens;
            result["reasoningTokens"] = snapshot.ReasoningTokens;
            result["cacheReadTokens"] = snapshot.CacheReadTokens;
            result["cacheWriteTokens"] = snapshot.CacheWriteTokens;
            result["totalTokens"] = snapshot.TotalTokens;
            result["cost"] = snapshot.Cost;
            result["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp);
            result["windowStart"] = FormatCounterTimestamp(snapshot.WindowStart);
            result["latestActivityAt"] = FormatCounterTimestamp(snapshot.LatestActivityAt);
        }

        result["go"] = goUsage is null ? null : BuildOpenCodeGoCounterJson(goUsage);
        if (!string.IsNullOrWhiteSpace(goUsageError))
        {
            result["goError"] = goUsageError;
        }

        return result;
    }

    private static JsonObject BuildOpenCodeGoCounterJson(OpenCodeGoUsageSnapshot snapshot)
    {
        return new JsonObject
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["source"] = snapshot.Source,
            ["isAvailable"] = true,
            ["rolling"] = BuildOpenCodeGoWindowCounterJson(snapshot.Rolling, OpenCodeGoLimits.RollingUsd),
            ["weekly"] = BuildOpenCodeGoWindowCounterJson(snapshot.Weekly, OpenCodeGoLimits.WeeklyUsd),
            ["monthly"] = BuildOpenCodeGoWindowCounterJson(snapshot.Monthly, null)
        };
    }

    private static JsonObject BuildOpenCodeGoWindowCounterJson(
        OpenCodeGoUsageWindow window,
        decimal? limitUsd)
    {
        double usedPercent = Math.Clamp(window.UsedPercent, 0d, 100d);
        return new JsonObject
        {
            ["status"] = window.Status,
            ["usedPercent"] = usedPercent,
            ["remainingPercent"] = Math.Clamp(100d - usedPercent, 0d, 100d),
            ["limitUsd"] = limitUsd,
            ["resetAt"] = FormatCounterTimestamp(window.ResetAt)
        };
    }

    private static JsonObject? BuildOpenRouterCounterJson(OpenRouterBalanceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["spendableBalance"] = snapshot.SpendableBalance,
            ["accountBalance"] = snapshot.AccountBalance,
            ["totalCredits"] = snapshot.TotalCredits,
            ["totalUsage"] = snapshot.TotalUsage,
            ["keyLimit"] = snapshot.KeyLimit,
            ["keyLimitRemaining"] = snapshot.KeyLimitRemaining,
            ["keyLimitReset"] = snapshot.KeyLimitReset,
            ["usageDaily"] = snapshot.UsageDaily,
            ["usageWeekly"] = snapshot.UsageWeekly,
            ["usageMonthly"] = snapshot.UsageMonthly,
            ["isFreeTier"] = snapshot.IsFreeTier
        };
    }

    private static JsonObject? BuildDevinCounterJson(DevinUsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["planName"] = snapshot.PlanName,
            ["planEndAt"] = FormatCounterTimestamp(snapshot.PlanEndAt),
            ["daily"] = BuildDevinWindowCounterJson(snapshot.DailyRemainingPercent, snapshot.DailyResetAt),
            ["weekly"] = BuildDevinWindowCounterJson(snapshot.WeeklyRemainingPercent, snapshot.WeeklyResetAt)
        };
    }

    private static JsonObject BuildDevinWindowCounterJson(
        double? remainingPercent,
        DateTimeOffset? resetAt)
    {
        JsonObject result = new()
        {
            ["resetAt"] = FormatCounterTimestamp(resetAt)
        };
        SetPercent(result, "remainingPercent", remainingPercent);
        SetPercent(result, "usedPercent", remainingPercent is { } remaining ? 100d - remaining : null);
        return result;
    }

    private static JsonObject? BuildHardwareCounterJson(HardwareSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonObject result = new()
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["gpuName"] = snapshot.GpuName
        };
        SetNullableDouble(result, "cpuTemperatureC", snapshot.CpuTemperatureC);
        SetNullableDouble(result, "gpuTemperatureC", snapshot.GpuTemperatureC);
        SetPercent(result, "cpuUsagePercent", snapshot.CpuUsagePercent);
        SetPercent(result, "gpuUsagePercent", snapshot.GpuUsagePercent);
        SetPercent(result, "ramUsagePercent", snapshot.RamUsagePercent);
        SetPercent(result, "vramUsagePercent", snapshot.VramUsagePercent);
        SetNullableLong(result, "ramTotalBytes", snapshot.RamTotalBytes);
        SetNullableLong(result, "ramAvailableBytes", snapshot.RamAvailableBytes);
        SetNullableLong(
            result,
            "ramUsedBytes",
            GetUsedBytes(snapshot.RamTotalBytes, snapshot.RamAvailableBytes));
        SetNullableLong(result, "vramTotalBytes", snapshot.VramTotalBytes);
        SetNullableLong(result, "vramAvailableBytes", snapshot.VramAvailableBytes);
        SetNullableLong(
            result,
            "vramUsedBytes",
            GetUsedBytes(snapshot.VramTotalBytes, snapshot.VramAvailableBytes));
        return result;
    }

    private static JsonObject? BuildDiskCounterJson(DiskSpaceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonArray drives = new();
        foreach (DiskSpaceStatus drive in snapshot.Drives)
        {
            JsonObject driveJson = new()
            {
                ["drive"] = drive.DriveLetter,
                ["redLimitGb"] = drive.RedLimitGb,
                ["isLow"] = drive.IsLow,
                ["isUnavailable"] = drive.IsUnavailable,
                ["error"] = drive.Error
            };
            SetNullableLong(driveJson, "freeBytes", drive.FreeBytes);
            SetNullableDouble(
                driveJson,
                "freeGigabytes",
                drive.FreeBytes is { } freeBytes ? freeBytes / (1024d * 1024d * 1024d) : null);
            drives.Add((JsonNode)driveJson);
        }

        return new JsonObject
        {
            ["checkedAt"] = FormatCounterTimestamp(snapshot.CheckedAt),
            ["healthy"] = snapshot.IsHealthy,
            ["hasLowSpace"] = snapshot.Drives.Any(drive => drive.IsLow),
            ["hasUnavailableDrives"] = snapshot.HasUnavailableDrives,
            ["lowDriveLetters"] = snapshot.LowDriveLetters,
            ["unavailableDriveLetters"] = snapshot.UnavailableDriveLetters,
            ["drives"] = drives
        };
    }

    private static string? FormatCounterTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static void SetPercent(JsonObject target, string propertyName, double? value)
    {
        target[propertyName] = value is { } number && double.IsFinite(number)
            ? JsonValue.Create(Math.Clamp(number, 0d, 100d))
            : null;
    }

    private static void SetNullableDouble(JsonObject target, string propertyName, double? value)
    {
        target[propertyName] = value is { } number && double.IsFinite(number)
            ? JsonValue.Create(number)
            : null;
    }

    private static void SetNullableDecimal(JsonObject target, string propertyName, decimal? value)
    {
        target[propertyName] = value is { } number
            ? JsonValue.Create(number)
            : null;
    }

    private static void SetNullableLong(JsonObject target, string propertyName, long? value)
    {
        target[propertyName] = value is { } number
            ? JsonValue.Create(number)
            : null;
    }

    private static long? GetUsedBytes(long? totalBytes, long? availableBytes)
    {
        if (totalBytes is not { } total || availableBytes is not { } available || total < 0)
        {
            return null;
        }

        return total - Math.Clamp(available, 0, total);
    }

    private void RunLimitWatchdog(ClaudeUsageSnapshot? snapshot)
    {
        if (_limitWatchdogInFlight)
        {
            return;
        }

        _limitWatchdogInFlight = true;
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

    private void UpdateCodexTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.Codex))
        {
            SuppressTrayIcon(
                CodexTrayIconId,
                newIconHandle,
                ref _codexIconHandle,
                ref _codexTrayIconAdded,
                ref _codexIconKey,
                ref _codexAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            CodexTrayIconId,
            newIconHandle,
            ref _codexIconHandle,
            ref _codexTrayIconAdded,
            ref _codexIconKey,
            ref _codexAppliedTooltip,
            iconKey,
            _codexTooltip);
    }

    private void UpdateClaudeTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.Claude))
        {
            SuppressTrayIcon(
                ClaudeTrayIconId,
                newIconHandle,
                ref _claudeIconHandle,
                ref _claudeTrayIconAdded,
                ref _claudeIconKey,
                ref _claudeAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            ClaudeTrayIconId,
            newIconHandle,
            ref _claudeIconHandle,
            ref _claudeTrayIconAdded,
            ref _claudeIconKey,
            ref _claudeAppliedTooltip,
            iconKey,
            _claudeTooltip);
    }

    private void UpdateKimiTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.Kimi))
        {
            SuppressTrayIcon(
                KimiTrayIconId,
                newIconHandle,
                ref _kimiIconHandle,
                ref _kimiTrayIconAdded,
                ref _kimiIconKey,
                ref _kimiAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            KimiTrayIconId,
            newIconHandle,
            ref _kimiIconHandle,
            ref _kimiTrayIconAdded,
            ref _kimiIconKey,
            ref _kimiAppliedTooltip,
            iconKey,
            _kimiTooltip);
    }

    private void UpdateDeepSeekTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.DeepSeek))
        {
            SuppressTrayIcon(
                DeepSeekTrayIconId,
                newIconHandle,
                ref _deepSeekIconHandle,
                ref _deepSeekTrayIconAdded,
                ref _deepSeekIconKey,
                ref _deepSeekAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            DeepSeekTrayIconId,
            newIconHandle,
            ref _deepSeekIconHandle,
            ref _deepSeekTrayIconAdded,
            ref _deepSeekIconKey,
            ref _deepSeekAppliedTooltip,
            iconKey,
            _deepSeekTooltip);
    }

    private void UpdateDiskTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.Disk))
        {
            if (newIconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(newIconHandle);
            }

            HideDiskTrayIcon();
            return;
        }

        UpdateTrayIcon(
            DiskTrayIconId,
            newIconHandle,
            ref _diskIconHandle,
            ref _diskTrayIconAdded,
            ref _diskIconKey,
            ref _diskAppliedTooltip,
            iconKey,
            _diskTooltip);
    }

    private void HideDiskTrayIcon()
    {
        RemoveTrayIcon(DiskTrayIconId, ref _diskTrayIconAdded);
        DestroyIconHandle(ref _diskIconHandle);
        _diskIconKey = null;
        _diskAppliedTooltip = null;
    }

    private void UpdateOpenCodeTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.OpenCode))
        {
            SuppressTrayIcon(
                OpenCodeTrayIconId,
                newIconHandle,
                ref _openCodeIconHandle,
                ref _openCodeTrayIconAdded,
                ref _openCodeIconKey,
                ref _openCodeAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            OpenCodeTrayIconId,
            newIconHandle,
            ref _openCodeIconHandle,
            ref _openCodeTrayIconAdded,
            ref _openCodeIconKey,
            ref _openCodeAppliedTooltip,
            iconKey,
            _openCodeTooltip);
    }

    private void UpdateDevinTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.Devin))
        {
            SuppressTrayIcon(
                DevinTrayIconId,
                newIconHandle,
                ref _devinIconHandle,
                ref _devinTrayIconAdded,
                ref _devinIconKey,
                ref _devinAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            DevinTrayIconId,
            newIconHandle,
            ref _devinIconHandle,
            ref _devinTrayIconAdded,
            ref _devinIconKey,
            ref _devinAppliedTooltip,
            iconKey,
            _devinTooltip);
    }

    private void UpdateOpenRouterTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.OpenRouter))
        {
            SuppressTrayIcon(
                OpenRouterTrayIconId,
                newIconHandle,
                ref _openRouterIconHandle,
                ref _openRouterTrayIconAdded,
                ref _openRouterIconKey,
                ref _openRouterAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            OpenRouterTrayIconId,
            newIconHandle,
            ref _openRouterIconHandle,
            ref _openRouterTrayIconAdded,
            ref _openRouterIconKey,
            ref _openRouterAppliedTooltip,
            iconKey,
            _openRouterTooltip);
    }

    private void UpdateTemperatureTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.Temperature))
        {
            SuppressTrayIcon(
                TemperatureTrayIconId,
                newIconHandle,
                ref _temperatureIconHandle,
                ref _temperatureTrayIconAdded,
                ref _temperatureIconKey,
                ref _temperatureAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            TemperatureTrayIconId,
            newIconHandle,
            ref _temperatureIconHandle,
            ref _temperatureTrayIconAdded,
            ref _temperatureIconKey,
            ref _temperatureAppliedTooltip,
            iconKey,
            _temperatureTooltip);
    }

    private void UpdateMemoryTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        if (!_trayIconSettings.IsVisible(TrayIconKind.CpuGpuLoad))
        {
            SuppressTrayIcon(
                MemoryTrayIconId,
                newIconHandle,
                ref _memoryIconHandle,
                ref _memoryTrayIconAdded,
                ref _memoryIconKey,
                ref _memoryAppliedTooltip);
            return;
        }

        UpdateTrayIcon(
            MemoryTrayIconId,
            newIconHandle,
            ref _memoryIconHandle,
            ref _memoryTrayIconAdded,
            ref _memoryIconKey,
            ref _memoryAppliedTooltip,
            iconKey,
            _memoryTooltip);
    }

    private void SuppressTrayIcon(
        uint iconId,
        IntPtr newIconHandle,
        ref IntPtr iconHandle,
        ref bool trayIconAdded,
        ref string? currentIconKey,
        ref string? currentTooltip)
    {
        if (newIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(newIconHandle);
        }

        RemoveTrayIcon(iconId, ref trayIconAdded);
        DestroyIconHandle(ref iconHandle);
        currentIconKey = null;
        currentTooltip = null;
    }

    private void UpdateTrayIcon(
        uint iconId,
        IntPtr newIconHandle,
        ref IntPtr iconHandle,
        ref bool trayIconAdded,
        ref string? currentIconKey,
        ref string? currentTooltip,
        string newIconKey,
        string tooltip)
    {
        if (newIconHandle == IntPtr.Zero)
        {
            return;
        }

        bool iconUnchanged = trayIconAdded && string.Equals(currentIconKey, newIconKey, StringComparison.Ordinal);
        bool tooltipUnchanged = string.Equals(currentTooltip, tooltip, StringComparison.Ordinal);
        if (iconUnchanged && tooltipUnchanged)
        {
            NativeMethods.DestroyIcon(newIconHandle);
            return;
        }

        IntPtr previousIconHandle = IntPtr.Zero;
        if (iconUnchanged)
        {
            NativeMethods.DestroyIcon(newIconHandle);
        }
        else
        {
            previousIconHandle = iconHandle;
            iconHandle = newIconHandle;
            currentIconKey = newIconKey;
        }

        currentTooltip = tooltip;

        NativeMethods.NOTIFYICONDATA data = CreateNotifyIconData(iconId, iconHandle, tooltip);
        uint message;
        if (trayIconAdded)
        {
            message = NativeMethods.NIM_MODIFY;
        }
        else
        {
            message = NativeMethods.NIM_ADD;
            trayIconAdded = true;
        }

        QueueShellNotify(message, data, previousIconHandle);
    }

    private void QueueShellNotify(uint message, NativeMethods.NOTIFYICONDATA data, IntPtr iconHandleToDestroy)
    {
        lock (_shellNotifyQueueLock)
        {
            _shellNotifyQueue = _shellNotifyQueue.ContinueWith(
                _ => ExecuteShellNotify(message, data, iconHandleToDestroy),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private static void ExecuteShellNotify(uint message, NativeMethods.NOTIFYICONDATA data, IntPtr iconHandleToDestroy)
    {
        bool notified = NativeMethods.Shell_NotifyIcon(message, ref data);
        if (!notified && message == NativeMethods.NIM_ADD)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        }

        if (iconHandleToDestroy != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(iconHandleToDestroy);
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyIconData(uint iconId, IntPtr iconHandle, string tooltip)
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = iconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_GUID,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = iconHandle,
            szTip = TruncateTooltip(tooltip),
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            guidItem = GetTrayIconGuid(iconId)
        };
    }

    private static Guid GetTrayIconGuid(uint iconId)
    {
        return iconId switch
        {
            ClaudeTrayIconId => ClaudeTrayIconGuid,
            KimiTrayIconId => KimiTrayIconGuid,
            DeepSeekTrayIconId => DeepSeekTrayIconGuid,
            DiskTrayIconId => DiskTrayIconGuid,
            OpenCodeTrayIconId => OpenCodeTrayIconGuid,
            TemperatureTrayIconId => TemperatureTrayIconGuid,
            MemoryTrayIconId => MemoryTrayIconGuid,
            DevinTrayIconId => DevinTrayIconGuid,
            OpenRouterTrayIconId => OpenRouterTrayIconGuid,
            _ => CodexTrayIconGuid
        };
    }

    private static IntPtr HandleWindowMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Current is not null)
        {
            return Current.WndProc(windowHandle, message, wParam, lParam);
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            RecreateTrayIcons();
            return IntPtr.Zero;
        }

        switch (message)
        {
            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(_windowHandle);
                return IntPtr.Zero;

            case NativeMethods.WM_TIMER:
                if ((nuint)wParam == RefreshTimerId)
                {
                    RefreshUsage();
                    return IntPtr.Zero;
                }

                if ((nuint)wParam == HardwareRefreshTimerId)
                {
                    RefreshHardware();
                    return IntPtr.Zero;
                }

                if ((nuint)wParam == UsageBalloonDelayTimerId)
                {
                    NativeMethods.KillTimer(_windowHandle, UsageBalloonDelayTimerId);
                    ShowUsageBalloonIfPointerStillOverIcon();
                    return IntPtr.Zero;
                }

                if ((nuint)wParam == UsageBalloonMonitorTimerId)
                {
                    MonitorUsageBalloonPointer();
                    return IntPtr.Zero;
                }

                break;

            case CodexResultMessage:
                ApplyCodexResult();
                PublishCounterState();
                return IntPtr.Zero;

            case ClaudeResultMessage:
                ApplyClaudeResult();
                PublishCounterState();
                return IntPtr.Zero;

            case KimiResultMessage:
                ApplyKimiResult();
                PublishCounterState();
                return IntPtr.Zero;

            case DeepSeekResultMessage:
                ApplyDeepSeekResult();
                PublishCounterState();
                return IntPtr.Zero;

            case UnetResultMessage:
                ApplyUnetResult();
                PublishCounterState();
                return IntPtr.Zero;

            case DiskResultMessage:
                ApplyDiskResult();
                PublishCounterState();
                return IntPtr.Zero;

            case OpenCodeResultMessage:
                ApplyOpenCodeResult();
                PublishCounterState();
                return IntPtr.Zero;

            case DevinResultMessage:
                ApplyDevinResult();
                PublishCounterState();
                return IntPtr.Zero;

            case OpenRouterResultMessage:
                ApplyOpenRouterResult();
                PublishCounterState();
                return IntPtr.Zero;

            case HardwareResultMessage:
                ApplyHardwareResult();
                PublishCounterState();
                return IntPtr.Zero;

            case OpenDiskSettingsMessage:
                OpenDiskSettings();
                return IntPtr.Zero;

            case ShutdownMessage:
                NativeMethods.DestroyWindow(_windowHandle);
                return IntPtr.Zero;

            case TrayCallbackMessage:
                uint iconId = (uint)wParam.ToInt64();
                if (iconId is not CodexTrayIconId and
                    not ClaudeTrayIconId and
                    not KimiTrayIconId and
                    not DeepSeekTrayIconId and
                    not DiskTrayIconId and
                    not OpenCodeTrayIconId and
                    not TemperatureTrayIconId and
                    not MemoryTrayIconId and
                    not DevinTrayIconId and
                    not OpenRouterTrayIconId)
                {
                    break;
                }

                switch ((uint)lParam.ToInt64())
                {
                    case NativeMethods.WM_MOUSEMOVE:
                        if (IsUsageBalloonIcon(iconId))
                        {
                            ScheduleUsageBalloon(iconId);
                        }
                        return IntPtr.Zero;

                    case NativeMethods.WM_LBUTTONDBLCLK:
                        HideUsageBalloon();
                        RefreshUsage();
                        return IntPtr.Zero;

                    case NativeMethods.WM_RBUTTONUP:
                    case NativeMethods.WM_CONTEXTMENU:
                        HideUsageBalloon();
                        ShowContextMenu(iconId);
                        return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_DESTROY:
                CleanupNativeResources();
                PostQuit();
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private static IntPtr HandleUsageBalloonWindowMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        return Current?.UsageBalloonWndProc(windowHandle, message, wParam, lParam) ??
               NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private IntPtr UsageBalloonWndProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_PAINT:
                PaintUsageBalloon(windowHandle);
                return IntPtr.Zero;

            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_MOUSEACTIVATE:
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private static bool IsUsageBalloonIcon(uint iconId)
    {
        return iconId is CodexTrayIconId or
            ClaudeTrayIconId or
            KimiTrayIconId or
            DeepSeekTrayIconId or
            DiskTrayIconId or
            OpenCodeTrayIconId or
            TemperatureTrayIconId or
            MemoryTrayIconId or
            DevinTrayIconId or
            OpenRouterTrayIconId;
    }

    private void ScheduleUsageBalloon(uint iconId)
    {
        if (_usageBalloonIconId == iconId || _hoverIconId == iconId)
        {
            return;
        }

        _hoverIconId = iconId;
        NativeMethods.KillTimer(_windowHandle, UsageBalloonDelayTimerId);
        NativeMethods.SetTimer(_windowHandle, UsageBalloonDelayTimerId, UsageBalloonDelayMs, IntPtr.Zero);
    }

    private void ShowUsageBalloonIfPointerStillOverIcon()
    {
        uint iconId = _hoverIconId;
        _hoverIconId = 0;
        if (!IsUsageBalloonIcon(iconId) ||
            !TryGetTrayIconRect(iconId, out NativeMethods.RECT trayRect) ||
            !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) ||
            !Contains(trayRect, cursor))
        {
            return;
        }

        UsageBalloonContent content = BuildUsageBalloonContent(iconId);
        int rowCount = content.Rows.Count;
        int processCount = content.TopRamProcesses.Count;
        const int width = 620;
        int height = 210 + (rowCount * 25) + (string.IsNullOrWhiteSpace(content.SecondaryDetail) ? 0 : 22);
        if (processCount > 0)
        {
            height = Math.Max(height, 154 + (rowCount * 25) + (processCount * 23));
        }

        NativeMethods.RECT anchor = trayRect;
        IntPtr monitor = NativeMethods.MonitorFromRect(ref anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.MONITORINFO monitorInfo = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            monitorInfo.rcWork = new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
                Bottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)
            };
        }

        int x = Math.Clamp(trayRect.Right - width, monitorInfo.rcWork.Left, Math.Max(monitorInfo.rcWork.Left, monitorInfo.rcWork.Right - width));
        int y = trayRect.Top - height - 8;
        if (y < monitorInfo.rcWork.Top)
        {
            y = trayRect.Bottom + 8;
        }

        y = Math.Clamp(y, monitorInfo.rcWork.Top, Math.Max(monitorInfo.rcWork.Top, monitorInfo.rcWork.Bottom - height));
        _usageBalloonContent = content;
        _usageBalloonIconId = iconId;
        NativeMethods.SetWindowPos(
            _usageBalloonWindowHandle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.InvalidateRect(_usageBalloonWindowHandle, IntPtr.Zero, true);
        NativeMethods.UpdateWindow(_usageBalloonWindowHandle);
        NativeMethods.KillTimer(_windowHandle, UsageBalloonMonitorTimerId);
        NativeMethods.SetTimer(_windowHandle, UsageBalloonMonitorTimerId, UsageBalloonMonitorIntervalMs, IntPtr.Zero);
    }

    private void MonitorUsageBalloonPointer()
    {
        if (_usageBalloonIconId == 0 || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            HideUsageBalloon(clearPendingHover: true);
            return;
        }

        if (NativeMethods.GetWindowRect(_usageBalloonWindowHandle, out NativeMethods.RECT balloonRect) &&
            Contains(balloonRect, cursor))
        {
            return;
        }

        foreach (uint iconId in GetUsageBalloonIconIds())
        {
            if (TryGetTrayIconRect(iconId, out NativeMethods.RECT trayRect) && Contains(trayRect, cursor))
            {
                return;
            }
        }

        HideUsageBalloon(clearPendingHover: true);
    }

    private static uint[] GetUsageBalloonIconIds()
    {
        return
        [
            CodexTrayIconId,
            ClaudeTrayIconId,
            KimiTrayIconId,
            DeepSeekTrayIconId,
            DiskTrayIconId,
            OpenCodeTrayIconId,
            TemperatureTrayIconId,
            MemoryTrayIconId,
            DevinTrayIconId,
            OpenRouterTrayIconId
        ];
    }

    private bool TryGetTrayIconRect(uint iconId, out NativeMethods.RECT rect)
    {
        rect = default;
        if (_usageBalloonWindowHandle == IntPtr.Zero || !IsTrayIconAdded(iconId))
        {
            return false;
        }

        NativeMethods.NOTIFYICONIDENTIFIER identifier = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
            hWnd = _windowHandle,
            uID = iconId,
            guidItem = GetTrayIconGuid(iconId)
        };
        return NativeMethods.Shell_NotifyIconGetRect(ref identifier, out rect) == 0;
    }

    private bool IsTrayIconAdded(uint iconId)
    {
        return iconId switch
        {
            CodexTrayIconId => _codexTrayIconAdded,
            ClaudeTrayIconId => _claudeTrayIconAdded,
            KimiTrayIconId => _kimiTrayIconAdded,
            DeepSeekTrayIconId => _deepSeekTrayIconAdded,
            DiskTrayIconId => _diskTrayIconAdded,
            OpenCodeTrayIconId => _openCodeTrayIconAdded,
            TemperatureTrayIconId => _temperatureTrayIconAdded,
            MemoryTrayIconId => _memoryTrayIconAdded,
            DevinTrayIconId => _devinTrayIconAdded,
            OpenRouterTrayIconId => _openRouterTrayIconAdded,
            _ => false
        };
    }

    private void HideUsageBalloon(bool clearPendingHover = false)
    {
        NativeMethods.KillTimer(_windowHandle, UsageBalloonMonitorTimerId);
        if (clearPendingHover)
        {
            NativeMethods.KillTimer(_windowHandle, UsageBalloonDelayTimerId);
            _hoverIconId = 0;
        }

        _usageBalloonIconId = 0;
        _usageBalloonContent = null;
        if (_usageBalloonWindowHandle != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_usageBalloonWindowHandle, NativeMethods.SW_HIDE);
        }
    }

    private UsageBalloonContent BuildUsageBalloonContent(uint iconId)
    {
        List<UsageBalloonRow> rows = [];
        string title;
        string summary;
        string detail;
        string secondaryDetail = string.Empty;
        string updated;

        switch (iconId)
        {
            case CodexTrayIconId:
                title = string.IsNullOrWhiteSpace(_lastCodexSnapshot?.PlanType)
                    ? "Codex"
                    : $"Codex {_lastCodexSnapshot.PlanType}";
                summary = _codexStatusText;
                detail = JoinBalloonDetails(_codexDetailText, _codexSparkUsageText);
                updated = _codexUpdatedText;
                if (_lastCodexSnapshot is { } codex)
                {
                    AddCodexRows(rows, codex, string.Empty);
                    if (_lastCodexSparkSnapshot is { } spark)
                    {
                        AddCodexRows(rows, spark, "Spark · ");
                    }
                }
                break;

            case ClaudeTrayIconId:
                title = "Claude";
                summary = _claudeStatusText;
                detail = _claudeDetailText;
                updated = _claudeUpdatedText;
                if (_lastClaudeSnapshot is { } claude)
                {
                    rows.Add(CreateQuotaRow("5h limit", claude.FiveHourUsedPercent, 100d - claude.FiveHourUsedPercent, claude.FiveHourResetAt));
                    rows.Add(CreateQuotaRow("7d limit", claude.SevenDayUsedPercent, 100d - claude.SevenDayUsedPercent, claude.SevenDayResetAt));
                }
                break;

            case KimiTrayIconId:
                title = "Kimi Code";
                summary = _kimiStatusText;
                detail = JoinBalloonDetails(_kimiDetailText, _kimiTokenUsageText);
                updated = _kimiUpdatedText;
                if (_lastKimiSnapshot is { } kimi)
                {
                    rows.Add(CreateQuotaRow("5h limit", kimi.FiveHourUsedPercent, kimi.FiveHourRemainingPercent, kimi.FiveHourResetAt));
                    rows.Add(CreateQuotaRow("7d limit", kimi.SevenDayUsedPercent, kimi.SevenDayRemainingPercent, kimi.SevenDayResetAt));
                }
                break;

            case DeepSeekTrayIconId:
                title = "DeepSeek";
                summary = _deepSeekStatusText;
                detail = _deepSeekDetailText;
                updated = _deepSeekUpdatedText;
                break;

            case OpenCodeTrayIconId:
                title = "OpenCode Go";
                summary = _openCodeStatusText;
                detail = _openCodeDetailText;
                updated = _openCodeUpdatedText;
                if (_lastOpenCodeGoSnapshot is { } openCode)
                {
                    rows.Add(CreateQuotaRow("5h limit", openCode.Rolling.UsedPercent, 100d - openCode.Rolling.UsedPercent, openCode.Rolling.ResetAt));
                    rows.Add(CreateQuotaRow("Weekly limit", openCode.Weekly.UsedPercent, 100d - openCode.Weekly.UsedPercent, openCode.Weekly.ResetAt));
                    rows.Add(CreateQuotaRow("Monthly limit", openCode.Monthly.UsedPercent, 100d - openCode.Monthly.UsedPercent, openCode.Monthly.ResetAt));
                }
                break;

            case DevinTrayIconId:
                title = GetDevinPlanTitle(_lastDevinSnapshot?.PlanName);
                summary = _devinStatusText;
                detail = _devinDetailText;
                secondaryDetail = $"Plan period ends {FormatPlanEnd(_lastDevinSnapshot?.PlanEndAt)}";
                updated = _devinUpdatedText;
                if (_lastDevinSnapshot is { } devinSnapshot)
                {
                    rows.Add(CreateQuotaRow("Daily limit", devinSnapshot.DailyRemainingPercent is { } dailyRemaining ? 100d - dailyRemaining : null, devinSnapshot.DailyRemainingPercent, devinSnapshot.DailyResetAt));
                    rows.Add(CreateQuotaRow("Weekly limit", devinSnapshot.WeeklyRemainingPercent is { } weeklyRemaining ? 100d - weeklyRemaining : null, devinSnapshot.WeeklyRemainingPercent, devinSnapshot.WeeklyResetAt));
                }
                break;

            case OpenRouterTrayIconId:
                title = "OpenRouter";
                summary = _openRouterStatusText;
                detail = _openRouterDetailText;
                updated = _openRouterUpdatedText;
                if (_lastOpenRouterSnapshot is { } openRouter)
                {
                    if (openRouter.KeyLimit is { } keyLimit)
                    {
                        decimal? keyUsed = openRouter.KeyLimitRemaining is { } keyRemaining
                            ? Math.Max(0m, keyLimit - keyRemaining)
                            : null;
                        DateTimeOffset? keyResetAt = GetOpenRouterLimitResetAt(openRouter.KeyLimitReset);
                        string keyLimitLabel = openRouter.KeyLimitReset?.ToLowerInvariant() switch
                        {
                            "daily" => "Daily API key cap",
                            "weekly" => "Weekly API key cap",
                            "monthly" => "Monthly API key cap",
                            _ => "API key cap"
                        };
                        rows.Add(new UsageBalloonRow(
                            keyLimitLabel,
                            FormatOpenRouterAmount(keyUsed),
                            FormatOpenRouterAmount(openRouter.KeyLimitRemaining),
                            FormatBalloonCountdown(keyResetAt),
                            FormatBalloonResetAt(keyResetAt)));
                    }

                    AddSpendRow(rows, "Spent today", openRouter.UsageDaily);
                    AddSpendRow(rows, "Spent this week", openRouter.UsageWeekly);
                    AddSpendRow(rows, "Spent this month", openRouter.UsageMonthly);
                }
                break;

            case TemperatureTrayIconId:
                title = "CPU / GPU temperature";
                summary = _temperatureStatusText;
                detail = _temperatureDetailText;
                updated = _temperatureUpdatedText;
                break;

            case MemoryTrayIconId:
                title = "CPU / GPU / RAM usage";
                summary = _memoryStatusText;
                detail = _memoryDetailText;
                updated = _memoryUpdatedText;
                break;

            case DiskTrayIconId:
                title = "Disk space";
                summary = _diskStatusText;
                detail = _diskDetailText;
                updated = _diskUpdatedText;
                break;

            default:
                title = "Usage";
                summary = "No usage information available.";
                detail = string.Empty;
                updated = string.Empty;
                break;
        }

        if (rows.Count == 0 && IsUsageProviderIcon(iconId))
        {
            detail = JoinBalloonDetails(detail, "Detailed limit windows are not currently available.");
        }

        IReadOnlyList<RamProcessUsage> topRamProcesses = iconId == MemoryTrayIconId
            ? _lastTopRamProcesses
            : [];
        return new UsageBalloonContent(title, summary, detail, secondaryDetail, updated, rows, topRamProcesses);
    }

    private static bool IsUsageProviderIcon(uint iconId)
    {
        return iconId is CodexTrayIconId or ClaudeTrayIconId or KimiTrayIconId or
            DeepSeekTrayIconId or OpenCodeTrayIconId or DevinTrayIconId or OpenRouterTrayIconId;
    }

    private static void AddCodexRows(List<UsageBalloonRow> rows, CodexUsageSnapshot snapshot, string prefix)
    {
        if (snapshot.PrimaryWindowMinutes > 0)
        {
            rows.Add(CreateQuotaRow(
                $"{prefix}{FormatLimitWindow(snapshot.PrimaryWindowMinutes)} limit",
                snapshot.PrimaryUsedPercent,
                100d - snapshot.PrimaryUsedPercent,
                snapshot.PrimaryResetAt));
        }

        if (snapshot.SecondaryWindowMinutes > 0)
        {
            rows.Add(CreateQuotaRow(
                $"{prefix}{FormatLimitWindow(snapshot.SecondaryWindowMinutes)} limit",
                snapshot.SecondaryUsedPercent,
                100d - snapshot.SecondaryUsedPercent,
                snapshot.SecondaryResetAt));
        }
    }

    private static string FormatLimitWindow(int minutes)
    {
        if (minutes >= 27 * 24 * 60)
        {
            return "Monthly";
        }

        if (minutes >= 6 * 24 * 60)
        {
            return "Weekly";
        }

        return FormatWindow(minutes);
    }

    private static UsageBalloonRow CreateQuotaRow(
        string label,
        double? usedPercent,
        double? remainingPercent,
        DateTimeOffset? resetAt)
    {
        return new UsageBalloonRow(
            label,
            FormatUsagePercent(usedPercent),
            FormatUsagePercent(remainingPercent),
            FormatBalloonCountdown(resetAt),
            FormatBalloonResetAt(resetAt));
    }

    private static void AddSpendRow(List<UsageBalloonRow> rows, string label, decimal? amount)
    {
        if (amount is { } value)
        {
            rows.Add(new UsageBalloonRow(label, FormatOpenRouterAmount(value), "—", "—", "—"));
        }
    }

    private static string FormatBalloonCountdown(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return "—";
        }

        TimeSpan remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Due";
        }

        int days = (int)Math.Floor(remaining.TotalDays);
        if (days > 0)
        {
            return remaining.Hours > 0 ? $"{days}d {remaining.Hours}h" : $"{days}d";
        }

        if (remaining.Hours > 0)
        {
            return remaining.Minutes > 0 ? $"{remaining.Hours}h {remaining.Minutes}m" : $"{remaining.Hours}h";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    private static string FormatBalloonResetAt(DateTimeOffset? resetAt)
    {
        if (resetAt is not { } value)
        {
            return "—";
        }

        DateTimeOffset localReset = value.ToLocalTime();
        string format = localReset - DateTimeOffset.Now >= TimeSpan.FromDays(6)
            ? "MMM d · ddd h:mm tt"
            : "ddd h:mm tt";
        return localReset.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string FormatPlanEnd(DateTimeOffset? planEndAt)
    {
        return planEndAt?.ToLocalTime().ToString("ddd, MMM d yyyy h:mm tt", CultureInfo.InvariantCulture) ?? "not provided";
    }

    private static DateTimeOffset? GetOpenRouterLimitResetAt(string? resetCadence)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        DateTime utcDate = utcNow.UtcDateTime.Date;
        return resetCadence?.ToLowerInvariant() switch
        {
            "daily" => new DateTimeOffset(utcDate.AddDays(1), TimeSpan.Zero),
            "weekly" => new DateTimeOffset(utcDate.AddDays(GetDaysUntilNextMonday(utcDate.DayOfWeek)), TimeSpan.Zero),
            "monthly" => new DateTimeOffset(new DateTime(utcDate.Year, utcDate.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)),
            _ => null
        };
    }

    private static int GetDaysUntilNextMonday(DayOfWeek dayOfWeek)
    {
        int days = (8 - (int)dayOfWeek) % 7;
        return days == 0 ? 7 : days;
    }

    private static string GetDevinPlanTitle(string? planName)
    {
        return string.IsNullOrWhiteSpace(planName) || string.Equals(planName, "Devin", StringComparison.OrdinalIgnoreCase)
            ? "Devin"
            : $"Devin {planName}";
    }

    private static string JoinBalloonDetails(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first} · {second}";
    }

    private static bool Contains(NativeMethods.RECT rect, NativeMethods.POINT point)
    {
        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    private void PaintUsageBalloon(IntPtr windowHandle)
    {
        NativeMethods.PAINTSTRUCT paint = new()
        {
            rgbReserved = new byte[32]
        };
        IntPtr deviceContext = NativeMethods.BeginPaint(windowHandle, ref paint);
        if (deviceContext == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!NativeMethods.GetClientRect(windowHandle, out NativeMethods.RECT clientRect))
            {
                return;
            }

            IntPtr backgroundBrush = NativeMethods.CreateSolidBrush(ColorRef(31, 34, 40));
            IntPtr borderBrush = NativeMethods.CreateSolidBrush(ColorRef(72, 78, 88));
            IntPtr accentBrush = NativeMethods.CreateSolidBrush(ColorRef(89, 166, 255));
            IntPtr bodyFont = NativeMethods.GetStockObject(NativeMethods.DEFAULT_GUI_FONT);
            IntPtr titleFont = NativeMethods.CreateFontW(
                -20, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            try
            {
                NativeMethods.FillRect(deviceContext, ref clientRect, backgroundBrush);
                NativeMethods.FrameRect(deviceContext, ref clientRect, borderBrush);
                NativeMethods.RECT accentRect = new()
                {
                    Left = clientRect.Left + 1,
                    Top = clientRect.Top + 1,
                    Right = clientRect.Left + 4,
                    Bottom = clientRect.Bottom - 1
                };
                NativeMethods.FillRect(deviceContext, ref accentRect, accentBrush);
                NativeMethods.SetBkMode(deviceContext, NativeMethods.TRANSPARENT);

                UsageBalloonContent content = _usageBalloonContent ?? new UsageBalloonContent(
                    "Usage", "No usage information available.", string.Empty, string.Empty, string.Empty, [], []);
                int x = 20;
                int width = Math.Max(1, clientRect.Right - x - 18);
                int y = 14;
                DrawBalloonText(deviceContext, content.Title, x, y, width, 27, ColorRef(250, 251, 252),
                    NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                    titleFont);
                y += 31;
                DrawBalloonText(deviceContext, content.Summary, x, y, width, 20, ColorRef(225, 229, 235),
                    NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                    bodyFont);
                y += 22;
                if (!string.IsNullOrWhiteSpace(content.Detail))
                {
                    DrawBalloonText(deviceContext, content.Detail, x, y, width, 20, ColorRef(177, 185, 196),
                        NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                        bodyFont);
                    y += 24;
                }

                if (!string.IsNullOrWhiteSpace(content.SecondaryDetail))
                {
                    DrawBalloonText(deviceContext, content.SecondaryDetail, x, y, width, 20, ColorRef(177, 185, 196),
                        NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                        bodyFont);
                    y += 22;
                }

                if (content.Rows.Count > 0)
                {
                    NativeMethods.RECT separator = new()
                    {
                        Left = x,
                        Top = y + 1,
                        Right = clientRect.Right - 18,
                        Bottom = y + 2
                    };
                    NativeMethods.FillRect(deviceContext, ref separator, borderBrush);
                    y += 10;

                    int[] columns = [x, x + 142, x + 210, x + 282, x + 370];
                    int[] widths = [138, 64, 68, 84, width - 370];
                    string[] headings = ["LIMIT", "USED", "LEFT", "TILL RESET", "RESET AT · LOCAL"];
                    for (int column = 0; column < headings.Length; column++)
                    {
                        uint alignment = column == 0
                            ? NativeMethods.DT_LEFT
                            : NativeMethods.DT_RIGHT;
                        DrawBalloonText(deviceContext, headings[column], columns[column], y, widths[column], 18,
                            ColorRef(134, 145, 160), alignment | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                            bodyFont);
                    }

                    y += 22;
                    foreach (UsageBalloonRow row in content.Rows)
                    {
                        string[] values = [row.Limit, row.Used, row.Remaining, row.UntilReset, row.ResetAt];
                        for (int column = 0; column < values.Length; column++)
                        {
                            uint alignment = column == 0
                                ? NativeMethods.DT_LEFT
                                : NativeMethods.DT_RIGHT;
                            DrawBalloonText(deviceContext, values[column], columns[column], y, widths[column], 20,
                                ColorRef(231, 234, 239), alignment | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                                bodyFont);
                        }

                        y += 25;
                    }
                }

                if (content.TopRamProcesses.Count > 0)
                {
                    NativeMethods.RECT separator = new()
                    {
                        Left = x,
                        Top = y + 1,
                        Right = clientRect.Right - 18,
                        Bottom = y + 2
                    };
                    NativeMethods.FillRect(deviceContext, ref separator, borderBrush);
                    y += 10;

                    int processColumnX = x + width - 148;
                    int pidColumnX = x + width - 92;
                    DrawBalloonText(deviceContext, "TOP RAM PROCESSES · WORKING SET", x, y, width - 148, 18,
                        ColorRef(134, 145, 160),
                        NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                        bodyFont);
                    DrawBalloonText(deviceContext, "PID", processColumnX, y, 48, 18,
                        ColorRef(134, 145, 160),
                        NativeMethods.DT_RIGHT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER,
                        bodyFont);
                    DrawBalloonText(deviceContext, "RAM", pidColumnX, y, 90, 18,
                        ColorRef(134, 145, 160),
                        NativeMethods.DT_RIGHT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER,
                        bodyFont);
                    y += 22;

                    foreach (RamProcessUsage process in content.TopRamProcesses)
                    {
                        DrawBalloonText(deviceContext, process.Name, x, y, width - 148, 20,
                            ColorRef(231, 234, 239),
                            NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                            bodyFont);
                        DrawBalloonText(deviceContext, process.ProcessId.ToString(CultureInfo.InvariantCulture), processColumnX, y, 48, 20,
                            ColorRef(177, 185, 196),
                            NativeMethods.DT_RIGHT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER,
                            bodyFont);
                        DrawBalloonText(deviceContext, DiskMonitor.FormatBytes(process.WorkingSetBytes), pidColumnX, y, 90, 20,
                            ColorRef(231, 234, 239),
                            NativeMethods.DT_RIGHT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER,
                            bodyFont);
                        y += 23;
                    }
                }

                if (!string.IsNullOrWhiteSpace(content.Updated))
                {
                    DrawBalloonText(deviceContext, content.Updated, x, clientRect.Bottom - 23, width, 16,
                        ColorRef(131, 140, 152),
                        NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE | NativeMethods.DT_VCENTER | NativeMethods.DT_END_ELLIPSIS,
                        bodyFont);
                }
            }
            finally
            {
                if (bodyFont != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(deviceContext, bodyFont);
                }

                NativeMethods.DeleteObject(backgroundBrush);
                NativeMethods.DeleteObject(borderBrush);
                NativeMethods.DeleteObject(accentBrush);
                if (titleFont != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(titleFont);
                }
            }
        }
        finally
        {
            NativeMethods.EndPaint(windowHandle, ref paint);
        }
    }

    private static void DrawBalloonText(
        IntPtr deviceContext,
        string text,
        int x,
        int y,
        int width,
        int height,
        uint color,
        uint format,
        IntPtr font)
    {
        if (font != IntPtr.Zero)
        {
            NativeMethods.SelectObject(deviceContext, font);
        }

        NativeMethods.SetTextColor(deviceContext, color);
        NativeMethods.RECT textRect = new()
        {
            Left = x,
            Top = y,
            Right = x + width,
            Bottom = y + height
        };
        NativeMethods.DrawText(deviceContext, text, text.Length, ref textRect, format);
    }

    private static uint ColorRef(byte red, byte green, byte blue)
    {
        return (uint)(red | (green << 8) | (blue << 16));
    }

    private sealed record UsageBalloonContent(
        string Title,
        string Summary,
        string Detail,
        string SecondaryDetail,
        string Updated,
        IReadOnlyList<UsageBalloonRow> Rows,
        IReadOnlyList<RamProcessUsage> TopRamProcesses);

    private sealed record UsageBalloonRow(
        string Limit,
        string Used,
        string Remaining,
        string UntilReset,
        string ResetAt);

    private void RecreateTrayIcons()
    {
        _codexTrayIconAdded = false;
        _claudeTrayIconAdded = false;
        _kimiTrayIconAdded = false;
        _deepSeekTrayIconAdded = false;
        _diskTrayIconAdded = false;
        _openCodeTrayIconAdded = false;
        _temperatureTrayIconAdded = false;
        _memoryTrayIconAdded = false;
        _devinTrayIconAdded = false;
        _openRouterTrayIconAdded = false;
        _codexIconKey = null;
        _claudeIconKey = null;
        _kimiIconKey = null;
        _deepSeekIconKey = null;
        _diskIconKey = null;
        _openCodeIconKey = null;
        _temperatureIconKey = null;
        _memoryIconKey = null;
        _devinIconKey = null;
        _openRouterIconKey = null;
        _codexAppliedTooltip = null;
        _claudeAppliedTooltip = null;
        _kimiAppliedTooltip = null;
        _deepSeekAppliedTooltip = null;
        _diskAppliedTooltip = null;
        _openCodeAppliedTooltip = null;
        _temperatureAppliedTooltip = null;
        _memoryAppliedTooltip = null;
        _devinAppliedTooltip = null;
        _openRouterAppliedTooltip = null;

        RefreshVisibleTrayIcons();
        RefreshUsage();
    }

    private void RefreshVisibleTrayIcons()
    {
        if (_lastCodexSnapshot is { } codexSnapshot)
        {
            UpdateCodexTrayIcon(
                TrayIconRenderer.CreateUsageIcon(codexSnapshot),
                TrayIconRenderer.GetCodexIconKey(codexSnapshot));
        }
        else
        {
            UpdateCodexTrayIcon(
                TrayIconRenderer.CreateUnavailableIcon(),
                TrayIconRenderer.CodexUnavailableIconKey);
        }

        if (_lastClaudeSnapshot is { } claudeSnapshot)
        {
            UpdateClaudeTrayIcon(
                TrayIconRenderer.CreateClaudeIcon(claudeSnapshot),
                TrayIconRenderer.GetClaudeIconKey(claudeSnapshot));
        }
        else
        {
            UpdateClaudeTrayIcon(
                TrayIconRenderer.CreateClaudeUnavailableIcon(),
                TrayIconRenderer.ClaudeUnavailableIconKey);
        }

        if (_lastKimiSnapshot is { } kimiSnapshot)
        {
            UpdateKimiTrayIcon(
                TrayIconRenderer.CreateKimiIcon(kimiSnapshot),
                TrayIconRenderer.GetKimiIconKey(kimiSnapshot));
        }
        else
        {
            UpdateKimiTrayIcon(
                TrayIconRenderer.CreateKimiUnavailableIcon(),
                TrayIconRenderer.KimiUnavailableIconKey);
        }

        if (_lastDeepSeekSnapshot is { } deepSeekSnapshot)
        {
            UpdateDeepSeekTrayIcon(
                TrayIconRenderer.CreateDeepSeekIcon(deepSeekSnapshot),
                TrayIconRenderer.GetDeepSeekIconKey(deepSeekSnapshot));
        }
        else
        {
            UpdateDeepSeekTrayIcon(
                TrayIconRenderer.CreateDeepSeekUnavailableIcon(),
                TrayIconRenderer.DeepSeekUnavailableIconKey);
        }

        if (_lastDiskSnapshot is { } diskSnapshot &&
            !string.IsNullOrWhiteSpace(diskSnapshot.LowDriveLetters))
        {
            UpdateDiskTrayIcon(
                TrayIconRenderer.CreateDiskIcon(diskSnapshot),
                TrayIconRenderer.GetDiskIconKey(diskSnapshot));
        }
        else
        {
            HideDiskTrayIcon();
        }

        if (_lastOpenCodeGoSnapshot is { } openCodeGoSnapshot)
        {
            UpdateOpenCodeTrayIcon(
                TrayIconRenderer.CreateOpenCodeIcon(openCodeGoSnapshot),
                TrayIconRenderer.GetOpenCodeIconKey(openCodeGoSnapshot));
        }
        else
        {
            UpdateOpenCodeTrayIcon(
                TrayIconRenderer.CreateOpenCodeUnavailableIcon(),
                TrayIconRenderer.OpenCodeUnavailableIconKey);
        }

        if (_lastDevinSnapshot is { } devinSnapshot)
        {
            UpdateDevinTrayIcon(
                TrayIconRenderer.CreateDevinIcon(devinSnapshot),
                TrayIconRenderer.GetDevinIconKey(devinSnapshot));
        }
        else
        {
            UpdateDevinTrayIcon(
                TrayIconRenderer.CreateDevinUnavailableIcon(),
                TrayIconRenderer.DevinUnavailableIconKey);
        }

        if (_lastOpenRouterSnapshot is { } openRouterSnapshot)
        {
            UpdateOpenRouterTrayIcon(
                TrayIconRenderer.CreateOpenRouterIcon(openRouterSnapshot),
                TrayIconRenderer.GetOpenRouterIconKey(openRouterSnapshot));
        }
        else
        {
            UpdateOpenRouterTrayIcon(
                TrayIconRenderer.CreateOpenRouterUnavailableIcon(),
                TrayIconRenderer.OpenRouterUnavailableIconKey);
        }

        if (_lastHardwareSnapshot is { } hardwareSnapshot)
        {
            UpdateTemperatureTrayIcon(
                TrayIconRenderer.CreateTemperatureIcon(hardwareSnapshot),
                TrayIconRenderer.GetTemperatureIconKey(hardwareSnapshot));
            UpdateMemoryTrayIcon(
                TrayIconRenderer.CreateMemoryIcon(hardwareSnapshot),
                TrayIconRenderer.GetMemoryIconKey(hardwareSnapshot));
        }
        else
        {
            UpdateTemperatureTrayIcon(
                TrayIconRenderer.CreateTemperatureUnavailableIcon(),
                TrayIconRenderer.TemperatureUnavailableIconKey);
            UpdateMemoryTrayIcon(
                TrayIconRenderer.CreateMemoryUnavailableIcon(),
                TrayIconRenderer.MemoryUnavailableIconKey);
        }
    }

    private static string GetTrayIconLabel(TrayIconKind iconKind)
    {
        return iconKind switch
        {
            TrayIconKind.Claude => "Claude",
            TrayIconKind.Kimi => "Kimi",
            TrayIconKind.DeepSeek => "DeepSeek",
            TrayIconKind.Disk => "Disk low-space warning",
            TrayIconKind.OpenCode => "OpenCode Go",
            TrayIconKind.Temperature => "CPU/GPU temperature",
            TrayIconKind.CpuGpuLoad => "CPU/GPU load (always on)",
            TrayIconKind.Devin => "Devin quota",
            TrayIconKind.OpenRouter => "OpenRouter balance",
            _ => "Codex"
        };
    }

    private void AppendTrayIconVisibilityMenu(IntPtr menuHandle)
    {
        IntPtr visibilityMenuHandle = NativeMethods.CreatePopupMenu();
        if (visibilityMenuHandle == IntPtr.Zero)
        {
            return;
        }

        bool menuAttached = false;
        try
        {
            foreach (TrayIconKind iconKind in TrayIconsInVisibilityMenu)
            {
                uint flags = NativeMethods.MF_STRING;
                if (_trayIconSettings.IsVisible(iconKind))
                {
                    flags |= NativeMethods.MF_CHECKED;
                }

                if (iconKind == TrayIconKind.CpuGpuLoad)
                {
                    flags |= NativeMethods.MF_GRAYED;
                }

                NativeMethods.AppendMenu(
                    visibilityMenuHandle,
                    flags,
                    (nuint)(CommandToggleFirstTrayIcon + (uint)iconKind),
                    GetTrayIconLabel(iconKind));
            }

            menuAttached = NativeMethods.AppendMenu(
                menuHandle,
                NativeMethods.MF_POPUP | NativeMethods.MF_STRING,
                unchecked((nuint)visibilityMenuHandle.ToInt64()),
                "Visible icons");
        }
        finally
        {
            if (!menuAttached)
            {
                NativeMethods.DestroyMenu(visibilityMenuHandle);
            }
        }
    }

    private void ToggleTrayIconVisibility(TrayIconKind iconKind)
    {
        if (iconKind == TrayIconKind.CpuGpuLoad)
        {
            return;
        }

        TrayIconSettings updatedSettings = _trayIconSettings.Toggle(iconKind);
        try
        {
            _trayIconSettingsStore.Save(updatedSettings);
            _trayIconSettings = updatedSettings;
            RefreshVisibleTrayIcons();
        }
        catch (Exception exception)
        {
            NativeMethods.MessageBox(
                _windowHandle,
                $"Could not save tray icon settings: {exception.Message}",
                "limits",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
        }
    }

    private void ShowContextMenu(uint iconId)
    {
        HideUsageBalloon(clearPendingHover: true);
        IntPtr menuHandle = NativeMethods.CreatePopupMenu();
        if (menuHandle == IntPtr.Zero)
        {
            return;
        }

        string statusText;
        string detailText;
        string updatedText;
        string sourceText;
        uint openCommand;
        string openLabel;

        switch (iconId)
        {
            case ClaudeTrayIconId:
                statusText = _claudeStatusText;
                detailText = _claudeDetailText;
                updatedText = _claudeUpdatedText;
                sourceText = _claudeSourceText;
                openCommand = CommandOpenClaudeUsage;
                openLabel = "Open Claude usage settings";
                break;

            case KimiTrayIconId:
                statusText = _kimiStatusText;
                detailText = _kimiDetailText;
                updatedText = _kimiUpdatedText;
                sourceText = _kimiSourceText;
                openCommand = CommandOpenKimiSessions;
                openLabel = "Open Kimi sessions";
                break;

            case DeepSeekTrayIconId:
                statusText = _deepSeekStatusText;
                detailText = _deepSeekDetailText;
                updatedText = _deepSeekUpdatedText;
                sourceText = _deepSeekSourceText;
                openCommand = CommandOpenDeepSeekBilling;
                openLabel = "Open DeepSeek billing";
                break;

            case DiskTrayIconId:
                statusText = _diskStatusText;
                detailText = _diskDetailText;
                updatedText = _diskUpdatedText;
                sourceText = _diskSourceText;
                openCommand = CommandOpenDiskSettings;
                openLabel = "Open disk settings";
                break;

            case OpenCodeTrayIconId:
                statusText = _openCodeStatusText;
                detailText = _openCodeDetailText;
                updatedText = _openCodeUpdatedText;
                sourceText = _openCodeSourceText;
                openCommand = CommandOpenOpenCodeData;
                openLabel = "Open OpenCode data";
                break;

            case DevinTrayIconId:
                statusText = _devinStatusText;
                detailText = _devinDetailText;
                updatedText = _devinUpdatedText;
                sourceText = _devinSourceText;
                openCommand = CommandOpenDevinUsage;
                openLabel = "Open Devin usage";
                break;

            case OpenRouterTrayIconId:
                statusText = _openRouterStatusText;
                detailText = _openRouterDetailText;
                updatedText = _openRouterUpdatedText;
                sourceText = _openRouterSourceText;
                openCommand = CommandOpenOpenRouterCredits;
                openLabel = "Open OpenRouter credits";
                break;

            case TemperatureTrayIconId:
                statusText = _temperatureStatusText;
                detailText = _temperatureDetailText;
                updatedText = _temperatureUpdatedText;
                sourceText = _temperatureSourceText;
                openCommand = CommandOpenTaskManager;
                openLabel = "Open Task Manager";
                break;

            case MemoryTrayIconId:
                statusText = _memoryStatusText;
                detailText = _memoryDetailText;
                updatedText = _memoryUpdatedText;
                sourceText = _memorySourceText;
                openCommand = CommandOpenTaskManager;
                openLabel = "Open Task Manager";
                break;

            default:
                statusText = _codexStatusText;
                detailText = _codexDetailText;
                updatedText = _codexUpdatedText;
                sourceText = _codexSourceText;
                openCommand = CommandOpenCodexSessions;
                openLabel = "Open Codex sessions";
                break;
        }

        try
        {
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(statusText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(detailText));
            if (iconId == CodexTrayIconId)
            {
                NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(_codexSparkUsageText));
            }
            else if (iconId == KimiTrayIconId)
            {
                NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(_kimiTokenUsageText));
            }

            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(updatedText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(sourceText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_SEPARATOR, 0, null);
            AppendTrayIconVisibilityMenu(menuHandle);
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandRefresh, "Refresh now");
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, openCommand, openLabel);
            if (iconId != DiskTrayIconId)
            {
                NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandOpenDiskSettings, "Open disk settings");
            }
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandExit, "Exit");

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
            NativeMethods.SetForegroundWindow(_windowHandle);

            uint selectedCommand = NativeMethods.TrackPopupMenu(
                menuHandle,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                0,
                _windowHandle,
                IntPtr.Zero);

            HandleMenuCommand(selectedCommand);
            NativeMethods.PostMessage(_windowHandle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(menuHandle);
        }
    }

    private void HandleMenuCommand(uint command)
    {
        if (command >= CommandToggleFirstTrayIcon && command <= CommandToggleLastTrayIcon)
        {
            ToggleTrayIconVisibility((TrayIconKind)(command - CommandToggleFirstTrayIcon));
            return;
        }

        switch (command)
        {
            case CommandRefresh:
                RefreshUsage();
                break;

            case CommandOpenCodexSessions:
                OpenCodexSessionsFolder();
                break;

            case CommandOpenClaudeUsage:
                OpenClaudeUsagePage();
                break;

            case CommandOpenKimiSessions:
                OpenKimiSessionsFolder();
                break;

            case CommandOpenDeepSeekBilling:
                OpenDeepSeekBillingPage();
                break;

            case CommandOpenDiskSettings:
                OpenDiskSettings();
                break;

            case CommandOpenOpenCodeData:
                OpenOpenCodeDataDirectory();
                break;

            case CommandOpenDevinUsage:
                OpenDevinUsagePage();
                break;

            case CommandOpenOpenRouterCredits:
                OpenOpenRouterCreditsPage();
                break;

            case CommandOpenTaskManager:
                OpenTaskManager();
                break;

            case CommandExit:
                NativeMethods.DestroyWindow(_windowHandle);
                break;
        }
    }

    private void OpenCodexSessionsFolder()
    {
        string target = Directory.Exists(_codexUsageReader.SessionsPath)
            ? _codexUsageReader.SessionsPath
            : _codexUsageReader.CodexHomePath;

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static void OpenClaudeUsagePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://claude.ai/settings/usage",
            UseShellExecute = true
        });
    }

    private void OpenKimiSessionsFolder()
    {
        string target = Directory.Exists(_kimiUsageReader.SessionsPath)
            ? _kimiUsageReader.SessionsPath
            : _kimiUsageReader.KimiHomePath;

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static void OpenDeepSeekBillingPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://platform.deepseek.com/usage",
            UseShellExecute = true
        });
    }

    private void OpenOpenCodeDataDirectory()
    {
        bool dataDirectoryExists = Directory.Exists(_openCodeUsageReader.DataDirectoryPath);
        bool databaseExists = File.Exists(_openCodeUsageReader.DatabasePath);
        if (!dataDirectoryExists && !databaseExists)
        {
            NativeMethods.MessageBox(
                _windowHandle,
                $"OpenCode data was not found at {_openCodeUsageReader.DatabasePath}.",
                "OpenCode",
                NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
            return;
        }

        string target = dataDirectoryExists
            ? _openCodeUsageReader.DataDirectoryPath
            : _openCodeUsageReader.DatabasePath;

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static void OpenDevinUsagePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://app.devin.ai/settings/usage",
            UseShellExecute = true
        });
    }

    private static void OpenOpenRouterCreditsPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://openrouter.ai/settings/credits",
            UseShellExecute = true
        });
    }

    private static void OpenTaskManager()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "taskmgr.exe",
            UseShellExecute = true
        });
    }

    private void OpenDiskSettings()
    {
        if (_diskSettingsWindow is not null)
        {
            _diskSettingsWindow.Activate();
            return;
        }

        DiskSettingsWindow? window = null;
        window = new DiskSettingsWindow(
            _diskMonitor.Settings,
            settings =>
            {
                _diskMonitor.SaveSettings(settings);
                RefreshDiskSpace();
            },
            () =>
            {
                if (ReferenceEquals(_diskSettingsWindow, window))
                {
                    _diskSettingsWindow = null;
                }
            });

        _diskSettingsWindow = window;
        try
        {
            window.Show(_windowHandle);
        }
        catch (Exception exception)
        {
            _diskSettingsWindow = null;
            window.Dispose();
            NativeMethods.MessageBox(
                _windowHandle,
                $"The disk settings window could not be opened: {exception.Message}",
                "Disk settings",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
        }
    }

    private void PostQuit()
    {
        _windowHandle = IntPtr.Zero;
        Current = null;
        NativeMethods.PostQuitMessage(0);
    }

    private void CleanupNativeResources()
    {
        HideUsageBalloon(clearPendingHover: true);
        if (_usageBalloonWindowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_usageBalloonWindowHandle);
            _usageBalloonWindowHandle = IntPtr.Zero;
        }

        RemoveTrayIcon(CodexTrayIconId, ref _codexTrayIconAdded);
        RemoveTrayIcon(ClaudeTrayIconId, ref _claudeTrayIconAdded);
        RemoveTrayIcon(KimiTrayIconId, ref _kimiTrayIconAdded);
        RemoveTrayIcon(DeepSeekTrayIconId, ref _deepSeekTrayIconAdded);
        RemoveTrayIcon(DiskTrayIconId, ref _diskTrayIconAdded);
        RemoveTrayIcon(OpenCodeTrayIconId, ref _openCodeTrayIconAdded);
        RemoveTrayIcon(TemperatureTrayIconId, ref _temperatureTrayIconAdded);
        RemoveTrayIcon(MemoryTrayIconId, ref _memoryTrayIconAdded);
        RemoveTrayIcon(DevinTrayIconId, ref _devinTrayIconAdded);
        RemoveTrayIcon(OpenRouterTrayIconId, ref _openRouterTrayIconAdded);

        _diskSettingsWindow?.Dispose();
        _diskSettingsWindow = null;

        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_windowHandle, RefreshTimerId);
            NativeMethods.KillTimer(_windowHandle, HardwareRefreshTimerId);
            NativeMethods.KillTimer(_windowHandle, UsageBalloonDelayTimerId);
            NativeMethods.KillTimer(_windowHandle, UsageBalloonMonitorTimerId);
        }

        _hardwareMonitor.Dispose();

        DestroyIconHandle(ref _codexIconHandle);
        DestroyIconHandle(ref _claudeIconHandle);
        DestroyIconHandle(ref _kimiIconHandle);
        DestroyIconHandle(ref _deepSeekIconHandle);
        DestroyIconHandle(ref _diskIconHandle);
        DestroyIconHandle(ref _openCodeIconHandle);
        DestroyIconHandle(ref _temperatureIconHandle);
        DestroyIconHandle(ref _memoryIconHandle);
        DestroyIconHandle(ref _devinIconHandle);
        DestroyIconHandle(ref _openRouterIconHandle);

        if (_windowClassRegistered)
        {
            NativeMethods.UnregisterClass(_windowClassName, NativeMethods.GetModuleHandle(null));
            _windowClassRegistered = false;
        }

        if (_usageBalloonWindowClassRegistered)
        {
            NativeMethods.UnregisterClass(_usageBalloonWindowClassName, NativeMethods.GetModuleHandle(null));
            _usageBalloonWindowClassRegistered = false;
        }
    }

    private void RemoveTrayIcon(uint iconId, ref bool trayIconAdded)
    {
        if (trayIconAdded && _windowHandle != IntPtr.Zero)
        {
            NativeMethods.NOTIFYICONDATA data = new()
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = iconId,
                uFlags = NativeMethods.NIF_GUID,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
                guidItem = GetTrayIconGuid(iconId)
            };

            QueueShellNotify(NativeMethods.NIM_DELETE, data, IntPtr.Zero);
            trayIconAdded = false;
        }
    }

    private static void DestroyIconHandle(ref IntPtr iconHandle)
    {
        if (iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(iconHandle);
            iconHandle = IntPtr.Zero;
        }
    }

    private static string BuildCodexTooltip(CodexUsageSnapshot snapshot)
    {
        int weeklyRemaining = CodexUsageMath.GetWeeklyRemainingPercent(snapshot);
        int weeklyWindow = CodexUsageMath.GetWeeklyWindowMinutes(snapshot);

        return TruncateTooltip(
            $"Codex: {FormatWindow(weeklyWindow)} left {weeklyRemaining}%, " +
            FormatResetCountdown(CodexUsageMath.GetWeeklyResetAt(snapshot)));
    }

    private static string BuildCodexHeadline(CodexUsageSnapshot snapshot)
    {
        string planType = string.IsNullOrWhiteSpace(snapshot.PlanType) ? "plan ?" : snapshot.PlanType;
        int weeklyRemaining = CodexUsageMath.GetWeeklyRemainingPercent(snapshot);

        return $"{planType}: {FormatWindow(CodexUsageMath.GetWeeklyWindowMinutes(snapshot))} left {weeklyRemaining}% " +
               $"({FormatResetCountdown(CodexUsageMath.GetWeeklyResetAt(snapshot))})";
    }

    private static string BuildCodexDetail(CodexUsageSnapshot snapshot)
    {
        DateTimeOffset? weeklyResetAt = CodexUsageMath.GetWeeklyResetAt(snapshot);
        string weeklyReset = weeklyResetAt is null
            ? "?"
            : weeklyResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"Reset {FormatWindow(CodexUsageMath.GetWeeklyWindowMinutes(snapshot))} {weeklyReset}";
    }

    private static string BuildSparkUsage(CodexUsageSnapshot? sparkSnapshot)
    {
        if (sparkSnapshot is null)
        {
            return "Spark usage: no recent Spark sessions";
        }

        int weeklyRemaining = CodexUsageMath.GetWeeklyRemainingPercent(sparkSnapshot);
        string sparkSeen = sparkSnapshot.Timestamp.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"Spark: {FormatWindow(CodexUsageMath.GetWeeklyWindowMinutes(sparkSnapshot))} left {weeklyRemaining}% " +
               $"({FormatResetCountdown(CodexUsageMath.GetWeeklyResetAt(sparkSnapshot))}), Spark seen {sparkSeen}";
    }

    private static string BuildClaudeTooltip(ClaudeUsageSnapshot snapshot)
    {
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);

        return TruncateTooltip(
            $"Claude: 5h left {fiveHourRemaining}%, 7d left {sevenDayRemaining}%, " +
            FormatResetCountdown(snapshot.SevenDayResetAt));
    }

    private static string BuildClaudeStaleTooltip(ClaudeUsageSnapshot snapshot)
    {
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);

        return TruncateTooltip(
            $"Claude: 5h left {fiveHourRemaining}%, 7d left {sevenDayRemaining}% (last known)");
    }

    private static string BuildClaudeHeadline(ClaudeUsageSnapshot snapshot)
    {
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);

        return $"Claude: 5h left {fiveHourRemaining}% | 7d left {sevenDayRemaining}% " +
               $"({FormatResetCountdown(snapshot.SevenDayResetAt)})";
    }

    private static string BuildClaudeDetail(ClaudeUsageSnapshot snapshot)
    {
        string fiveHourReset = snapshot.FiveHourResetAt is null
            ? "?"
            : snapshot.FiveHourResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        string sevenDayReset = snapshot.SevenDayResetAt is null
            ? "?"
            : snapshot.SevenDayResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"Reset 5h {fiveHourReset}, 7d {sevenDayReset}";
    }

    private static string BuildKimiTooltip(KimiUsageSnapshot snapshot)
    {
        return TruncateTooltip(
            $"Kimi: 5h left {FormatUsagePercent(snapshot.FiveHourRemainingPercent)}, " +
            $"7d left {FormatUsagePercent(snapshot.SevenDayRemainingPercent)}, " +
            FormatResetCountdown(snapshot.SevenDayResetAt));
    }

    private static string BuildKimiStaleTooltip(KimiUsageSnapshot snapshot)
    {
        return TruncateTooltip(
            $"Kimi: 5h left {FormatUsagePercent(snapshot.FiveHourRemainingPercent)}, " +
            $"7d left {FormatUsagePercent(snapshot.SevenDayRemainingPercent)} (last known)");
    }

    private static string BuildKimiUnavailableTooltip(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "Kimi: usage unavailable";
        }

        return TruncateTooltip($"Kimi: usage unavailable ({errorMessage})");
    }

    private static string BuildKimiHeadline(KimiUsageSnapshot snapshot)
    {
        return $"Kimi: 5h left {FormatUsagePercent(snapshot.FiveHourRemainingPercent)} | " +
               $"7d left {FormatUsagePercent(snapshot.SevenDayRemainingPercent)} " +
               $"({FormatResetCountdown(snapshot.SevenDayResetAt)})";
    }

    private static string BuildKimiDetail(KimiUsageSnapshot snapshot)
    {
        string fiveHourReset = snapshot.FiveHourResetAt is null
            ? "?"
            : snapshot.FiveHourResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        string sevenDayReset = snapshot.SevenDayResetAt is null
            ? "?"
            : snapshot.SevenDayResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"Left 5h {FormatUsagePercent(snapshot.FiveHourRemainingPercent)}, " +
               $"7d {FormatUsagePercent(snapshot.SevenDayRemainingPercent)}; " +
               $"reset 5h {fiveHourReset}, 7d {sevenDayReset}";
    }

    private static string BuildKimiTokenUsage(KimiUsageSnapshot snapshot)
    {
        if (snapshot.RecordCount <= 0)
        {
            return "Kimi tokens: no local usage.record events in 24h";
        }

        return $"Kimi tokens 24h: spent {FormatCompactTokens(snapshot.SpentTokens)}, " +
               $"cached read {FormatCompactTokens(snapshot.CachedReadTokens)}";
    }

    private static string BuildDeepSeekTooltip(DeepSeekBalanceSnapshot snapshot)
    {
        return TruncateTooltip($"DeepSeek: {FormatDeepSeekBalance(snapshot.TotalBalance)} left via DeepCode");
    }

    private static string BuildDeepSeekUnavailableTooltip(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "DeepSeek: balance unavailable";
        }

        return TruncateTooltip($"DeepSeek: balance unavailable ({errorMessage})");
    }

    private static string BuildDeepSeekHeadline(DeepSeekBalanceSnapshot snapshot)
    {
        string availability = snapshot.IsAvailable ? "available" : "unavailable for API calls";
        return $"DeepSeek: {FormatDeepSeekBalance(snapshot.TotalBalance)} left ({availability})";
    }

    private static string BuildDeepSeekDetail(DeepSeekBalanceSnapshot snapshot)
    {
        return $"Topped up {FormatDeepSeekBalance(snapshot.ToppedUpBalance)}, " +
               $"granted {FormatDeepSeekBalance(snapshot.GrantedBalance)}";
    }

    private static string BuildOpenCodeTooltip(
        OpenCodeUsageSnapshot snapshot,
        OpenCodeGoUsageSnapshot? goSnapshot,
        string? goUsageError)
    {
        string localText =
            $"OpenCode: {FormatCompactTokens(snapshot.TotalTokens)} tokens today, " +
            $"{snapshot.SessionCount} {(snapshot.SessionCount == 1 ? "session" : "sessions")}";
        if (goSnapshot is null)
        {
            return TruncateTooltip(FormatOpenCodeGoError(localText, goUsageError));
        }

        return TruncateTooltip(
            $"{localText}; {BuildOpenCodeGoSummary(goSnapshot)}" +
            (string.IsNullOrWhiteSpace(goUsageError) ? string.Empty : " (last known Go quota)"));
    }

    private static string BuildOpenCodeUnavailableTooltip(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "OpenCode: usage unavailable";
        }

        return TruncateTooltip($"OpenCode: usage unavailable ({errorMessage})");
    }

    private static string BuildOpenCodeHeadline(
        OpenCodeUsageSnapshot snapshot,
        OpenCodeGoUsageSnapshot? goSnapshot)
    {
        string localText = $"OpenCode: {FormatCompactTokens(snapshot.TotalTokens)} tokens today | " +
                           $"{snapshot.SessionCount} {(snapshot.SessionCount == 1 ? "session" : "sessions")}";
        return goSnapshot is null
            ? localText
            : $"{localText} | Go {BuildOpenCodeGoSummary(goSnapshot)}";
    }

    private static string BuildOpenCodeDetail(
        OpenCodeUsageSnapshot snapshot,
        OpenCodeGoUsageSnapshot? goSnapshot,
        string? goUsageError)
    {
        string localText = $"Input {FormatCompactTokens(snapshot.InputTokens)}, output {FormatCompactTokens(snapshot.OutputTokens)}, " +
                           $"reasoning {FormatCompactTokens(snapshot.ReasoningTokens)}, cache read {FormatCompactTokens(snapshot.CacheReadTokens)}, " +
                           $"cost {snapshot.Cost.ToString("C", CultureInfo.InvariantCulture)}";
        return goSnapshot is null
            ? FormatOpenCodeGoError(localText, goUsageError)
            : $"{localText}; {BuildOpenCodeGoSummary(goSnapshot)}";
    }

    private static string BuildOpenCodeGoTooltip(OpenCodeGoUsageSnapshot snapshot, bool isStale)
    {
        return TruncateTooltip(
            $"OpenCode Go: {BuildOpenCodeGoSummary(snapshot)}" +
            (isStale ? " (last known)" : string.Empty));
    }

    private static string BuildOpenCodeGoHeadline(OpenCodeGoUsageSnapshot snapshot)
    {
        return $"OpenCode Go: {BuildOpenCodeGoSummary(snapshot)}";
    }

    private static string BuildOpenCodeGoDetail(OpenCodeGoUsageSnapshot snapshot)
    {
        return $"Rolling 5-hour limit ${OpenCodeGoLimits.RollingUsd}, " +
               $"weekly ${OpenCodeGoLimits.WeeklyUsd}; monthly allowance varies by model; " +
               BuildOpenCodeGoSummary(snapshot);
    }

    private static string BuildOpenCodeGoSummary(OpenCodeGoUsageSnapshot snapshot)
    {
        return $"5h {CodexUsageMath.GetRemainingPercent(snapshot.Rolling.UsedPercent)}% left, " +
               $"weekly {CodexUsageMath.GetRemainingPercent(snapshot.Weekly.UsedPercent)}% left, " +
               $"monthly {CodexUsageMath.GetRemainingPercent(snapshot.Monthly.UsedPercent)}% left";
    }

    private static string FormatOpenCodeGoError(string text, string? goUsageError)
    {
        return string.IsNullOrWhiteSpace(goUsageError)
            ? text
            : $"{text}; {goUsageError}";
    }

    private static string BuildOpenRouterTooltip(OpenRouterBalanceSnapshot snapshot)
    {
        return TruncateTooltip(
            $"OpenRouter: {FormatOpenRouterAmount(snapshot.SpendableBalance)} left" +
            (snapshot.UsageDaily is { } daily ? $", {FormatOpenRouterAmount(daily)} today" : string.Empty));
    }

    private static string BuildOpenRouterUnavailableTooltip(string? errorMessage)
    {
        return string.IsNullOrWhiteSpace(errorMessage)
            ? "OpenRouter: balance unavailable"
            : TruncateTooltip($"OpenRouter: balance unavailable ({errorMessage})");
    }

    private static string BuildOpenRouterHeadline(OpenRouterBalanceSnapshot snapshot)
    {
        string text = $"OpenRouter: {FormatOpenRouterAmount(snapshot.SpendableBalance)} left";
        if (snapshot.KeyLimitRemaining is { } keyRemaining &&
            snapshot.AccountBalance is { } balance &&
            keyRemaining < balance)
        {
            text += " (key limit)";
        }

        return snapshot.IsFreeTier ? $"{text} | free tier" : text;
    }

    private static string BuildOpenRouterDetail(OpenRouterBalanceSnapshot snapshot)
    {
        List<string> parts = [];
        if (snapshot.AccountBalance is { } balance)
        {
            parts.Add(
                $"Account {FormatOpenRouterAmount(balance)} " +
                $"({FormatOpenRouterAmount(snapshot.TotalUsage)} of {FormatOpenRouterAmount(snapshot.TotalCredits)} used)");
        }

        if (snapshot.KeyLimit is { } limit)
        {
            string reset = string.IsNullOrWhiteSpace(snapshot.KeyLimitReset)
                ? string.Empty
                : $", resets {snapshot.KeyLimitReset}";
            parts.Add(
                $"key {FormatOpenRouterAmount(snapshot.KeyLimitRemaining)} of {FormatOpenRouterAmount(limit)}{reset}");
        }

        parts.Add(
            $"key spend today {FormatOpenRouterAmount(snapshot.UsageDaily)}, " +
            $"week {FormatOpenRouterAmount(snapshot.UsageWeekly)}, " +
            $"month {FormatOpenRouterAmount(snapshot.UsageMonthly)}");
        return string.Join("; ", parts);
    }

    private static string FormatOpenRouterAmount(decimal? amount)
    {
        return amount is { } value
            ? $"${value.ToString("0.00", CultureInfo.InvariantCulture)}"
            : "?";
    }

    private static string BuildDevinTooltip(DevinUsageSnapshot snapshot, bool isStale)
    {
        return TruncateTooltip(
            $"{GetDevinPlanTitle(snapshot.PlanName)}: daily {FormatUsagePercent(snapshot.DailyRemainingPercent)} left, " +
            $"weekly {FormatUsagePercent(snapshot.WeeklyRemainingPercent)} left" +
            (isStale ? " (last known)" : string.Empty));
    }

    private static string BuildDevinUnavailableTooltip(string? errorMessage)
    {
        return string.IsNullOrWhiteSpace(errorMessage)
            ? "Devin: quota unavailable"
            : TruncateTooltip($"Devin: quota unavailable ({errorMessage})");
    }

    private static string BuildDevinHeadline(DevinUsageSnapshot snapshot)
    {
        return $"{GetDevinPlanTitle(snapshot.PlanName)}: daily {FormatUsagePercent(snapshot.DailyRemainingPercent)} left | " +
               $"weekly {FormatUsagePercent(snapshot.WeeklyRemainingPercent)} left";
    }

    private static string BuildDevinDetail(DevinUsageSnapshot snapshot)
    {
        return $"Daily {FormatResetCountdown(snapshot.DailyResetAt)}, " +
               $"weekly {FormatResetCountdown(snapshot.WeeklyResetAt)}";
    }

    private static string BuildTemperatureTooltip(HardwareSnapshot snapshot)
    {
        return TruncateTooltip(
            $"Temperature: CPU {FormatTemperature(snapshot.CpuTemperatureC)}, " +
            $"GPU {FormatTemperature(snapshot.GpuTemperatureC)}");
    }

    private static string BuildTemperatureHeadline(HardwareSnapshot snapshot)
    {
        return $"Temperature: CPU {FormatTemperature(snapshot.CpuTemperatureC)} | " +
               $"GPU {FormatTemperature(snapshot.GpuTemperatureC)}";
    }

    private static string BuildTemperatureDetail(HardwareSnapshot snapshot)
    {
        string gpuName = string.IsNullOrWhiteSpace(snapshot.GpuName) ? "GPU" : snapshot.GpuName;
        return $"CPU package {FormatTemperature(snapshot.CpuTemperatureC)}; " +
               $"{gpuName} {FormatTemperature(snapshot.GpuTemperatureC)}";
    }

    private static string BuildUsageTooltip(HardwareSnapshot snapshot)
    {
        return TruncateTooltip(
            $"Usage: CPU {FormatUsagePercent(snapshot.CpuUsagePercent)}, " +
            $"GPU {FormatUsagePercent(snapshot.GpuUsagePercent)}, " +
            $"RAM {FormatUsagePercent(snapshot.RamUsagePercent)}, " +
            $"VRAM {FormatUsagePercent(snapshot.VramUsagePercent)}");
    }

    private static string BuildUsageHeadline(HardwareSnapshot snapshot)
    {
        return $"Usage: CPU {FormatUsagePercent(snapshot.CpuUsagePercent)} | " +
               $"GPU {FormatUsagePercent(snapshot.GpuUsagePercent)} | " +
               $"RAM {FormatUsagePercent(snapshot.RamUsagePercent)} | " +
               $"VRAM {FormatUsagePercent(snapshot.VramUsagePercent)}";
    }

    private static string BuildUsageDetail(HardwareSnapshot snapshot)
    {
        return $"CPU {FormatUsagePercent(snapshot.CpuUsagePercent)}; " +
               $"GPU {FormatUsagePercent(snapshot.GpuUsagePercent)}; " +
               $"RAM {FormatMemoryUsage(snapshot.RamUsagePercent, snapshot.RamTotalBytes, snapshot.RamAvailableBytes)}; " +
               $"VRAM {FormatMemoryUsage(snapshot.VramUsagePercent, snapshot.VramTotalBytes, snapshot.VramAvailableBytes)}";
    }

    private static string FormatTemperature(double? temperatureC)
    {
        return temperatureC is null || !double.IsFinite(temperatureC.Value)
            ? "?"
            : $"{temperatureC.Value:0.#}°C";
    }

    private static string FormatUsagePercent(double? usagePercent)
    {
        if (usagePercent is null || !double.IsFinite(usagePercent.Value))
        {
            return "?";
        }

        return $"{Math.Clamp(usagePercent.Value, 0d, 100d):0.#}%";
    }

    private static string FormatMemoryUsage(
        double? usagePercent,
        long? totalBytes,
        long? availableBytes)
    {
        if (totalBytes is not { } total || availableBytes is not { } available || total <= 0)
        {
            return FormatUsagePercent(usagePercent);
        }

        long clampedAvailable = Math.Clamp(available, 0, total);
        long usedBytes = total - clampedAvailable;
        return $"{DiskMonitor.FormatBytes(usedBytes)} / {DiskMonitor.FormatBytes(total)} " +
               $"({FormatUsagePercent(usagePercent)})";
    }

    private void AlertDiskLimits(DiskSpaceSnapshot snapshot)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (DiskSpaceStatus drive in snapshot.Drives.Where(drive => drive.IsLow))
        {
            if (_lastDiskAlertAt.TryGetValue(drive.DriveLetter, out DateTimeOffset lastAlert) &&
                now - lastAlert < TimeSpan.FromMinutes(1))
            {
                continue;
            }

            _lastDiskAlertAt[drive.DriveLetter] = now;
            NativeMethods.MessageBeep(NativeMethods.MB_ICONWARNING);
        }
    }

    private static string BuildDiskTooltip(DiskSpaceSnapshot snapshot)
    {
        if (!snapshot.HasSelectedDrives)
        {
            return "Disk: no disks selected";
        }

        return TruncateTooltip($"Disk: {snapshot.Summary}");
    }

    private static string BuildDiskHeadline(DiskSpaceSnapshot snapshot)
    {
        if (!snapshot.HasSelectedDrives)
        {
            return "Disk: no disks selected";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LowDriveLetters))
        {
            return $"Disk red limit: {snapshot.LowDriveLetters}";
        }

        if (snapshot.HasUnavailableDrives)
        {
            return $"Disk: unavailable {snapshot.UnavailableDriveLetters}";
        }

        return $"Disk: {snapshot.Summary}";
    }

    private static string BuildDiskDetail(DiskSpaceSnapshot snapshot)
    {
        if (!snapshot.HasSelectedDrives)
        {
            return "Choose disks and red limits from disk settings.";
        }

        return $"Red limits: {snapshot.LimitSummary}; {snapshot.Summary}";
    }

    private static string FormatDeepSeekBalance(decimal amount)
    {
        return $"${amount.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    private static string FormatWindow(int minutes)
    {
        if (minutes <= 0)
        {
            return "?";
        }

        if (minutes % (60 * 24) == 0)
        {
            return $"{minutes / (60 * 24)}d";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
    }

    private static string FormatResetCountdown(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return "reset in ?";
        }

        TimeSpan remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "reset due";
        }

        int days = (int)Math.Floor(remaining.TotalDays);
        int hours = remaining.Hours;
        if (days > 0)
        {
            return hours > 0
                ? $"reset in {days}d {hours}h"
                : $"reset in {days}d";
        }

        if (hours > 0)
        {
            return $"reset in {hours}h";
        }

        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"reset in {minutes}m";
    }

    private static string FormatUsagePercent(double value)
    {
        return $"{Math.Clamp(value, 0d, 100d):0.#}%";
    }

    private static string FormatCompactTokens(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return tokens < 10_000_000
                ? $"{tokens / 1_000_000d:0.#}M"
                : $"{tokens / 1_000_000d:0}M";
        }

        if (tokens >= 1_000)
        {
            return tokens < 100_000
                ? $"{tokens / 1_000d:0.#}k"
                : $"{tokens / 1_000d:0}k";
        }

        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    private static string TruncateTooltip(string value)
    {
        return value.Length <= 127 ? value : value[..127];
    }

    private static string LimitMenuText(string value)
    {
        const int maxLength = 120;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : $"{value[..(maxLength - 3)]}...";
    }
}

internal static class CodexUsageMath
{
    public static int GetRemainingPercent(double usedPercent)
    {
        double remaining = 100d - usedPercent;
        return Math.Clamp((int)Math.Floor(remaining), 0, 100);
    }

    public static int GetWeeklyRemainingPercent(CodexUsageSnapshot snapshot)
    {
        return GetRemainingPercent(GetWeeklyUsedPercent(snapshot));
    }

    public static int GetWeeklyWindowMinutes(CodexUsageSnapshot snapshot)
    {
        return UseSecondaryLimit(snapshot)
            ? snapshot.SecondaryWindowMinutes
            : snapshot.PrimaryWindowMinutes;
    }

    public static DateTimeOffset? GetWeeklyResetAt(CodexUsageSnapshot snapshot)
    {
        return UseSecondaryLimit(snapshot)
            ? snapshot.SecondaryResetAt
            : snapshot.PrimaryResetAt;
    }

    private static double GetWeeklyUsedPercent(CodexUsageSnapshot snapshot)
    {
        return UseSecondaryLimit(snapshot)
            ? snapshot.SecondaryUsedPercent
            : snapshot.PrimaryUsedPercent;
    }

    private static bool UseSecondaryLimit(CodexUsageSnapshot snapshot)
    {
        if (snapshot.SecondaryWindowMinutes <= 0)
        {
            return false;
        }

        if (snapshot.PrimaryWindowMinutes <= 0)
        {
            return true;
        }

        return snapshot.SecondaryWindowMinutes > snapshot.PrimaryWindowMinutes;
    }
}

internal static class ClaudeUsageMath
{
    public static int GetRemainingPercent(double usedPercent)
    {
        double remaining = 100d - usedPercent;
        return Math.Clamp((int)Math.Floor(remaining), 0, 100);
    }
}

internal sealed record CodexUsageSnapshot(
    DateTimeOffset Timestamp,
    double PrimaryUsedPercent,
    double SecondaryUsedPercent,
    int PrimaryWindowMinutes,
    int SecondaryWindowMinutes,
    DateTimeOffset? PrimaryResetAt,
    DateTimeOffset? SecondaryResetAt,
    string? PlanType,
    string? Model,
    string SourceFile);

internal sealed record UsageReadResult(CodexUsageSnapshot? Snapshot, CodexUsageSnapshot? SparkSnapshot, string? ErrorMessage);

internal sealed class CodexUsageReader
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    private const int MaxFilesToScan = 32;
    private const int MaxTailBytesToRead = 4 * 1024 * 1024;
    private const int MaxExpandedTailBytesToRead = 64 * 1024 * 1024;
    private const int MaxExpandedTailFilesToScan = 8;
    private const int MaxModelPrefixLinesToRead = 200;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string CodexHomePath { get; } = ResolveCodexHome();
    public string SessionsPath => Path.Combine(CodexHomePath, "sessions");
    public string AuthPath => Path.Combine(CodexHomePath, "auth.json");

    public UsageReadResult ReadLatestSnapshot()
    {
        UsageReadResult accountResult = ReadAccountSnapshot();
        if (accountResult.Snapshot is not null)
        {
            return accountResult;
        }

        UsageReadResult sessionResult = ReadLatestSessionSnapshot();
        if (sessionResult.Snapshot is not null)
        {
            return sessionResult;
        }

        string error = string.Join(
            " ",
            new[] { accountResult.ErrorMessage, sessionResult.ErrorMessage }
                .Where(message => !string.IsNullOrWhiteSpace(message)));
        return new UsageReadResult(null, sessionResult.SparkSnapshot, error);
    }

    private UsageReadResult ReadAccountSnapshot()
    {
        if (!File.Exists(AuthPath))
        {
            return new UsageReadResult(null, null, $"Missing Codex credentials file: {AuthPath}");
        }

        try
        {
            CodexCredential credential = ReadCredential();
            using HttpRequestMessage request = new(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "limits/1.3");

            using HttpResponseMessage response = HttpClient.Send(request);
            string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return new UsageReadResult(
                    null,
                    null,
                    $"Codex account usage request failed: HTTP {(int)response.StatusCode}");
            }

            UsageReadResult? parsed = ParseAccountUsageResponse(responseBody);
            return parsed ?? new UsageReadResult(null, null, "Codex account usage response did not include a regular limit.");
        }
        catch (Exception exception)
        {
            return new UsageReadResult(null, null, exception.Message);
        }
    }

    private CodexCredential ReadCredential()
    {
        using FileStream stream = new(AuthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("tokens", out JsonElement tokens) ||
            !tokens.TryGetProperty("access_token", out JsonElement accessTokenElement) ||
            !tokens.TryGetProperty("account_id", out JsonElement accountIdElement) ||
            string.IsNullOrWhiteSpace(accessTokenElement.GetString()) ||
            string.IsNullOrWhiteSpace(accountIdElement.GetString()))
        {
            throw new InvalidOperationException("Codex ChatGPT OAuth credentials were not found.");
        }

        return new CodexCredential(accessTokenElement.GetString()!, accountIdElement.GetString()!);
    }

    private static UsageReadResult? ParseAccountUsageResponse(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        string? planType = root.TryGetProperty("plan_type", out JsonElement planTypeElement)
            ? planTypeElement.GetString()
            : null;
        if (!root.TryGetProperty("rate_limit", out JsonElement regularLimit))
        {
            return null;
        }

        DateTimeOffset timestamp = DateTimeOffset.Now;
        CodexUsageSnapshot? regularSnapshot = ParseAccountLimit(
            regularLimit,
            timestamp,
            planType,
            model: null);
        if (regularSnapshot is null)
        {
            return null;
        }

        CodexUsageSnapshot? sparkSnapshot = null;
        if (root.TryGetProperty("additional_rate_limits", out JsonElement additionalLimits) &&
            additionalLimits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement additionalLimit in additionalLimits.EnumerateArray())
            {
                string? limitName = additionalLimit.TryGetProperty("limit_name", out JsonElement limitNameElement)
                    ? limitNameElement.GetString()
                    : null;
                string? meteredFeature = additionalLimit.TryGetProperty("metered_feature", out JsonElement meteredFeatureElement)
                    ? meteredFeatureElement.GetString()
                    : null;
                bool isSpark = limitName?.Contains("spark", StringComparison.OrdinalIgnoreCase) == true ||
                               meteredFeature?.Contains("bengalfox", StringComparison.OrdinalIgnoreCase) == true;
                if (!isSpark || !additionalLimit.TryGetProperty("rate_limit", out JsonElement sparkLimit))
                {
                    continue;
                }

                sparkSnapshot = ParseAccountLimit(sparkLimit, timestamp, planType, limitName ?? meteredFeature);
                break;
            }
        }

        return new UsageReadResult(regularSnapshot, sparkSnapshot, null);
    }

    private static CodexUsageSnapshot? ParseAccountLimit(
        JsonElement limit,
        DateTimeOffset timestamp,
        string? planType,
        string? model)
    {
        RateLimitInfo primary = ReadAccountLimitWindow(limit, "primary_window");
        RateLimitInfo secondary = ReadAccountLimitWindow(limit, "secondary_window");
        if (primary.WindowMinutes <= 0 && secondary.WindowMinutes <= 0)
        {
            return null;
        }

        return new CodexUsageSnapshot(
            timestamp,
            primary.UsedPercent,
            secondary.UsedPercent,
            primary.WindowMinutes,
            secondary.WindowMinutes,
            primary.ResetsAt,
            secondary.ResetsAt,
            planType,
            model,
            UsageEndpoint);
    }

    private static RateLimitInfo ReadAccountLimitWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement window) || window.ValueKind != JsonValueKind.Object)
        {
            return new RateLimitInfo(0, 0, null);
        }

        double usedPercent = window.TryGetProperty("used_percent", out JsonElement usedPercentElement)
            ? usedPercentElement.GetDouble()
            : 0;
        int windowMinutes = window.TryGetProperty("limit_window_seconds", out JsonElement windowSecondsElement) &&
                            windowSecondsElement.TryGetInt32(out int windowSeconds)
            ? Math.Max(1, windowSeconds / 60)
            : 0;
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("reset_at", out JsonElement resetAtElement) &&
            resetAtElement.TryGetInt64(out long resetAtSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetAtSeconds);
        }

        return new RateLimitInfo(usedPercent, windowMinutes, resetsAt);
    }

    private UsageReadResult ReadLatestSessionSnapshot()
    {
        if (!Directory.Exists(SessionsPath))
        {
            return new UsageReadResult(null, null, $"Missing sessions folder: {SessionsPath}");
        }

        try
        {
            List<FileInfo> recentFiles = Directory
                .EnumerateFiles(SessionsPath, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxFilesToScan)
                .ToList();

            CodexUsageSnapshot? latestSnapshot = null;
            CodexUsageSnapshot? latestSparkSnapshot = null;

            for (int index = 0; index < recentFiles.Count; index++)
            {
                UsageFileSnapshot fileSnapshot = TryReadFile(
                    recentFiles[index].FullName,
                    allowExpandedTailScan: index < MaxExpandedTailFilesToScan);
                if (fileSnapshot.Latest is { } candidate &&
                    (latestSnapshot is null || candidate.Timestamp > latestSnapshot.Timestamp))
                {
                    latestSnapshot = candidate;
                }

                if (fileSnapshot.LatestSpark is { } sparkCandidate &&
                    (latestSparkSnapshot is null || sparkCandidate.Timestamp > latestSparkSnapshot.Timestamp))
                {
                    latestSparkSnapshot = sparkCandidate;
                }
            }

            return latestSnapshot is null
                ? new UsageReadResult(null, latestSparkSnapshot, "No token_count events were found in recent sessions.")
                : new UsageReadResult(latestSnapshot, latestSparkSnapshot, null);
        }
        catch (Exception exception)
        {
            return new UsageReadResult(null, null, exception.Message);
        }
    }

    private static string ResolveCodexHome()
    {
        string? configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Environment.ExpandEnvironmentVariables(configuredHome);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".codex");
    }

    private static UsageFileSnapshot TryReadFile(string path, bool allowExpandedTailScan)
    {
        using FileStream stream = OpenSharedReadStream(path);
        string? currentModel = stream.Length > MaxTailBytesToRead
            ? TryReadInitialModel(stream)
            : null;

        UsageFileSnapshot snapshot = TryReadFileWindow(stream, path, currentModel, MaxTailBytesToRead);
        if (snapshot.HasAny || !allowExpandedTailScan || stream.Length <= MaxTailBytesToRead)
        {
            return snapshot;
        }

        return TryReadFileWindow(stream, path, currentModel, MaxExpandedTailBytesToRead);
    }

    private static UsageFileSnapshot TryReadFileWindow(FileStream stream, string path, string? currentModel, int maxBytesToRead)
    {
        CodexUsageSnapshot? latest = null;
        CodexUsageSnapshot? latestSpark = null;
        bool startedInsideFile = SeekToRecentTail(stream, maxBytesToRead);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        if (startedInsideFile)
        {
            _ = reader.ReadLine();
        }

        while (reader.ReadLine() is { } line)
        {
            bool isTurnContext = line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal);
            bool isTokenCount = line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal);
            if (!isTurnContext && !isTokenCount)
            {
                continue;
            }

            if (isTurnContext && TryReadTurnContextModel(line, out string? model))
            {
                currentModel = model;
                continue;
            }

            if (!isTokenCount)
            {
                continue;
            }

            CodexUsageSnapshot? parsed = TryParseLine(line, path, currentModel);
            if (parsed is null)
            {
                continue;
            }

            bool isSpark = IsSparkSnapshot(parsed);
            if (!isSpark && (latest is null || parsed.Timestamp > latest.Timestamp))
            {
                latest = parsed;
            }

            if (isSpark && (latestSpark is null || parsed.Timestamp > latestSpark.Timestamp))
            {
                latestSpark = parsed;
            }
        }

        return new UsageFileSnapshot(latest, latestSpark);
    }

    private static FileStream OpenSharedReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
    }

    private static string? TryReadInitialModel(FileStream stream)
    {
        long originalPosition = stream.Position;
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            using StreamReader reader = new(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: true);

            for (int lineIndex = 0; lineIndex < MaxModelPrefixLinesToRead && reader.ReadLine() is { } line; lineIndex++)
            {
                if (line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal) &&
                    TryReadTurnContextModel(line, out string? model) &&
                    !string.IsNullOrWhiteSpace(model))
                {
                    return model;
                }
            }
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }

        return null;
    }

    private static bool SeekToRecentTail(FileStream stream, int maxBytesToRead)
    {
        if (stream.Length <= maxBytesToRead)
        {
            stream.Seek(0, SeekOrigin.Begin);
            return false;
        }

        stream.Seek(-maxBytesToRead, SeekOrigin.End);
        return true;
    }

    private static bool TryReadTurnContextModel(string line, out string? model)
    {
        model = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                !string.Equals(typeElement.GetString(), "turn_context", StringComparison.Ordinal))
            {
                return false;
            }

            if (root.TryGetProperty("payload", out JsonElement payload) &&
                payload.TryGetProperty("model", out JsonElement modelElement))
            {
                model = modelElement.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static CodexUsageSnapshot? TryParseLine(string line, string sourceFile, string? model)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("payload", out JsonElement payload) ||
                !payload.TryGetProperty("type", out JsonElement payloadType) ||
                !string.Equals(payloadType.GetString(), "token_count", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("timestamp", out JsonElement timestampElement) ||
                !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
            {
                return null;
            }

            if (!payload.TryGetProperty("rate_limits", out JsonElement rateLimits))
            {
                return null;
            }

            RateLimitInfo primary = ReadLimit(rateLimits, "primary");
            RateLimitInfo secondary = ReadLimit(rateLimits, "secondary");
            string? planType = rateLimits.TryGetProperty("plan_type", out JsonElement planTypeElement)
                ? planTypeElement.GetString()
                : null;
            string? snapshotModel = ResolveSnapshotModel(rateLimits, payload, model);

            return new CodexUsageSnapshot(
                timestamp,
                primary.UsedPercent,
                secondary.UsedPercent,
                primary.WindowMinutes,
                secondary.WindowMinutes,
                primary.ResetsAt,
                secondary.ResetsAt,
                planType,
                snapshotModel,
                sourceFile);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? ResolveSnapshotModel(JsonElement rateLimits, JsonElement payload, string? contextModel)
    {
        if (rateLimits.TryGetProperty("limit_name", out JsonElement limitNameElement))
        {
            string? limitName = limitNameElement.GetString();
            if (!string.IsNullOrWhiteSpace(limitName))
            {
                return limitName;
            }
        }

        if (rateLimits.TryGetProperty("limit_id", out JsonElement limitIdElement))
        {
            string? limitId = limitIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(limitId) &&
                !string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase))
            {
                return limitId;
            }
        }

        if (payload.TryGetProperty("info", out JsonElement info))
        {
            if (info.TryGetProperty("current_model", out JsonElement currentModelElement))
            {
                string? currentModel = currentModelElement.GetString();
                if (!string.IsNullOrWhiteSpace(currentModel))
                {
                    return currentModel;
                }
            }

            if (info.TryGetProperty("model", out JsonElement modelElement))
            {
                string? model = modelElement.GetString();
                if (!string.IsNullOrWhiteSpace(model))
                {
                    return model;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(contextModel))
        {
            return contextModel;
        }

        return null;
    }

    private static RateLimitInfo ReadLimit(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement limit) ||
            limit.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new RateLimitInfo(0, 0, null);
        }

        double usedPercent = limit.TryGetProperty("used_percent", out JsonElement usedPercentElement)
            ? usedPercentElement.GetDouble()
            : 0;

        int windowMinutes = limit.TryGetProperty("window_minutes", out JsonElement windowMinutesElement)
            ? windowMinutesElement.GetInt32()
            : 0;

        DateTimeOffset? resetsAt = null;
        if (limit.TryGetProperty("resets_at", out JsonElement resetsAtElement) &&
            resetsAtElement.ValueKind is JsonValueKind.Number &&
            resetsAtElement.TryGetInt64(out long resetsAtSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAtSeconds);
        }

        return new RateLimitInfo(usedPercent, windowMinutes, resetsAt);
    }

    private static bool IsSparkSnapshot(CodexUsageSnapshot snapshot)
    {
        return snapshot.Model?.Contains("spark", StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed record UsageFileSnapshot(CodexUsageSnapshot? Latest, CodexUsageSnapshot? LatestSpark)
    {
        public bool HasAny => Latest is not null || LatestSpark is not null;
    }

    private sealed record RateLimitInfo(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);

    private sealed record CodexCredential(string AccessToken, string AccountId);
}

internal sealed record ClaudeUsageSnapshot(
    DateTimeOffset Timestamp,
    double FiveHourUsedPercent,
    double SevenDayUsedPercent,
    DateTimeOffset? FiveHourResetAt,
    DateTimeOffset? SevenDayResetAt,
    string SourceFile);

internal sealed record ClaudeUsageReadResult(ClaudeUsageSnapshot? Snapshot, string? ErrorMessage);

internal sealed class ClaudeUsageReader
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly object CredentialRefreshLock = new();
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(7)
    };

    public string ClaudeHomePath { get; } = ResolveClaudeHome();
    public string CredentialsPath => Path.Combine(ClaudeHomePath, ".credentials.json");

    public ClaudeUsageReadResult ReadLatestSnapshot()
    {
        try
        {
            ClaudeCredential credential = ReadCredential();
            ClaudeHttpResponse response = SendUsageRequest(credential.AccessToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && credential.CanRefresh)
            {
                credential = RefreshCredential(credential);
                response = SendUsageRequest(credential.AccessToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                string reason = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Claude OAuth token expired and could not be refreshed - run `claude login`"
                    : $"Claude usage request failed: HTTP {(int)response.StatusCode}";
                return new ClaudeUsageReadResult(null, reason);
            }

            ClaudeUsageSnapshot? snapshot = ParseUsageResponse(response.Body);
            return snapshot is null
                ? new ClaudeUsageReadResult(null, "Claude usage response did not include five_hour and seven_day limits.")
                : new ClaudeUsageReadResult(snapshot, null);
        }
        catch (Exception exception)
        {
            return new ClaudeUsageReadResult(null, exception.Message);
        }
    }

    private static ClaudeHttpResponse SendUsageRequest(string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, UsageEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "limits/1.3");

        using HttpResponseMessage response = HttpClient.Send(request);
        string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return new ClaudeHttpResponse(response.StatusCode, responseBody);
    }

    private static string ResolveClaudeHome()
    {
        string? configuredHome = Environment.GetEnvironmentVariable("CLAUDE_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Environment.ExpandEnvironmentVariables(configuredHome);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude");
    }

    private ClaudeCredential ReadCredential()
    {
        string? environmentToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return new ClaudeCredential(environmentToken, null, true);
        }

        if (!File.Exists(CredentialsPath))
        {
            throw new FileNotFoundException($"Missing Claude credentials file: {CredentialsPath}", CredentialsPath);
        }

        return ReadStoredCredential();
    }

    private ClaudeCredential ReadStoredCredential()
    {
        using FileStream stream = new(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("claudeAiOauth", out JsonElement oauth) ||
            !oauth.TryGetProperty("accessToken", out JsonElement accessTokenElement) ||
            string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
        {
            throw new InvalidOperationException("Claude OAuth access token was not found.");
        }

        string? refreshToken = oauth.TryGetProperty("refreshToken", out JsonElement refreshTokenElement)
            ? refreshTokenElement.GetString()
            : null;
        return new ClaudeCredential(accessTokenElement.GetString()!, refreshToken, false);
    }

    private ClaudeCredential RefreshCredential(ClaudeCredential staleCredential)
    {
        lock (CredentialRefreshLock)
        {
            ClaudeCredential currentCredential = ReadStoredCredential();
            if (!string.Equals(currentCredential.AccessToken, staleCredential.AccessToken, StringComparison.Ordinal) ||
                !string.Equals(currentCredential.RefreshToken, staleCredential.RefreshToken, StringComparison.Ordinal))
            {
                return currentCredential;
            }

            if (string.IsNullOrWhiteSpace(currentCredential.RefreshToken))
            {
                throw new InvalidOperationException("Claude OAuth refresh token was not found.");
            }

            string requestBody = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = currentCredential.RefreshToken,
                ["client_id"] = OAuthClientId
            }.ToJsonString();
            using HttpRequestMessage request = new(HttpMethod.Post, TokenEndpoint)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "limits/1.3");

            using HttpResponseMessage response = HttpClient.Send(request);
            string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                ClaudeCredential latestCredential = ReadStoredCredential();
                if (!string.Equals(latestCredential.AccessToken, currentCredential.AccessToken, StringComparison.Ordinal))
                {
                    return latestCredential;
                }

                throw new InvalidOperationException($"Claude OAuth refresh failed: HTTP {(int)response.StatusCode}");
            }

            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("access_token", out JsonElement accessTokenElement) ||
                string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
            {
                throw new InvalidOperationException("Claude OAuth refresh response did not include an access token.");
            }

            string accessToken = accessTokenElement.GetString()!;
            string refreshToken = root.TryGetProperty("refresh_token", out JsonElement refreshTokenElement) &&
                                  !string.IsNullOrWhiteSpace(refreshTokenElement.GetString())
                ? refreshTokenElement.GetString()!
                : currentCredential.RefreshToken;
            long expiresInSeconds = root.TryGetProperty("expires_in", out JsonElement expiresInElement) &&
                                    expiresInElement.TryGetInt64(out long expiresIn)
                ? expiresIn
                : 28_800;
            long expiresAtMilliseconds = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeMilliseconds();

            return WriteRefreshedCredential(
                currentCredential,
                accessToken,
                refreshToken,
                expiresAtMilliseconds);
        }
    }

    private ClaudeCredential WriteRefreshedCredential(
        ClaudeCredential originalCredential,
        string accessToken,
        string refreshToken,
        long expiresAtMilliseconds)
    {
        ClaudeCredential latestCredential = ReadStoredCredential();
        if (!string.Equals(latestCredential.RefreshToken, originalCredential.RefreshToken, StringComparison.Ordinal))
        {
            return latestCredential;
        }

        JsonNode? root;
        using (FileStream stream = new(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            root = JsonNode.Parse(stream);
        }

        if (root?["claudeAiOauth"] is not JsonObject oauth)
        {
            throw new InvalidOperationException("Claude OAuth credential object was not found.");
        }

        oauth["accessToken"] = accessToken;
        oauth["refreshToken"] = refreshToken;
        oauth["expiresAt"] = expiresAtMilliseconds;

        string temporaryPath = $"{CredentialsPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, CredentialsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new ClaudeCredential(accessToken, refreshToken, false);
    }

    private static ClaudeUsageSnapshot? ParseUsageResponse(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;

        if (!TryReadLimit(root, "five_hour", out UsageLimit fiveHour) ||
            !TryReadLimit(root, "seven_day", out UsageLimit sevenDay))
        {
            return null;
        }

        return new ClaudeUsageSnapshot(
            DateTimeOffset.Now,
            fiveHour.UsedPercent,
            sevenDay.UsedPercent,
            fiveHour.ResetsAt,
            sevenDay.ResetsAt,
            UsageEndpoint);
    }

    private static bool TryReadLimit(JsonElement root, string name, out UsageLimit limit)
    {
        limit = new UsageLimit(0, null);
        if (!root.TryGetProperty(name, out JsonElement limitElement) ||
            limitElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        double usedPercent = limitElement.TryGetProperty("utilization", out JsonElement utilizationElement)
            ? utilizationElement.GetDouble()
            : 0;

        DateTimeOffset? resetsAt = null;
        if (limitElement.TryGetProperty("resets_at", out JsonElement resetsAtElement) &&
            resetsAtElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetsAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedReset))
        {
            resetsAt = parsedReset;
        }

        limit = new UsageLimit(usedPercent, resetsAt);
        return true;
    }

    private sealed record UsageLimit(double UsedPercent, DateTimeOffset? ResetsAt);

    private sealed record ClaudeHttpResponse(System.Net.HttpStatusCode StatusCode, string Body)
    {
        public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
    }

    private sealed record ClaudeCredential(string AccessToken, string? RefreshToken, bool IsEnvironmentToken)
    {
        public bool CanRefresh => !IsEnvironmentToken && !string.IsNullOrWhiteSpace(RefreshToken);
    }
}

internal static class KimiUsageMath
{
    public static int GetRemainingPercent(double remainingPercent)
    {
        return Math.Clamp((int)Math.Floor(remainingPercent), 0, 100);
    }
}

internal sealed record KimiUsageSnapshot(
    DateTimeOffset Timestamp,
    double FiveHourUsedPercent,
    double SevenDayUsedPercent,
    double FiveHourRemainingPercent,
    double SevenDayRemainingPercent,
    DateTimeOffset? FiveHourResetAt,
    DateTimeOffset? SevenDayResetAt,
    long SpentTokens,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CachedReadTokens,
    int RecordCount,
    string SourceFile);

internal sealed record KimiUsageReadResult(KimiUsageSnapshot? Snapshot, string? ErrorMessage);

internal sealed class KimiUsageReader
{
    private const int MaxFilesToScan = 64;
    private const int MaxTailBytesToRead = 16 * 1024 * 1024;
    private const int MaxCredentialReadAttempts = 5;
    private const int MaxUsageRequestAttempts = 3;
    private const string DefaultKimiCodeBaseUrl = "https://api.kimi.com/coding/v1";
    private const string DefaultOAuthHost = "https://auth.kimi.com";
    private const string KimiClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly string _usageBaseUrl = ResolveKimiCodeBaseUrl();
    private readonly string _oauthHost = ResolveOAuthHost();
    private string? _cachedAccessToken;
    private long? _cachedExpiresAt;

    public string KimiHomePath { get; } = ResolveKimiHome();
    public string SessionsPath => Path.Combine(KimiHomePath, "sessions");
    public string CredentialsPath => Path.Combine(KimiHomePath, "credentials", "kimi-code.json");
    public string UsageEndpoint => $"{_usageBaseUrl}/usages";

    public KimiUsageReadResult ReadLatestSnapshot()
    {
        try
        {
            KimiCredential credential = ReadAccessToken(forceRefresh: false);
            KimiQuotaSnapshot quotaSnapshot = ReadQuotaSnapshot(credential);
            KimiLocalTokenSnapshot tokenSnapshot = ReadLocalTokenSnapshot();

            return new KimiUsageReadResult(
                new KimiUsageSnapshot(
                    DateTimeOffset.Now,
                    quotaSnapshot.FiveHour.UsedPercent,
                    quotaSnapshot.SevenDay.UsedPercent,
                    quotaSnapshot.FiveHour.RemainingPercent,
                    quotaSnapshot.SevenDay.RemainingPercent,
                    quotaSnapshot.FiveHour.ResetAt,
                    quotaSnapshot.SevenDay.ResetAt,
                    tokenSnapshot.SpentTokens,
                    tokenSnapshot.InputTokens,
                    tokenSnapshot.OutputTokens,
                    tokenSnapshot.CacheCreationTokens,
                    tokenSnapshot.CachedReadTokens,
                    tokenSnapshot.RecordCount,
                    UsageEndpoint),
                null);
        }
        catch (Exception exception)
        {
            return new KimiUsageReadResult(null, exception.Message);
        }
    }

    private KimiQuotaSnapshot ReadQuotaSnapshot(KimiCredential credential)
    {
        KimiUsageHttpResponse response = SendUsageRequest(credential.AccessToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && credential.CanRefresh)
        {
            credential = ReadAccessToken(forceRefresh: true);
            response = SendUsageRequest(credential.AccessToken);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            response = SendUsageRequest(credential.AccessToken, useSingularEndpoint: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => throw new InvalidOperationException("Kimi token expired - run `kimi login` or set KIMI_API_KEY."),
                System.Net.HttpStatusCode.TooManyRequests => throw new InvalidOperationException("Kimi usage endpoint rate limited the request."),
                _ => throw new InvalidOperationException($"Kimi usage request failed: HTTP {(int)response.StatusCode}")
            };
        }

        return ParseQuotaResponse(response.Body);
    }

    private KimiUsageHttpResponse SendUsageRequest(string accessToken, bool useSingularEndpoint = false)
    {
        string endpoint = useSingularEndpoint
            ? $"{_usageBaseUrl}/usage"
            : UsageEndpoint;

        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaxUsageRequestAttempts; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "KimiCLI/1.6 limits/1.0");

                using HttpResponseMessage response = HttpClient.Send(request);
                string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                KimiUsageHttpResponse result = new(response.StatusCode, response.IsSuccessStatusCode, responseBody);
                if (result.IsSuccessStatusCode ||
                    !IsTransientStatusCode(result.StatusCode) ||
                    attempt == MaxUsageRequestAttempts)
                {
                    return result;
                }
            }
            catch (Exception exception) when (IsTransientHttpException(exception))
            {
                lastException = exception;
                if (attempt == MaxUsageRequestAttempts)
                {
                    break;
                }
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(300 * attempt));
        }

        throw new InvalidOperationException($"Kimi usage request failed: {lastException?.Message ?? "transient HTTP failure"}");
    }

    private static bool IsTransientStatusCode(System.Net.HttpStatusCode statusCode)
    {
        int status = (int)statusCode;
        return status == 408 || status >= 500;
    }

    private static bool IsTransientHttpException(Exception exception)
    {
        return exception is HttpRequestException or TaskCanceledException or IOException;
    }

    private KimiCredential ReadAccessToken(bool forceRefresh)
    {
        string? environmentToken = ReadEnvironmentToken();
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return new KimiCredential(environmentToken, CanRefresh: false);
        }

        if (!File.Exists(CredentialsPath))
        {
            if (TryGetCachedCredential() is { } cachedCredential)
            {
                return cachedCredential;
            }

            throw new InvalidOperationException($"Missing Kimi credentials file: {CredentialsPath}");
        }

        JsonObject credentials;
        try
        {
            credentials = ReadCredentialsObject();
        }
        catch (Exception exception) when (IsTransientCredentialException(exception))
        {
            if (TryGetCachedCredential() is { } cachedCredential)
            {
                return cachedCredential;
            }

            throw new InvalidOperationException($"Kimi credentials could not be read: {exception.Message}");
        }

        string? accessToken = credentials["access_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            if (TryGetCachedCredential() is { } cachedCredential)
            {
                return cachedCredential;
            }

            throw new InvalidOperationException("Kimi access_token was not found.");
        }

        long? expiresAt = TryReadLong(credentials["expires_at"]);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!forceRefresh && (expiresAt is null || now < expiresAt.Value - 30))
        {
            return CacheCredential(accessToken, expiresAt, canRefresh: true);
        }

        string? refreshToken = credentials["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Kimi access token expired and refresh_token was not found.");
        }

        return RefreshAccessToken(credentials, refreshToken);
    }

    private static string? ReadEnvironmentToken()
    {
        foreach (string name in new[] { "KIMI_API_KEY", "KIMI_CODING_API_KEY", "KIMI_API_CODE", "KIMI_CODE_API_KEY", "KIMI_CODE_ACCESS_TOKEN" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private KimiCredential? TryGetCachedCredential()
    {
        if (string.IsNullOrWhiteSpace(_cachedAccessToken))
        {
            return null;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_cachedExpiresAt is not null && now >= _cachedExpiresAt.Value - 30)
        {
            return null;
        }

        return new KimiCredential(_cachedAccessToken, CanRefresh: true);
    }

    private KimiCredential CacheCredential(string accessToken, long? expiresAt, bool canRefresh)
    {
        _cachedAccessToken = accessToken;
        _cachedExpiresAt = expiresAt;
        return new KimiCredential(accessToken, canRefresh);
    }

    private static bool IsTransientCredentialException(Exception exception)
    {
        return exception is IOException or JsonException or UnauthorizedAccessException;
    }

    private KimiCredential RefreshAccessToken(JsonObject credentials, string refreshToken)
    {
        string tokenEndpoint = $"{_oauthHost}/api/oauth/token";
        using HttpRequestMessage request = new(HttpMethod.Post, tokenEndpoint);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "KimiCLI/1.6 limits/1.0");

        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = KimiClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        using HttpResponseMessage response = HttpClient.Send(request);
        string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kimi OAuth refresh failed: HTTP {(int)response.StatusCode}");
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        string? accessToken = root.TryGetProperty("access_token", out JsonElement accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Kimi OAuth refresh response did not include access_token.");
        }

        if (!root.TryGetProperty("expires_in", out JsonElement expiresInElement) ||
            !TryReadDouble(expiresInElement, out double expiresIn) ||
            expiresIn <= 0)
        {
            throw new InvalidOperationException("Kimi OAuth refresh response did not include expires_in.");
        }

        string newRefreshToken = root.TryGetProperty("refresh_token", out JsonElement refreshTokenElement)
            ? refreshTokenElement.GetString() ?? refreshToken
            : refreshToken;
        long newExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds();

        credentials["version"] = "1.0";
        credentials["type"] = "oauth_token";
        credentials["access_token"] = accessToken;
        credentials["expires_at"] = newExpiresAt;
        credentials["refresh_token"] = newRefreshToken;
        WriteCredentialsObject(credentials);

        return CacheCredential(accessToken, newExpiresAt, canRefresh: true);
    }

    private JsonObject ReadCredentialsObject()
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaxCredentialReadAttempts; attempt++)
        {
            try
            {
                using FileStream stream = new(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                JsonNode? node = JsonNode.Parse(stream);
                return node as JsonObject ?? throw new InvalidOperationException("Kimi credentials file is not a JSON object.");
            }
            catch (Exception exception) when (IsTransientCredentialException(exception))
            {
                lastException = exception;
                if (attempt == MaxCredentialReadAttempts)
                {
                    break;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }

        throw lastException ?? new IOException("Kimi credentials file could not be read.");
    }

    private void WriteCredentialsObject(JsonObject credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialsPath)!);
        string tempPath = $"{CredentialsPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(
            tempPath,
            credentials.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        File.Move(tempPath, CredentialsPath, overwrite: true);
    }

    private KimiLocalTokenSnapshot ReadLocalTokenSnapshot()
    {
        DateTimeOffset windowStart = DateTimeOffset.Now.AddHours(-24);
        if (!Directory.Exists(SessionsPath))
        {
            return KimiLocalTokenSnapshot.Empty(windowStart);
        }

        try
        {
            long windowStartMilliseconds = windowStart.ToUnixTimeMilliseconds();
            KimiUsageAccumulator accumulator = new(windowStart);

            foreach (FileInfo file in Directory
                         .EnumerateFiles(SessionsPath, "wire.jsonl", SearchOption.AllDirectories)
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Take(MaxFilesToScan))
            {
                TryReadFile(file.FullName, windowStartMilliseconds, accumulator);
            }

            return accumulator.ToSnapshot();
        }
        catch
        {
            return KimiLocalTokenSnapshot.Empty(windowStart);
        }
    }

    private static KimiQuotaSnapshot ParseQuotaResponse(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("usage", out JsonElement weeklyUsage) ||
            weeklyUsage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Kimi usage response did not include weekly usage.");
        }

        KimiQuotaLimit sevenDay = ReadQuotaLimit(weeklyUsage);
        KimiQuotaLimit? fiveHour = null;
        KimiQuotaLimit? firstLimit = null;

        if (root.TryGetProperty("limits", out JsonElement limits) &&
            limits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in limits.EnumerateArray())
            {
                JsonElement detail = item.TryGetProperty("detail", out JsonElement detailElement) &&
                                     detailElement.ValueKind == JsonValueKind.Object
                    ? detailElement
                    : item;

                KimiQuotaLimit candidate = ReadQuotaLimit(detail);
                firstLimit ??= candidate;

                if (IsFiveHourLimit(item))
                {
                    fiveHour = candidate;
                    break;
                }
            }
        }

        fiveHour ??= firstLimit ?? throw new InvalidOperationException("Kimi usage response did not include a 5-hour limit.");
        return new KimiQuotaSnapshot(fiveHour, sevenDay);
    }

    private static bool IsFiveHourLimit(JsonElement limitElement)
    {
        if (!limitElement.TryGetProperty("window", out JsonElement window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        double? duration = ReadJsonDouble(window, "duration");
        string timeUnit = ReadJsonString(window, "timeUnit") ?? ReadJsonString(window, "time_unit") ?? string.Empty;

        return duration == 300 && timeUnit.Contains("MINUTE", StringComparison.OrdinalIgnoreCase) ||
               duration == 5 && timeUnit.Contains("HOUR", StringComparison.OrdinalIgnoreCase);
    }

    private static KimiQuotaLimit ReadQuotaLimit(JsonElement data)
    {
        double limit = ReadJsonDouble(data, "limit") ?? ReadJsonDouble(data, "limit_amount") ?? 0d;
        double used = ReadJsonDouble(data, "used") ?? ReadJsonDouble(data, "used_amount") ?? 0d;
        double? remaining = ReadJsonDouble(data, "remaining");

        double usedPercent = limit > 0 ? used / limit * 100d : used;
        double remainingPercent = limit > 0
            ? (remaining ?? Math.Max(0d, limit - used)) / limit * 100d
            : Math.Max(0d, 100d - usedPercent);

        return new KimiQuotaLimit(
            Math.Clamp(usedPercent, 0d, 100d),
            Math.Clamp(remainingPercent, 0d, 100d),
            ReadResetAt(data));
    }

    private static DateTimeOffset? ReadResetAt(JsonElement data)
    {
        foreach (string propertyName in new[] { "resetTime", "reset_time", "resetAt", "reset_at" })
        {
            if (!data.TryGetProperty(propertyName, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                return parsed;
            }

            if (element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt64(out long timestamp))
            {
                return timestamp > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
        }

        return null;
    }

    private static string ResolveKimiHome()
    {
        string? configuredHome = Environment.GetEnvironmentVariable("KIMI_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Environment.ExpandEnvironmentVariables(configuredHome);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".kimi-code");
    }

    private static string ResolveKimiCodeBaseUrl()
    {
        string? configuredBaseUrl = Environment.GetEnvironmentVariable("KIMI_CODE_BASE_URL");
        return string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? DefaultKimiCodeBaseUrl
            : configuredBaseUrl.TrimEnd('/');
    }

    private static string ResolveOAuthHost()
    {
        string? configuredHost = Environment.GetEnvironmentVariable("KIMI_CODE_OAUTH_HOST") ??
                                 Environment.GetEnvironmentVariable("KIMI_OAUTH_HOST");
        return string.IsNullOrWhiteSpace(configuredHost)
            ? DefaultOAuthHost
            : configuredHost.TrimEnd('/');
    }

    private static void TryReadFile(string path, long windowStartMilliseconds, KimiUsageAccumulator accumulator)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            bool startedInsideFile = SeekToRecentTail(stream);
            using StreamReader reader = new(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            if (startedInsideFile)
            {
                _ = reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"type\":\"usage.record\"", StringComparison.Ordinal))
                {
                    continue;
                }

                TryAddUsageRecord(line, path, windowStartMilliseconds, accumulator);
            }
        }
        catch (IOException)
        {
            // Kimi may be actively writing a session file; skip that file for this refresh.
        }
        catch (UnauthorizedAccessException)
        {
            // A single unreadable file should not hide usage from the other sessions.
        }
    }

    private static bool SeekToRecentTail(FileStream stream)
    {
        if (stream.Length <= MaxTailBytesToRead)
        {
            stream.Seek(0, SeekOrigin.Begin);
            return false;
        }

        stream.Seek(-MaxTailBytesToRead, SeekOrigin.End);
        return true;
    }

    private static void TryAddUsageRecord(
        string line,
        string sourceFile,
        long windowStartMilliseconds,
        KimiUsageAccumulator accumulator)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                !string.Equals(typeElement.GetString(), "usage.record", StringComparison.Ordinal))
            {
                return;
            }

            if (!root.TryGetProperty("time", out JsonElement timeElement) ||
                !timeElement.TryGetInt64(out long timestampMilliseconds) ||
                timestampMilliseconds < windowStartMilliseconds)
            {
                return;
            }

            if (!root.TryGetProperty("usage", out JsonElement usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            accumulator.Add(
                DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds),
                ReadUsageTokenValue(usage, "inputOther"),
                ReadUsageTokenValue(usage, "output"),
                ReadUsageTokenValue(usage, "inputCacheCreation"),
                ReadUsageTokenValue(usage, "inputCacheRead"),
                sourceFile);
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static long ReadUsageTokenValue(JsonElement usage, string propertyName)
    {
        return usage.TryGetProperty(propertyName, out JsonElement element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out long value)
            ? Math.Max(0, value)
            : 0;
    }

    private static double? ReadJsonDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement valueElement) &&
               TryReadDouble(valueElement, out double value)
            ? value
            : null;
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement valueElement) &&
               valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString()
            : null;
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static long? TryReadLong(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<long>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record KimiCredential(string AccessToken, bool CanRefresh);

    private sealed record KimiUsageHttpResponse(System.Net.HttpStatusCode StatusCode, bool IsSuccessStatusCode, string Body);

    private sealed record KimiQuotaSnapshot(KimiQuotaLimit FiveHour, KimiQuotaLimit SevenDay);

    private sealed record KimiQuotaLimit(double UsedPercent, double RemainingPercent, DateTimeOffset? ResetAt);

    private sealed record KimiLocalTokenSnapshot(
        DateTimeOffset WindowStart,
        long SpentTokens,
        long InputTokens,
        long OutputTokens,
        long CacheCreationTokens,
        long CachedReadTokens,
        int RecordCount)
    {
        public static KimiLocalTokenSnapshot Empty(DateTimeOffset windowStart)
        {
            return new KimiLocalTokenSnapshot(windowStart, 0, 0, 0, 0, 0, 0);
        }
    }

    private sealed class KimiUsageAccumulator
    {
        private readonly DateTimeOffset _windowStart;
        private long _inputTokens;
        private long _outputTokens;
        private long _cacheCreationTokens;
        private long _cachedReadTokens;
        private int _recordCount;

        public KimiUsageAccumulator(DateTimeOffset windowStart)
        {
            _windowStart = windowStart;
        }

        public void Add(
            DateTimeOffset timestamp,
            long inputTokens,
            long outputTokens,
            long cacheCreationTokens,
            long cachedReadTokens,
            string sourceFile)
        {
            _ = timestamp;
            _ = sourceFile;
            _inputTokens += inputTokens;
            _outputTokens += outputTokens;
            _cacheCreationTokens += cacheCreationTokens;
            _cachedReadTokens += cachedReadTokens;
            _recordCount++;
        }

        public KimiLocalTokenSnapshot ToSnapshot()
        {
            long spentTokens = _inputTokens + _outputTokens + _cacheCreationTokens;
            return new KimiLocalTokenSnapshot(
                _windowStart,
                spentTokens,
                _inputTokens,
                _outputTokens,
                _cacheCreationTokens,
                _cachedReadTokens,
                _recordCount);
        }
    }
}

internal sealed record DeepSeekBalanceSnapshot(
    decimal TotalBalance,
    decimal GrantedBalance,
    decimal ToppedUpBalance,
    bool IsAvailable,
    DateTimeOffset Timestamp,
    string SourceFile,
    string BalanceEndpoint);

internal sealed record DeepSeekBalanceReadResult(DeepSeekBalanceSnapshot? Snapshot, string? ErrorMessage);

internal sealed class DeepSeekBalanceReader
{
    private const string DefaultBaseUrl = "https://api.deepseek.com";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".deepcode",
        "settings.json");

    public DeepSeekBalanceReadResult ReadLatestSnapshot()
    {
        try
        {
            DeepCodeConfiguration configuration = ReadDeepCodeConfiguration();
            string endpoint = BuildBalanceEndpoint(configuration.BaseUrl);

            using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                configuration.ApiKey);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("DeepCode/limits");

            using HttpResponseMessage response = HttpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                string message = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "DeepCode API key was rejected by DeepSeek.",
                    System.Net.HttpStatusCode.PaymentRequired => "DeepSeek reports insufficient balance.",
                    System.Net.HttpStatusCode.TooManyRequests => "DeepSeek balance endpoint rate limited the request.",
                    _ => $"DeepSeek balance request failed: HTTP {(int)response.StatusCode}"
                };
                throw new InvalidOperationException(message);
            }

            string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            DeepSeekBalanceSnapshot snapshot = ParseBalanceResponse(
                responseBody,
                configuration.Source,
                endpoint);
            return new DeepSeekBalanceReadResult(snapshot, null);
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            TaskCanceledException or
            IOException or
            JsonException or
            InvalidOperationException or
            UnauthorizedAccessException)
        {
            return new DeepSeekBalanceReadResult(null, exception.Message);
        }
    }

    private DeepCodeConfiguration ReadDeepCodeConfiguration()
    {
        string? apiKey = null;
        string? baseUrl = null;

        if (File.Exists(SettingsPath))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("env", out JsonElement env) && env.ValueKind == JsonValueKind.Object)
            {
                apiKey = ReadNonEmptyString(env, "API_KEY");
                baseUrl = ReadNonEmptyString(env, "BASE_URL");
            }
        }

        string? environmentApiKey = Environment.GetEnvironmentVariable("DEEPCODE_API_KEY");
        string? environmentBaseUrl = Environment.GetEnvironmentVariable("DEEPCODE_BASE_URL");
        apiKey = string.IsNullOrWhiteSpace(environmentApiKey) ? apiKey : environmentApiKey.Trim();
        baseUrl = string.IsNullOrWhiteSpace(environmentBaseUrl) ? baseUrl : environmentBaseUrl.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"DeepCode API key was not found in {SettingsPath} or DEEPCODE_API_KEY.");
        }

        string source = string.IsNullOrWhiteSpace(environmentApiKey)
            ? SettingsPath
            : "DEEPCODE_API_KEY";
        return new DeepCodeConfiguration(apiKey, baseUrl ?? DefaultBaseUrl, source);
    }

    private static string BuildBalanceEndpoint(string configuredBaseUrl)
    {
        string baseUrl = configuredBaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/anthropic".Length];
        }
        else if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/v1".Length];
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("DeepCode BASE_URL is not a valid HTTP URL.");
        }

        return $"{baseUrl}/user/balance";
    }

    private static DeepSeekBalanceSnapshot ParseBalanceResponse(
        string responseBody,
        string source,
        string endpoint)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        bool isAvailable = root.TryGetProperty("is_available", out JsonElement availableElement) &&
                           availableElement.ValueKind == JsonValueKind.True;

        if (!root.TryGetProperty("balance_infos", out JsonElement balances) ||
            balances.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("DeepSeek balance response did not include balance_infos.");
        }

        foreach (JsonElement balance in balances.EnumerateArray())
        {
            if (!string.Equals(ReadNonEmptyString(balance, "currency"), "USD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new DeepSeekBalanceSnapshot(
                ReadDecimal(balance, "total_balance"),
                ReadDecimal(balance, "granted_balance"),
                ReadDecimal(balance, "topped_up_balance"),
                isAvailable,
                DateTimeOffset.UtcNow,
                source,
                endpoint);
        }

        throw new InvalidOperationException("DeepSeek balance response did not include a USD balance.");
    }

    private static decimal ReadDecimal(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element))
        {
            throw new InvalidOperationException($"DeepSeek balance response did not include {propertyName}.");
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out decimal numericValue))
        {
            return numericValue;
        }

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                element.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal stringValue))
        {
            return stringValue;
        }

        throw new InvalidOperationException($"DeepSeek balance response has an invalid {propertyName}.");
    }

    private static string? ReadNonEmptyString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record DeepCodeConfiguration(string ApiKey, string BaseUrl, string Source);
}

internal sealed record OpenCodeUsageSnapshot(
    long SessionCount,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal Cost,
    DateTimeOffset Timestamp,
    DateTimeOffset WindowStart,
    DateTimeOffset? LatestActivityAt,
    string SourceFile)
{
    public long TotalTokens => InputTokens + OutputTokens + ReasoningTokens + CacheReadTokens + CacheWriteTokens;
}

internal static class OpenCodeGoLimits
{
    public const decimal RollingUsd = 12m;
    public const decimal WeeklyUsd = 30m;
}

internal sealed record OpenCodeGoUsageWindow(
    string Status,
    double UsedPercent,
    DateTimeOffset? ResetAt);

internal sealed record OpenCodeGoUsageSnapshot(
    DateTimeOffset Timestamp,
    OpenCodeGoUsageWindow Rolling,
    OpenCodeGoUsageWindow Weekly,
    OpenCodeGoUsageWindow Monthly,
    string Source);

internal sealed record OpenCodeGoUsageReadResult(
    OpenCodeGoUsageSnapshot? Snapshot,
    string? ErrorMessage);

internal sealed record OpenCodeUsageReadResult(
    OpenCodeUsageSnapshot? Snapshot,
    OpenCodeGoUsageSnapshot? GoUsage,
    string? GoUsageError,
    string? ErrorMessage);

internal sealed record UnetAccountBalance(
    string Username,
    decimal? Balance,
    string Currency,
    bool IsAvailable,
    string? Error);

internal sealed record UnetBalanceSnapshot(
    IReadOnlyList<UnetAccountBalance> Accounts,
    DateTimeOffset Timestamp);

internal sealed record UnetBalanceReadResult(UnetBalanceSnapshot? Snapshot, string? ErrorMessage);

internal sealed record UnetCredential(string Username, string Password);

internal sealed class UnetCredentialStore
{
    public string CredentialsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "limits",
        "unet-credentials.json");

    public IReadOnlyList<UnetCredential> Load()
    {
        if (!File.Exists(CredentialsPath))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("accounts", out JsonElement accountsElement) ||
                accountsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<UnetCredential> credentials = [];
            foreach (JsonElement accountElement in accountsElement.EnumerateArray())
            {
                string? username = ReadString(accountElement, "username");
                string? protectedPassword = ReadString(accountElement, "passwordProtected");
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(protectedPassword))
                {
                    continue;
                }

                byte[] encryptedPassword = Convert.FromBase64String(protectedPassword);
                try
                {
                    byte[] clearPassword = ProtectedData.Unprotect(
                        encryptedPassword,
                        optionalEntropy: null,
                        DataProtectionScope.CurrentUser);
                    try
                    {
                        string password = Encoding.UTF8.GetString(clearPassword);
                        if (!string.IsNullOrWhiteSpace(password))
                        {
                            credentials.Add(new UnetCredential(username.Trim(), password));
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(clearPassword);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encryptedPassword);
                }
            }

            return credentials;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            FormatException or
            CryptographicException)
        {
            throw new InvalidOperationException("UNET credential storage could not be read.", exception);
        }
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}

internal sealed class UnetBalanceReader
{
    private static readonly Uri LoginUri = new("https://my.unet.by/login");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const string Currency = "BYN";
    private readonly UnetCredentialStore _credentialStore = new();

    public string CredentialsPath => _credentialStore.CredentialsPath;

    public UnetBalanceReadResult ReadLatestSnapshot()
    {
        try
        {
            IReadOnlyList<UnetCredential> credentials = _credentialStore.Load();
            if (credentials.Count == 0)
            {
                throw new InvalidOperationException($"No UNET credentials were found at {CredentialsPath}.");
            }

            List<UnetAccountBalance> accounts = [];
            foreach (UnetCredential credential in credentials)
            {
                try
                {
                    decimal balance = ReadAccountBalance(credential);
                    accounts.Add(new UnetAccountBalance(credential.Username, balance, Currency, true, null));
                }
                catch (Exception exception) when (IsAccountReadFailure(exception))
                {
                    accounts.Add(new UnetAccountBalance(
                        credential.Username,
                        null,
                        Currency,
                        false,
                        GetSafeErrorMessage(exception)));
                }
            }

            return new UnetBalanceReadResult(
                new UnetBalanceSnapshot(accounts, DateTimeOffset.UtcNow),
                null);
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            TaskCanceledException or
            IOException or
            JsonException or
            InvalidOperationException or
            UnauthorizedAccessException or
            FormatException or
            CryptographicException)
        {
            return new UnetBalanceReadResult(null, GetSafeErrorMessage(exception));
        }
    }

    private static decimal ReadAccountBalance(UnetCredential credential)
    {
        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        using HttpClient client = new(handler)
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("limits/1.3");

        using HttpRequestMessage pageRequest = new(HttpMethod.Get, LoginUri);
        using HttpResponseMessage pageResponse = client.Send(pageRequest);
        if (!pageResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"UNET login page returned HTTP {(int)pageResponse.StatusCode}.");
        }

        string loginHtml = pageResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        string csrfToken = ReadInputValue(loginHtml, "_csrf_token")
            ?? throw new InvalidOperationException("UNET login form did not include a CSRF token.");

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["username"] = credential.Username,
            ["password"] = credential.Password,
            ["_csrf_token"] = csrfToken
        });
        using HttpRequestMessage loginRequest = new(HttpMethod.Post, LoginUri)
        {
            Content = form
        };
        loginRequest.Headers.Referrer = LoginUri;

        using HttpResponseMessage accountResponse = client.Send(loginRequest);
        if (!accountResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"UNET login returned HTTP {(int)accountResponse.StatusCode}.");
        }

        string accountHtml = accountResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ParseBalance(accountHtml);
    }

    private static decimal ParseBalance(string html)
    {
        int balanceLabelIndex = html.IndexOf("Текущий баланс", StringComparison.OrdinalIgnoreCase);
        if (balanceLabelIndex < 0)
        {
            throw new InvalidOperationException("UNET login was not accepted or the balance row changed.");
        }

        int rowEnd = html.IndexOf("</tr>", balanceLabelIndex, StringComparison.OrdinalIgnoreCase);
        int spanStart = html.IndexOf("<span", balanceLabelIndex, StringComparison.OrdinalIgnoreCase);
        if (rowEnd < 0 || spanStart < 0 || spanStart >= rowEnd)
        {
            throw new InvalidOperationException("UNET balance value was not found.");
        }

        int spanEnd = html.IndexOf('>', spanStart);
        if (spanEnd < 0 || spanEnd >= rowEnd)
        {
            throw new InvalidOperationException("UNET balance value was not found.");
        }

        string? balanceText = ReadAttribute(html[spanStart..(spanEnd + 1)], "title");
        if (decimal.TryParse(balanceText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal balance))
        {
            return balance;
        }

        throw new InvalidOperationException("UNET balance value was invalid.");
    }

    private static string? ReadInputValue(string html, string inputName)
    {
        int searchIndex = 0;
        while (searchIndex < html.Length)
        {
            int inputStart = html.IndexOf("<input", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (inputStart < 0)
            {
                return null;
            }

            int inputEnd = html.IndexOf('>', inputStart);
            if (inputEnd < 0)
            {
                return null;
            }

            string inputTag = html[inputStart..(inputEnd + 1)];
            if (string.Equals(ReadAttribute(inputTag, "name"), inputName, StringComparison.OrdinalIgnoreCase))
            {
                return ReadAttribute(inputTag, "value");
            }

            searchIndex = inputEnd + 1;
        }

        return null;
    }

    private static string? ReadAttribute(string tag, string attributeName)
    {
        int searchIndex = 0;
        while (searchIndex < tag.Length)
        {
            int attributeStart = tag.IndexOf(attributeName, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (attributeStart < 0)
            {
                return null;
            }

            int attributeEnd = attributeStart + attributeName.Length;
            bool validStart = attributeStart == 0 || !IsAttributeNameCharacter(tag[attributeStart - 1]);
            bool validEnd = attributeEnd >= tag.Length || !IsAttributeNameCharacter(tag[attributeEnd]);
            if (!validStart || !validEnd)
            {
                searchIndex = attributeEnd;
                continue;
            }

            int equalsIndex = attributeEnd;
            while (equalsIndex < tag.Length && char.IsWhiteSpace(tag[equalsIndex]))
            {
                equalsIndex++;
            }

            if (equalsIndex >= tag.Length || tag[equalsIndex] != '=')
            {
                searchIndex = attributeEnd;
                continue;
            }

            int valueStart = equalsIndex + 1;
            while (valueStart < tag.Length && char.IsWhiteSpace(tag[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= tag.Length)
            {
                return null;
            }

            char quote = tag[valueStart];
            if (quote is '"' or '\'')
            {
                int valueEnd = tag.IndexOf(quote, valueStart + 1);
                return valueEnd < 0
                    ? null
                    : WebUtility.HtmlDecode(tag[(valueStart + 1)..valueEnd]);
            }

            int unquotedEnd = valueStart;
            while (unquotedEnd < tag.Length && !char.IsWhiteSpace(tag[unquotedEnd]) && tag[unquotedEnd] != '>')
            {
                unquotedEnd++;
            }

            return WebUtility.HtmlDecode(tag[valueStart..unquotedEnd]);
        }

        return null;
    }

    private static bool IsAttributeNameCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value is '_' or '-' or ':';
    }

    private static bool IsAccountReadFailure(Exception exception)
    {
        return exception is
            HttpRequestException or
            TaskCanceledException or
            IOException or
            InvalidOperationException or
            FormatException;
    }

    private static string GetSafeErrorMessage(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => "UNET request timed out.",
            HttpRequestException => "UNET HTTP request failed.",
            _ => "UNET login or balance parsing failed."
        };
    }
}

internal sealed record DevinUsageSnapshot(
    DateTimeOffset Timestamp,
    string? PlanName,
    double? DailyRemainingPercent,
    double? WeeklyRemainingPercent,
    DateTimeOffset? DailyResetAt,
    DateTimeOffset? WeeklyResetAt,
    DateTimeOffset? PlanEndAt,
    string Source);

internal sealed record DevinUsageReadResult(DevinUsageSnapshot? Snapshot, string? ErrorMessage);

internal sealed class DevinUsageReader
{
    private const string DefaultApiBaseUrl = "https://server.codeium.com";
    private const string GetUserStatusPath = "/exa.seat_management_pb.SeatManagementService/GetUserStatus";
    private const string DevinCredentialsFileName = "credentials.toml";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public string CredentialsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Devin",
        DevinCredentialsFileName);

    public string UsageEndpoint => $"{DefaultApiBaseUrl}{GetUserStatusPath}";

    public DevinUsageReadResult ReadLatestSnapshot()
    {
        try
        {
            DevinCredential credential = ReadCredential();
            string endpoint = new Uri(
                new Uri(credential.ApiBaseUrl, UriKind.Absolute),
                GetUserStatusPath).ToString();

            using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(BuildGetUserStatusRequest(credential.Token))
            };
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/proto");
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"Basic {credential.Token}-{credential.Token}");

            using HttpResponseMessage response = HttpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                return new DevinUsageReadResult(
                    null,
                    $"Devin quota request failed: HTTP {(int)response.StatusCode}");
            }

            byte[] responseBody = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            DevinUsageSnapshot? snapshot = ParseUserStatus(responseBody, endpoint);
            return snapshot is null
                ? new DevinUsageReadResult(null, "Devin quota response could not be parsed.")
                : new DevinUsageReadResult(snapshot, null);
        }
        catch (TaskCanceledException)
        {
            return new DevinUsageReadResult(null, "Devin quota request timed out.");
        }
        catch (HttpRequestException)
        {
            return new DevinUsageReadResult(null, "Devin quota request failed.");
        }
        catch (UnauthorizedAccessException)
        {
            return new DevinUsageReadResult(null, "Devin credentials could not be read.");
        }
        catch (IOException)
        {
            return new DevinUsageReadResult(null, "Devin credentials could not be read.");
        }
        catch (InvalidOperationException exception)
        {
            return new DevinUsageReadResult(null, exception.Message);
        }
        catch (FormatException)
        {
            return new DevinUsageReadResult(null, "Devin quota response was invalid.");
        }
        catch (Exception)
        {
            return new DevinUsageReadResult(null, "Devin quota is unavailable.");
        }
    }

    private DevinCredential ReadCredential()
    {
        if (!File.Exists(CredentialsPath))
        {
            throw new InvalidOperationException($"Devin credentials were not found at {CredentialsPath}.");
        }

        string contents = File.ReadAllText(CredentialsPath);
        string token = ReadTomlString(contents, "windsurf_api_key")
            ?? throw new InvalidOperationException("Devin API key was not found in the local credentials file.");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Devin API key was empty in the local credentials file.");
        }

        string apiBaseUrl = ReadTomlString(contents, "api_server_url") ?? DefaultApiBaseUrl;
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out Uri? apiBaseUri) ||
            (!string.Equals(apiBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(apiBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Devin API server URL was invalid.");
        }

        return new DevinCredential(token, apiBaseUri.ToString().TrimEnd('/'));
    }

    private static string? ReadTomlString(string contents, string key)
    {
        foreach (string line in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0 ||
                !string.Equals(line[..equalsIndex].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line[(equalsIndex + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                return value[1..^1]
                    .Replace("\\\\", "\\", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal);
            }

            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                return value[1..^1];
            }

            return value;
        }

        return null;
    }

    private static byte[] BuildGetUserStatusRequest(string token)
    {
        List<byte> metadata = [];
        AppendStringField(metadata, 1, "chisel");
        AppendStringField(metadata, 2, "0.0.0-dev");
        AppendStringField(metadata, 3, token);
        AppendStringField(metadata, 4, "en");
        AppendStringField(metadata, 5, "windows");
        AppendStringField(metadata, 7, "0.0.0-dev");

        List<byte> body = [10];
        AppendVarint(body, (ulong)metadata.Count);
        body.AddRange(metadata);
        return body.ToArray();
    }

    private static void AppendStringField(List<byte> target, int fieldNumber, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        target.Add((byte)((fieldNumber << 3) | 2));
        AppendVarint(target, (ulong)bytes.Length);
        target.AddRange(bytes);
    }

    private static void AppendVarint(List<byte> target, ulong value)
    {
        while (value > 0x7F)
        {
            target.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        target.Add((byte)value);
    }

    private static DevinUsageSnapshot? ParseUserStatus(byte[] response, string source)
    {
        List<ProtoField>? responseFields = DecodeFields(response);
        byte[]? userStatusBytes = GetBytes(responseFields, 1);
        List<ProtoField>? userStatusFields = userStatusBytes is null
            ? null
            : DecodeFields(userStatusBytes);
        byte[]? planStatusBytes = GetBytes(userStatusFields, 13);
        List<ProtoField>? planStatusFields = planStatusBytes is null
            ? null
            : DecodeFields(planStatusBytes);
        if (planStatusFields is null)
        {
            return null;
        }

        string? planName = null;
        byte[]? planInfoBytes = GetBytes(planStatusFields, 1);
        if (planInfoBytes is not null)
        {
            planName = GetString(DecodeFields(planInfoBytes), 2);
        }

        return new DevinUsageSnapshot(
            DateTimeOffset.UtcNow,
            planName,
            GetPercent(planStatusFields, 14),
            GetPercent(planStatusFields, 15),
            GetResetAt(planStatusFields, 17),
            GetResetAt(planStatusFields, 18),
            GetTimestampAt(planStatusFields, 3),
            source);
    }

    private static DateTimeOffset? GetTimestampAt(List<ProtoField>? fields, int fieldNumber)
    {
        byte[]? timestampBytes = GetBytes(fields, fieldNumber);
        return timestampBytes is null
            ? null
            : GetResetAt(DecodeFields(timestampBytes), 1);
    }

    private static double? GetPercent(List<ProtoField>? fields, int fieldNumber)
    {
        ulong? value = GetVarint(fields, fieldNumber);
        return value is null ? null : Math.Clamp((double)value.Value, 0d, 100d);
    }

    private static DateTimeOffset? GetResetAt(List<ProtoField>? fields, int fieldNumber)
    {
        ulong? value = GetVarint(fields, fieldNumber);
        if (value is null || value.Value > long.MaxValue || value.Value == 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[]? GetBytes(List<ProtoField>? fields, int fieldNumber)
    {
        return fields?.FirstOrDefault(field => field.Number == fieldNumber && field.Bytes is not null)?.Bytes;
    }

    private static ulong? GetVarint(List<ProtoField>? fields, int fieldNumber)
    {
        return fields?.FirstOrDefault(field => field.Number == fieldNumber && field.Varint is not null)?.Varint;
    }

    private static string? GetString(List<ProtoField>? fields, int fieldNumber)
    {
        byte[]? bytes = GetBytes(fields, fieldNumber);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static List<ProtoField>? DecodeFields(byte[] buffer)
    {
        List<ProtoField> fields = [];
        int offset = 0;
        while (offset < buffer.Length)
        {
            if (!TryReadVarint(buffer, ref offset, out ulong tag) || tag >> 3 == 0)
            {
                return null;
            }

            int fieldNumber = checked((int)(tag >> 3));
            int wireType = (int)(tag & 7);
            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(buffer, ref offset, out ulong varint))
                    {
                        return null;
                    }

                    fields.Add(new ProtoField(fieldNumber, varint, null));
                    break;

                case 1:
                    if (buffer.Length - offset < sizeof(long))
                    {
                        return null;
                    }

                    offset += sizeof(long);
                    break;

                case 2:
                    if (!TryReadVarint(buffer, ref offset, out ulong byteLength) ||
                        byteLength > int.MaxValue ||
                        byteLength > (ulong)(buffer.Length - offset))
                    {
                        return null;
                    }

                    int length = (int)byteLength;
                    byte[] bytes = new byte[length];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, length);
                    offset += length;
                    fields.Add(new ProtoField(fieldNumber, null, bytes));
                    break;

                case 5:
                    if (buffer.Length - offset < sizeof(int))
                    {
                        return null;
                    }

                    offset += sizeof(int);
                    break;

                default:
                    return null;
            }
        }

        return fields;
    }

    private static bool TryReadVarint(byte[] buffer, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= buffer.Length)
            {
                return false;
            }

            byte current = buffer[offset++];
            if (shift == 63 && (current & 0xFE) != 0)
            {
                return false;
            }

            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record DevinCredential(string Token, string ApiBaseUrl);

    private sealed record ProtoField(int Number, ulong? Varint, byte[]? Bytes);
}

internal sealed record OpenRouterBalanceSnapshot(
    DateTimeOffset Timestamp,
    decimal? TotalCredits,
    decimal? TotalUsage,
    string? KeyLabel,
    decimal? KeyLimit,
    decimal? KeyLimitRemaining,
    string? KeyLimitReset,
    decimal? UsageDaily,
    decimal? UsageWeekly,
    decimal? UsageMonthly,
    bool IsFreeTier,
    string KeySource,
    string Source)
{
    public decimal? AccountBalance =>
        TotalCredits is { } credits && TotalUsage is { } usage ? credits - usage : null;

    // What can actually be spent with this key: the lower of the account balance and the key's own limit.
    public decimal? SpendableBalance =>
        (AccountBalance, KeyLimitRemaining) switch
        {
            ({ } balance, { } keyRemaining) => Math.Min(balance, keyRemaining),
            ({ } balance, null) => balance,
            (null, { } keyRemaining) => keyRemaining,
            _ => null
        };
}

internal sealed record OpenRouterBalanceReadResult(OpenRouterBalanceSnapshot? Snapshot, string? ErrorMessage);

internal sealed class OpenRouterBalanceReader
{
    private const string ApiBaseUrl = "https://openrouter.ai/api/v1";
    private const string KeyEndpoint = ApiBaseUrl + "/key";
    private const string CreditsEndpoint = ApiBaseUrl + "/credits";
    private const string ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string AuthPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local",
        "share",
        "opencode",
        "auth.json");

    public string UsageEndpoint => KeyEndpoint;

    public OpenRouterBalanceReadResult ReadLatestSnapshot()
    {
        try
        {
            (string apiKey, string keySource) = ReadApiKey();

            using JsonDocument keyDocument = SendGet(KeyEndpoint, apiKey, required: true)!;
            JsonElement keyData = GetData(keyDocument, "key");

            decimal? totalCredits = null;
            decimal? totalUsage = null;
            using JsonDocument? creditsDocument = SendGet(CreditsEndpoint, apiKey, required: false);
            if (creditsDocument is not null)
            {
                JsonElement creditsData = GetData(creditsDocument, "credits");
                totalCredits = ReadNullableDecimal(creditsData, "total_credits");
                totalUsage = ReadNullableDecimal(creditsData, "total_usage");
            }

            OpenRouterBalanceSnapshot snapshot = new(
                DateTimeOffset.UtcNow,
                totalCredits,
                totalUsage,
                ReadNonEmptyString(keyData, "label"),
                ReadNullableDecimal(keyData, "limit"),
                ReadNullableDecimal(keyData, "limit_remaining"),
                ReadNonEmptyString(keyData, "limit_reset"),
                ReadNullableDecimal(keyData, "usage_daily"),
                ReadNullableDecimal(keyData, "usage_weekly"),
                ReadNullableDecimal(keyData, "usage_monthly"),
                keyData.TryGetProperty("is_free_tier", out JsonElement freeTier) &&
                freeTier.ValueKind == JsonValueKind.True,
                keySource,
                creditsDocument is null ? KeyEndpoint : $"{KeyEndpoint} + {CreditsEndpoint}");

            if (snapshot.SpendableBalance is null)
            {
                return new OpenRouterBalanceReadResult(
                    null,
                    "OpenRouter credits are unavailable and the key has no spending limit.");
            }

            return new OpenRouterBalanceReadResult(snapshot, null);
        }
        catch (TaskCanceledException)
        {
            return new OpenRouterBalanceReadResult(null, "OpenRouter balance request timed out.");
        }
        catch (HttpRequestException)
        {
            return new OpenRouterBalanceReadResult(null, "OpenRouter balance request failed.");
        }
        catch (JsonException)
        {
            return new OpenRouterBalanceReadResult(null, "OpenRouter balance response was invalid.");
        }
        catch (InvalidOperationException exception)
        {
            return new OpenRouterBalanceReadResult(null, exception.Message);
        }
        catch (IOException)
        {
            return new OpenRouterBalanceReadResult(null, "OpenRouter credentials could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return new OpenRouterBalanceReadResult(null, "OpenRouter credentials could not be read.");
        }
        catch (Exception)
        {
            return new OpenRouterBalanceReadResult(null, "OpenRouter balance is unavailable.");
        }
    }

    private (string ApiKey, string Source) ReadApiKey()
    {
        if (File.Exists(AuthPath))
        {
            try
            {
                using FileStream stream = new(
                    AuthPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("openrouter", out JsonElement provider) &&
                    provider.ValueKind == JsonValueKind.Object &&
                    ReadNonEmptyString(provider, "key") is { } fileKey)
                {
                    return (fileKey, AuthPath);
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                JsonException)
            {
                // Fall through to `opencode auth export` below.
            }
        }

        // OpenCode v2 keeps credentials in its service credential store
        // instead of <data>/auth.json.
        string? exportedKey = OpenCodeUsageReader.ReadExportedApiKey("openrouter");
        if (!string.IsNullOrWhiteSpace(exportedKey))
        {
            return (exportedKey, "opencode auth export");
        }

        string? environmentKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            return (environmentKey.Trim(), ApiKeyEnvironmentVariable);
        }

        throw new InvalidOperationException(
            $"OpenRouter API key was not found in {AuthPath} or {ApiKeyEnvironmentVariable}.");
    }

    private static JsonDocument? SendGet(string endpoint, string apiKey, bool required)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "limits/1.5.1");

        using HttpResponseMessage response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            if (!required)
            {
                return null;
            }

            string message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "OpenRouter API key was rejected.",
                HttpStatusCode.TooManyRequests => "OpenRouter rate limited the balance request.",
                _ => $"OpenRouter balance request failed: HTTP {(int)response.StatusCode}"
            };
            throw new InvalidOperationException(message);
        }

        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonDocument.Parse(body);
    }

    private static JsonElement GetData(JsonDocument document, string endpointName)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"OpenRouter {endpointName} response did not include data.");
        }

        return data;
    }

    private static decimal? ReadNullableDecimal(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out decimal numericValue))
        {
            return numericValue;
        }

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                element.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static string? ReadNonEmptyString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal sealed class OpenCodeUsageReader
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient GoHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private const string GoUsageEndpoint = "https://opencode.ai/zen/go/v1/usage";

    public string DataDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local",
        "share",
        "opencode");

    public string DatabasePath => Path.Combine(DataDirectoryPath, "opencode.db");
    public string AuthPath => Path.Combine(DataDirectoryPath, "auth.json");

    public OpenCodeUsageReadResult ReadLatestSnapshot()
    {
        OpenCodeGoUsageReadResult goResult = ReadGoUsage();
        try
        {
            if (!File.Exists(DatabasePath))
            {
                throw new InvalidOperationException($"OpenCode database was not found at {DatabasePath}.");
            }

            string executablePath = ResolveOpenCodeExecutable()
                ?? throw new InvalidOperationException("The OpenCode executable was not found on PATH.");

            // OpenCode v2 removed the `opencode db` subcommand and renamed the
            // `session` table to `session_v2`, so read today's totals through
            // the supported `opencode stats --json --days 1` command instead.
            string response = RunStatsQuery(executablePath);
            OpenCodeUsageSnapshot snapshot = ParseSnapshot(response);
            return new OpenCodeUsageReadResult(snapshot, goResult.Snapshot, goResult.ErrorMessage, null);
        }
        catch (Exception exception)
        {
            return new OpenCodeUsageReadResult(
                null,
                goResult.Snapshot,
                goResult.ErrorMessage,
                exception.Message);
        }
    }

    private OpenCodeGoUsageReadResult ReadGoUsage()
    {
        try
        {
            string apiKey = ReadGoApiKey();
            using HttpRequestMessage request = new(HttpMethod.Get, GoUsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "limits/1.5.1");

            using HttpResponseMessage response = GoHttpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"OpenCode Go usage request failed with HTTP {(int)response.StatusCode}.");
            }

            string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new OpenCodeGoUsageReadResult(ParseGoUsageResponse(responseBody), null);
        }
        catch (TaskCanceledException)
        {
            return new OpenCodeGoUsageReadResult(null, "OpenCode Go quota request timed out.");
        }
        catch (HttpRequestException)
        {
            return new OpenCodeGoUsageReadResult(null, "OpenCode Go quota request failed.");
        }
        catch (JsonException)
        {
            return new OpenCodeGoUsageReadResult(null, "OpenCode Go quota response was invalid.");
        }
        catch (InvalidOperationException exception)
        {
            return new OpenCodeGoUsageReadResult(null, exception.Message);
        }
        catch (IOException)
        {
            return new OpenCodeGoUsageReadResult(null, "OpenCode Go credentials could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return new OpenCodeGoUsageReadResult(null, "OpenCode Go credentials could not be read.");
        }
        catch (FormatException)
        {
            return new OpenCodeGoUsageReadResult(null, "OpenCode Go quota response was invalid.");
        }
        catch (Exception)
        {
            return new OpenCodeGoUsageReadResult(null, "OpenCode Go quota is unavailable.");
        }
    }

    private string ReadGoApiKey()
    {
        // OpenCode v2 no longer keeps credentials in <data>/auth.json; they
        // live in the service credential store. Keep the legacy file as a
        // fallback, then read the key via `opencode auth export`.
        if (File.Exists(AuthPath))
        {
            try
            {
                using FileStream stream = new(
                    AuthPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("opencode-go", out JsonElement provider) &&
                    provider.ValueKind == JsonValueKind.Object &&
                    provider.TryGetProperty("key", out JsonElement keyElement) &&
                    keyElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(keyElement.GetString()))
                {
                    return keyElement.GetString()!;
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                JsonException)
            {
                // Fall through to `opencode auth export` below.
            }
        }

        string? exportedKey = ReadExportedApiKey("opencode-go");
        if (!string.IsNullOrWhiteSpace(exportedKey))
        {
            return exportedKey;
        }

        throw new InvalidOperationException("OpenCode Go API key was not found.");
    }

    internal static string? ReadExportedApiKey(string integrationId)
    {
        string? executablePath = ResolveOpenCodeExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string output;
        try
        {
            output = RunOpenCodeCommand(executablePath, ["auth", "export"]);
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("integrationID", out JsonElement idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(idElement.GetString(), integrationId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.TryGetProperty("value", out JsonElement valueElement) &&
                    valueElement.ValueKind == JsonValueKind.Object &&
                    valueElement.TryGetProperty("key", out JsonElement keyElement) &&
                    keyElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(keyElement.GetString()))
                {
                    return keyElement.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static OpenCodeGoUsageSnapshot ParseGoUsageResponse(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("OpenCode Go quota response did not include usage.");
        }

        return new OpenCodeGoUsageSnapshot(
            DateTimeOffset.UtcNow,
            ParseGoUsageWindow(usage, "rolling"),
            ParseGoUsageWindow(usage, "weekly"),
            ParseGoUsageWindow(usage, "monthly"),
            GoUsageEndpoint);
    }

    private static OpenCodeGoUsageWindow ParseGoUsageWindow(JsonElement usage, string windowName)
    {
        if (!usage.TryGetProperty(windowName, out JsonElement window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("percent", out JsonElement percentElement) ||
            !percentElement.TryGetDouble(out double usedPercent) ||
            !double.IsFinite(usedPercent))
        {
            throw new InvalidOperationException($"OpenCode Go quota did not include {windowName} usage.");
        }

        string status = window.TryGetProperty("status", out JsonElement statusElement) &&
                        statusElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(statusElement.GetString())
            ? statusElement.GetString()!
            : "unknown";
        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resetsAt", out JsonElement resetAtElement) &&
            resetAtElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                resetAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsedResetAt))
        {
            resetAt = parsedResetAt;
        }

        return new OpenCodeGoUsageWindow(
            status,
            Math.Clamp(usedPercent, 0d, 100d),
            resetAt);
    }

    internal static string? ResolveOpenCodeExecutable()
    {
        List<string> candidates = [];

        string? configuredPath = Environment.GetEnvironmentVariable("OPENCODE_BIN");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath.Trim().Trim('"'));
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = pathEntry.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                candidates.Add(Path.Combine(directory, "opencode.exe"));
                // npm global installs place a shim (opencode.cmd/ps1) on PATH
                // while the real binary lives under @opencode/cli.
                candidates.Add(Path.Combine(directory, "node_modules", "@opencode", "cli", "bin", "opencode.exe"));
                candidates.Add(Path.Combine(directory, "node_modules", "opencode-ai", "bin", "opencode.exe"));
            }
        }

        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.Combine(appData, "npm", "node_modules", "@opencode", "cli", "bin", "opencode.exe"));
            candidates.Add(Path.Combine(appData, "npm", "node_modules", "opencode-ai", "bin", "opencode.exe"));
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "nodejs", "node_modules", "@opencode", "cli", "bin", "opencode.exe"));
            candidates.Add(Path.Combine(programFiles, "nodejs", "node_modules", "opencode-ai", "bin", "opencode.exe"));
        }

        candidates.Add(Path.Combine(
            "C:\\Programs",
            "nodejs",
            "node_modules",
            "@opencode",
            "cli",
            "bin",
            "opencode.exe"));
        candidates.Add(Path.Combine(
            "C:\\Programs",
            "nodejs",
            "node_modules",
            "opencode-ai",
            "bin",
            "opencode.exe"));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string RunStatsQuery(string executablePath)
    {
        // `stats --json --days 1` reports today's totals; without flags it
        // reports year-to-date. Prefer today's window, retrying with
        // --standalone when the background service is unreachable.
        try
        {
            return RunOpenCodeCommand(executablePath, ["stats", "--json", "--days", "1"]);
        }
        catch (InvalidOperationException exception) when (!exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return RunOpenCodeCommand(executablePath, ["stats", "--json", "--days", "1", "--standalone"]);
        }
    }

    private static string RunOpenCodeCommand(string executablePath, string[] arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("OpenCode command could not be started.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)QueryTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException("OpenCode command timed out.");
        }

        string output = outputTask.GetAwaiter().GetResult().Trim();
        string error = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"OpenCode command failed with exit code {process.ExitCode}."
                    : error);
        }

        if (output.Length == 0)
        {
            throw new InvalidOperationException("OpenCode command returned no data.");
        }

        return output;
    }

    private static OpenCodeUsageSnapshot ParseSnapshot(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("OpenCode stats returned an invalid result.");
        }

        DateTimeOffset windowStart = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        if (root.TryGetProperty("range", out JsonElement range) &&
            range.ValueKind == JsonValueKind.Object)
        {
            if (ReadOptionalInt64(range, "from") is { } fromMilliseconds)
            {
                try
                {
                    windowStart = DateTimeOffset.FromUnixTimeMilliseconds(fromMilliseconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            if (ReadOptionalInt64(range, "to") is { } toMilliseconds)
            {
                try
                {
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(toMilliseconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
        }

        long sessionCount = ReadOptionalInt64(root, "sessions") ?? 0;
        long inputTokens = 0;
        long outputTokens = 0;
        long reasoningTokens = 0;
        long cacheReadTokens = 0;
        long cacheWriteTokens = 0;
        if (root.TryGetProperty("tokens", out JsonElement tokens) &&
            tokens.ValueKind == JsonValueKind.Object)
        {
            inputTokens = ReadOptionalInt64(tokens, "input") ?? 0;
            outputTokens = ReadOptionalInt64(tokens, "output") ?? 0;
            reasoningTokens = ReadOptionalInt64(tokens, "reasoning") ?? 0;
            if (tokens.TryGetProperty("cache", out JsonElement cache) &&
                cache.ValueKind == JsonValueKind.Object)
            {
                cacheReadTokens = ReadOptionalInt64(cache, "read") ?? 0;
                cacheWriteTokens = ReadOptionalInt64(cache, "write") ?? 0;
            }
        }

        decimal cost = ReadOptionalDecimal(root, "cost") ?? 0m;

        return new OpenCodeUsageSnapshot(
            sessionCount,
            inputTokens,
            outputTokens,
            reasoningTokens,
            cacheReadTokens,
            cacheWriteTokens,
            cost,
            timestamp,
            windowStart,
            null,
            Path.Combine(DataDirectoryPathForSource(), "opencode.db"));
    }

    private static long? ReadOptionalInt64(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null ||
            element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt64(out long integerValue))
            {
                return integerValue;
            }

            if (element.TryGetDouble(out double doubleValue) && double.IsFinite(doubleValue))
            {
                return (long)Math.Max(long.MinValue, Math.Min(long.MaxValue, doubleValue));
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static decimal? ReadOptionalDecimal(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null ||
            element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetDecimal(out decimal decimalValue))
            {
                return decimalValue;
            }

            if (element.TryGetDouble(out double doubleValue) && double.IsFinite(doubleValue))
            {
                return (decimal)doubleValue;
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static string DataDirectoryPathForSource()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "opencode");
    }
}

internal sealed record HardwareSnapshot(
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    double? CpuUsagePercent,
    double? GpuUsagePercent,
    double? RamUsagePercent,
    double? VramUsagePercent,
    long? RamTotalBytes,
    long? RamAvailableBytes,
    long? VramTotalBytes,
    long? VramAvailableBytes,
    string? GpuName,
    DateTimeOffset Timestamp,
    string SourceDescription);

internal sealed record HardwareReadResult(
    HardwareSnapshot? Snapshot,
    string? ErrorMessage,
    IReadOnlyList<RamProcessUsage>? TopRamProcesses = null);

internal sealed record RamProcessUsage(int ProcessId, string Name, long WorkingSetBytes);

internal static class RamProcessReader
{
    public static IReadOnlyList<RamProcessUsage> ReadTopProcesses(int maxCount = 5)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return [];
        }

        List<RamProcessUsage> usage = [];
        foreach (Process process in processes)
        {
            try
            {
                long workingSetBytes = process.WorkingSet64;
                if (workingSetBytes > 0)
                {
                    usage.Add(new RamProcessUsage(process.Id, process.ProcessName, workingSetBytes));
                }
            }
            catch (InvalidOperationException)
            {
                // The process may exit while its memory is being read.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Windows can deny access to protected processes.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip processes whose details are not readable by this user.
            }
            finally
            {
                process.Dispose();
            }
        }

        return usage
            .OrderByDescending(process => process.WorkingSetBytes)
            .Take(Math.Max(0, maxCount))
            .ToArray();
    }
}

internal sealed class HardwareMonitor
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private readonly AmdPackageTemperatureReader _amdPackageTemperatureReader = new();
    private CpuTimeSample? _lastCpuTimeSample;

    public string SourceDescription =>
        "GetSystemTimes + GlobalMemoryStatusEx + AMD Ryzen Master package sensor + nvidia-smi";

    public HardwareReadResult ReadSnapshot()
    {
        try
        {
            NvidiaSnapshot gpu = ReadNvidiaSnapshot();
            PhysicalMemorySnapshot ram = ReadPhysicalMemorySnapshot();
            return new HardwareReadResult(
                new HardwareSnapshot(
                    CpuTemperatureC: ReadCpuTemperature(),
                    GpuTemperatureC: gpu.TemperatureC,
                    CpuUsagePercent: ReadCpuUsagePercent(),
                    GpuUsagePercent: gpu.UtilizationPercent,
                    RamUsagePercent: ram.UsagePercent,
                    VramUsagePercent: GetUsedPercent(gpu.TotalBytes, gpu.AvailableBytes),
                    RamTotalBytes: ram.TotalBytes,
                    RamAvailableBytes: ram.AvailableBytes,
                    VramTotalBytes: gpu.TotalBytes,
                    VramAvailableBytes: gpu.AvailableBytes,
                    GpuName: gpu.Name,
                    Timestamp: DateTimeOffset.UtcNow,
                    SourceDescription: SourceDescription),
                null);
        }
        catch (Exception exception)
        {
            return new HardwareReadResult(null, exception.Message);
        }
    }

    public void Dispose()
    {
        _amdPackageTemperatureReader.Dispose();
    }

    private double? ReadCpuTemperature()
    {
        return _amdPackageTemperatureReader.ReadTemperature();
    }

    private double? ReadCpuUsagePercent()
    {
        if (!NativeMethods.GetSystemTimes(
                out NativeMethods.SYSTEM_FILETIME idleTime,
                out NativeMethods.SYSTEM_FILETIME kernelTime,
                out NativeMethods.SYSTEM_FILETIME userTime))
        {
            return null;
        }

        CpuTimeSample current = new(
            idleTime.ToInt64(),
            kernelTime.ToInt64(),
            userTime.ToInt64());
        CpuTimeSample? previous = _lastCpuTimeSample;
        _lastCpuTimeSample = current;
        if (previous is null)
        {
            return null;
        }

        long idleDelta = current.IdleTicks - previous.Value.IdleTicks;
        long kernelDelta = current.KernelTicks - previous.Value.KernelTicks;
        long userDelta = current.UserTicks - previous.Value.UserTicks;
        long totalDelta = kernelDelta + userDelta;
        if (idleDelta < 0 || totalDelta <= 0)
        {
            return null;
        }

        long busyDelta = Math.Max(0, totalDelta - idleDelta);
        return Math.Clamp(busyDelta * 100d / totalDelta, 0d, 100d);
    }

    private static PhysicalMemorySnapshot ReadPhysicalMemorySnapshot()
    {
        NativeMethods.MEMORYSTATUSEX status = new()
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhysicalBytes == 0)
        {
            return PhysicalMemorySnapshot.Empty;
        }

        long? totalBytes = ToInt64(status.TotalPhysicalBytes);
        long? availableBytes = ToInt64(status.AvailablePhysicalBytes);
        if (totalBytes is not { } total || total <= 0)
        {
            return PhysicalMemorySnapshot.Empty;
        }

        if (availableBytes is { } available)
        {
            availableBytes = Math.Clamp(available, 0, total);
        }

        return new PhysicalMemorySnapshot(
            GetUsedPercent(totalBytes, availableBytes),
            totalBytes,
            availableBytes);
    }

    private static double? GetUsedPercent(long? totalBytes, long? availableBytes)
    {
        if (totalBytes is not { } total || total <= 0 ||
            availableBytes is not { } available)
        {
            return null;
        }

        long clampedAvailable = Math.Clamp(available, 0, total);
        return Math.Clamp((total - clampedAvailable) * 100d / total, 0d, 100d);
    }

    private static long? ToInt64(ulong value)
    {
        return value <= long.MaxValue ? (long)value : null;
    }

    private static NvidiaSnapshot ReadNvidiaSnapshot()
    {
        string? nvidiaSmiPath = ResolveToolPath("nvidia-smi.exe");
        if (nvidiaSmiPath is null)
        {
            return NvidiaSnapshot.Empty;
        }

        try
        {
            string output = RunProcess(
                nvidiaSmiPath,
                [
                    "--query-gpu=name,temperature.gpu,utilization.gpu,memory.total,memory.free",
                    "--format=csv,noheader,nounits"
                ]);
            List<NvidiaGpu> gpus = [];
            foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split(',');
                if (fields.Length < 5)
                {
                    continue;
                }

                int temperatureIndex = fields.Length - 4;
                double? gpuTemperature = TryParseDouble(fields[temperatureIndex]);
                double? gpuUtilization = TryParseDouble(fields[temperatureIndex + 1]);
                long? totalBytes = TryParseMebibytes(fields[temperatureIndex + 2]);
                long? availableBytes = TryParseMebibytes(fields[temperatureIndex + 3]);
                if (gpuTemperature is null && gpuUtilization is null &&
                    totalBytes is null && availableBytes is null)
                {
                    continue;
                }

                string gpuName = string.Join(",", fields[..temperatureIndex]).Trim();
                gpus.Add(new NvidiaGpu(
                    gpuName,
                    gpuTemperature,
                    gpuUtilization,
                    totalBytes,
                    availableBytes));
            }

            if (gpus.Count == 0)
            {
                return NvidiaSnapshot.Empty;
            }

            long? total = SumNullable(gpus.Select(gpu => gpu.TotalBytes));
            long? available = SumNullable(gpus.Select(gpu => gpu.AvailableBytes));
            double? maxTemperature = gpus
                .Where(gpu => gpu.TemperatureC is not null)
                .Select(gpu => gpu.TemperatureC!.Value)
                .Select(value => (double?)value)
                .DefaultIfEmpty()
                .Max();
            double? maxUtilization = gpus
                .Where(gpu => gpu.UtilizationPercent is not null)
                .Select(gpu => gpu.UtilizationPercent!.Value)
                .Select(value => (double?)value)
                .DefaultIfEmpty()
                .Max();
            string gpuDisplayName = gpus.Count == 1
                ? gpus[0].Name
                : $"NVIDIA GPUs ({gpus.Count})";
            return new NvidiaSnapshot(
                string.IsNullOrWhiteSpace(gpuDisplayName) ? "NVIDIA GPU" : gpuDisplayName,
                maxTemperature,
                maxUtilization,
                total,
                available);
        }
        catch (Exception)
        {
            return NvidiaSnapshot.Empty;
        }
    }

    private static string? ResolveToolPath(string fileName)
    {
        List<string> candidates = [];
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            candidates.Add(Path.Combine(systemRoot, "System32", fileName));
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = pathEntry.Trim().Trim('"');
                if (directory.Length > 0)
                {
                    candidates.Add(Path.Combine(directory, fileName));
                }
            }
        }

        candidates.Add(Path.Combine("C:\\Windows", "System32", fileName));
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException($"{Path.GetFileName(fileName)} timed out.");
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}."
                    : error);
        }

        return output;
    }

    private static double? TryParseDouble(string value)
    {
        return double.TryParse(
            value.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double result)
            ? result
            : null;
    }

    private static long? TryParseMebibytes(string value)
    {
        double? mebibytes = TryParseDouble(value);
        if (mebibytes is null || !double.IsFinite(mebibytes.Value) || mebibytes < 0)
        {
            return null;
        }

        double bytes = mebibytes.Value * 1024d * 1024d;
        return bytes > long.MaxValue ? null : (long)Math.Round(bytes);
    }

    private static long? SumNullable(IEnumerable<long?> values)
    {
        long total = 0;
        bool hasValue = false;
        foreach (long? value in values)
        {
            if (value is null)
            {
                continue;
            }

            hasValue = true;
            total = checked(total + value.Value);
        }

        return hasValue ? total : null;
    }

    private sealed record NvidiaGpu(
        string Name,
        double? TemperatureC,
        double? UtilizationPercent,
        long? TotalBytes,
        long? AvailableBytes);

    private sealed record NvidiaSnapshot(
        string? Name,
        double? TemperatureC,
        double? UtilizationPercent,
        long? TotalBytes,
        long? AvailableBytes)
    {
        public static NvidiaSnapshot Empty => new(null, null, null, null, null);
    }

    private sealed record PhysicalMemorySnapshot(
        double? UsagePercent,
        long? TotalBytes,
        long? AvailableBytes)
    {
        public static PhysicalMemorySnapshot Empty => new(null, null, null);
    }

    private readonly record struct CpuTimeSample(
        long IdleTicks,
        long KernelTicks,
        long UserTicks);
}

internal sealed class AmdPackageTemperatureReader : IDisposable
{
    private const int ParameterBufferSize = 0x400;
    private const int TemperatureOffset = 0x78;
    private readonly object _sync = new();
    private IntPtr _module;
    private IntPtr _platform;
    private AmdGetRmCpuParametersDelegate? _getRmCpuParameters;
    private bool _initializationAttempted;
    private bool _initialized;

    public double? ReadTemperature()
    {
        lock (_sync)
        {
            try
            {
                if (!EnsureInitialized())
                {
                    return null;
                }

                IntPtr buffer = Marshal.AllocHGlobal(ParameterBufferSize);
                try
                {
                    byte[] zeroes = new byte[ParameterBufferSize];
                    Marshal.Copy(zeroes, 0, buffer, zeroes.Length);
                    if (_getRmCpuParameters!(buffer) != 0)
                    {
                        return null;
                    }

                    byte[] result = new byte[ParameterBufferSize];
                    Marshal.Copy(buffer, result, 0, result.Length);
                    double temperature = BitConverter.ToDouble(result, TemperatureOffset);
                    return double.IsFinite(temperature) && temperature > 1d && temperature <= 150d
                        ? temperature
                        : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_module != IntPtr.Zero)
            {
                NativeLibrary.Free(_module);
                _module = IntPtr.Zero;
            }

            _platform = IntPtr.Zero;
            _getRmCpuParameters = null;
            _initialized = false;
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        if (_initializationAttempted)
        {
            return false;
        }

        _initializationAttempted = true;
        foreach (string path in GetPlatformPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                _module = NativeLibrary.Load(path);
                IntPtr getPlatformAddress = NativeLibrary.GetExport(_module, "GetPlatform");
                IntPtr getParametersAddress = NativeLibrary.GetExport(_module, "GetRmCpuParameters");
                AmdGetPlatformDelegate getPlatform =
                    Marshal.GetDelegateForFunctionPointer<AmdGetPlatformDelegate>(getPlatformAddress);
                _getRmCpuParameters =
                    Marshal.GetDelegateForFunctionPointer<AmdGetRmCpuParametersDelegate>(getParametersAddress);
                _platform = getPlatform();
                if (_platform == IntPtr.Zero)
                {
                    throw new InvalidOperationException("AMD platform initialization returned no platform.");
                }

                IntPtr vtable = Marshal.ReadIntPtr(_platform);
                if (vtable == IntPtr.Zero)
                {
                    throw new InvalidOperationException("AMD platform vtable was unavailable.");
                }

                AmdPlatformInitDelegate init = Marshal.GetDelegateForFunctionPointer<AmdPlatformInitDelegate>(
                    Marshal.ReadIntPtr(vtable, IntPtr.Size));
                if (init(_platform) == 0)
                {
                    throw new InvalidOperationException("AMD platform initialization failed.");
                }

                _initialized = true;
                return true;
            }
            catch
            {
                if (_module != IntPtr.Zero)
                {
                    NativeLibrary.Free(_module);
                    _module = IntPtr.Zero;
                }

                _platform = IntPtr.Zero;
                _getRmCpuParameters = null;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetPlatformPaths()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string[] programFiles =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];

        foreach (string root in programFiles)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                paths.Add(Path.Combine(root, "AMD", "RyzenMaster", "bin", "Platform.dll"));
            }
        }

        foreach (string path in paths)
        {
            yield return path;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AmdGetPlatformDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AmdGetRmCpuParametersDelegate(IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte AmdPlatformInitDelegate(IntPtr self);
}

internal sealed record TrayIconSettings(
    bool Codex,
    bool Claude,
    bool Kimi,
    bool DeepSeek,
    bool Disk,
    bool OpenCode,
    bool Temperature,
    bool CpuGpuLoad,
    bool Devin,
    bool OpenRouter)
{
    public static TrayIconSettings Default => new(
        Codex: false,
        Claude: false,
        Kimi: false,
        DeepSeek: false,
        Disk: true,
        OpenCode: true,
        Temperature: false,
        CpuGpuLoad: true,
        Devin: true,
        OpenRouter: true);

    public bool IsVisible(TrayIconKind iconKind)
    {
        return iconKind switch
        {
            TrayIconKind.Claude => Claude,
            TrayIconKind.Kimi => Kimi,
            TrayIconKind.DeepSeek => DeepSeek,
            TrayIconKind.Disk => Disk,
            TrayIconKind.OpenCode => OpenCode,
            TrayIconKind.Temperature => Temperature,
            TrayIconKind.CpuGpuLoad => true,
            TrayIconKind.Devin => Devin,
            TrayIconKind.OpenRouter => OpenRouter,
            _ => Codex
        };
    }

    public TrayIconSettings Toggle(TrayIconKind iconKind)
    {
        return iconKind switch
        {
            TrayIconKind.Claude => this with { Claude = !Claude },
            TrayIconKind.Kimi => this with { Kimi = !Kimi },
            TrayIconKind.DeepSeek => this with { DeepSeek = !DeepSeek },
            TrayIconKind.Disk => this with { Disk = !Disk },
            TrayIconKind.OpenCode => this with { OpenCode = !OpenCode },
            TrayIconKind.Temperature => this with { Temperature = !Temperature },
            TrayIconKind.CpuGpuLoad => this with { CpuGpuLoad = true },
            TrayIconKind.Devin => this with { Devin = !Devin },
            TrayIconKind.OpenRouter => this with { OpenRouter = !OpenRouter },
            _ => this with { Codex = !Codex }
        };
    }
}

internal sealed class TrayIconSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "limits",
        "icon-settings.json");

    public TrayIconSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return TrayIconSettings.Default;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            JsonElement root = document.RootElement;
            TrayIconSettings defaults = TrayIconSettings.Default;
            return new TrayIconSettings(
                Codex: ReadBoolean(root, "codex", defaults.Codex),
                Claude: ReadBoolean(root, "claude", defaults.Claude),
                Kimi: ReadBoolean(root, "kimi", defaults.Kimi),
                DeepSeek: ReadBoolean(root, "deepSeek", defaults.DeepSeek),
                Disk: ReadBoolean(root, "disk", defaults.Disk),
                OpenCode: ReadBoolean(root, "openCode", defaults.OpenCode),
                Temperature: ReadBoolean(root, "temperature", defaults.Temperature),
                CpuGpuLoad: true,
                Devin: ReadBoolean(root, "devin", defaults.Devin),
                OpenRouter: ReadBoolean(root, "openRouter", defaults.OpenRouter));
        }
        catch (Exception)
        {
            return TrayIconSettings.Default;
        }
    }

    public void Save(TrayIconSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        JsonObject root = new()
        {
            ["codex"] = settings.Codex,
            ["claude"] = settings.Claude,
            ["kimi"] = settings.Kimi,
            ["deepSeek"] = settings.DeepSeek,
            ["disk"] = settings.Disk,
            ["openCode"] = settings.OpenCode,
            ["temperature"] = settings.Temperature,
            ["cpuGpuLoad"] = true,
            ["devin"] = settings.Devin,
            ["openRouter"] = settings.OpenRouter
        };
        File.WriteAllText(SettingsPath, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static bool ReadBoolean(JsonElement root, string propertyName, bool fallback)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) &&
               (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;
    }
}

internal sealed record DiskDriveSetting(string DriveLetter, double RedLimitGb)
{
    public long RedLimitBytes => DiskMonitor.GigabytesToBytes(RedLimitGb);
}

internal sealed record DiskMonitorSettings(IReadOnlyList<DiskDriveSetting> Drives)
{
    public static DiskMonitorSettings Default => new(
    [
        new DiskDriveSetting("C", 5),
        new DiskDriveSetting("D", 5)
    ]);

    public static DiskMonitorSettings Normalize(IEnumerable<DiskDriveSetting> drives)
    {
        Dictionary<string, DiskDriveSetting> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (DiskDriveSetting drive in drives)
        {
            string driveLetter = drive.DriveLetter.Trim().TrimEnd(':').ToUpperInvariant();
            if (driveLetter.Length != 1 || driveLetter[0] is < 'A' or > 'Z')
            {
                continue;
            }

            double redLimitGb = double.IsFinite(drive.RedLimitGb)
                ? Math.Clamp(drive.RedLimitGb, 0, 1_000_000)
                : 5;
            normalized[driveLetter] = new DiskDriveSetting(driveLetter, redLimitGb);
        }

        return new DiskMonitorSettings(normalized.Values.OrderBy(drive => drive.DriveLetter).ToArray());
    }
}

internal sealed class DiskMonitorSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "limits",
        "settings.json");

    public DiskMonitorSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return DiskMonitorSettings.Default;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("drives", out JsonElement drives) ||
                drives.ValueKind != JsonValueKind.Array)
            {
                return DiskMonitorSettings.Default;
            }

            List<DiskDriveSetting> settings = [];
            foreach (JsonElement drive in drives.EnumerateArray())
            {
                if (drive.ValueKind != JsonValueKind.Object ||
                    !drive.TryGetProperty("drive", out JsonElement driveElement) ||
                    driveElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? driveLetter = driveElement.GetString();
                if (string.IsNullOrWhiteSpace(driveLetter))
                {
                    continue;
                }

                double? redLimitGb = null;
                if (drive.TryGetProperty("redLimitGb", out JsonElement limitElement))
                {
                    if (limitElement.ValueKind == JsonValueKind.Number && limitElement.TryGetDouble(out double numericLimit))
                    {
                        redLimitGb = numericLimit;
                    }
                    else if (limitElement.ValueKind == JsonValueKind.String &&
                             double.TryParse(
                                 limitElement.GetString(),
                                 NumberStyles.Float,
                                 CultureInfo.InvariantCulture,
                                 out double stringLimit))
                    {
                        redLimitGb = stringLimit;
                    }
                }

                settings.Add(new DiskDriveSetting(driveLetter.Trim(), redLimitGb ?? 5));
            }

            return DiskMonitorSettings.Normalize(settings);
        }
        catch (Exception)
        {
            return DiskMonitorSettings.Default;
        }
    }

    public void Save(DiskMonitorSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        JsonArray drives = new();
        foreach (DiskDriveSetting drive in settings.Drives)
        {
            drives.Add((JsonNode)new JsonObject
            {
                ["drive"] = drive.DriveLetter,
                ["redLimitGb"] = drive.RedLimitGb
            });
        }

        JsonObject root = new() { ["drives"] = drives };
        File.WriteAllText(SettingsPath, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }
}

internal sealed class DiskMonitor
{
    private readonly DiskMonitorSettingsStore _settingsStore = new();
    private DiskMonitorSettings _settings;

    public DiskMonitor()
    {
        _settings = _settingsStore.Load();
    }

    public string SettingsPath => _settingsStore.SettingsPath;

    public DiskMonitorSettings Settings => Volatile.Read(ref _settings);

    public void SaveSettings(DiskMonitorSettings settings)
    {
        DiskMonitorSettings normalized = DiskMonitorSettings.Normalize(settings.Drives);
        _settingsStore.Save(normalized);
        Volatile.Write(ref _settings, normalized);
    }

    public DiskSpaceSnapshot ReadSnapshot()
    {
        DiskMonitorSettings settings = Settings;
        List<DiskSpaceStatus> drives = [];
        foreach (DiskDriveSetting configuredDrive in settings.Drives)
        {
            try
            {
                DriveInfo drive = new($@"{configuredDrive.DriveLetter}:\");
                if (!drive.IsReady)
                {
                    drives.Add(new DiskSpaceStatus(configuredDrive.DriveLetter, null, configuredDrive.RedLimitGb, "not ready"));
                    continue;
                }

                drives.Add(new DiskSpaceStatus(
                    configuredDrive.DriveLetter,
                    drive.AvailableFreeSpace,
                    configuredDrive.RedLimitGb,
                    null));
            }
            catch (Exception exception)
            {
                drives.Add(new DiskSpaceStatus(
                    configuredDrive.DriveLetter,
                    null,
                    configuredDrive.RedLimitGb,
                    exception.Message));
            }
        }

        return new DiskSpaceSnapshot(drives, DateTimeOffset.UtcNow);
    }

    public DiskSpaceSnapshot CreateErrorSnapshot(string error)
    {
        DiskMonitorSettings settings = Settings;
        return new DiskSpaceSnapshot(
            settings.Drives.Select(drive =>
                new DiskSpaceStatus(drive.DriveLetter, null, drive.RedLimitGb, error)).ToArray(),
            DateTimeOffset.UtcNow);
    }

    public static long GigabytesToBytes(double gigabytes)
    {
        double bytes = Math.Max(0, gigabytes) * 1024d * 1024d * 1024d;
        return bytes >= long.MaxValue ? long.MaxValue : (long)Math.Round(bytes);
    }

    public static string FormatBytes(long bytes)
    {
        double gib = bytes / 1024d / 1024d / 1024d;
        return $"{gib:0.##} GB";
    }

    public static string FormatGigabytes(double gigabytes)
    {
        return $"{gigabytes:0.##} GB";
    }
}

internal sealed record DiskSpaceStatus(
    string DriveLetter,
    long? FreeBytes,
    double RedLimitGb,
    string? Error)
{
    public long RedLimitBytes => DiskMonitor.GigabytesToBytes(RedLimitGb);
    public bool IsUnavailable => FreeBytes is null;
    public bool IsLow => FreeBytes is { } freeBytes && freeBytes <= RedLimitBytes;
    public bool IsLimitFailure => IsUnavailable || IsLow;
}

internal sealed record DiskSpaceSnapshot(IReadOnlyList<DiskSpaceStatus> Drives, DateTimeOffset CheckedAt)
{
    public bool HasSelectedDrives => Drives.Count > 0;
    public bool HasLowDisk => Drives.Any(drive => drive.IsLimitFailure);
    public bool HasUnavailableDrives => Drives.Any(drive => drive.IsUnavailable);
    public bool IsHealthy => HasSelectedDrives && !HasUnavailableDrives && !HasLowDisk;
    public string LowDriveLetters => string.Concat(Drives.Where(drive => drive.IsLow).Select(drive => drive.DriveLetter));
    public string UnavailableDriveLetters => string.Concat(Drives.Where(drive => drive.IsUnavailable).Select(drive => drive.DriveLetter));
    public string IconText => BuildIconText();

    public string Summary => !HasSelectedDrives
        ? "no disks selected"
        : string.Join(", ", Drives.Select(drive => drive.FreeBytes is null
            ? $"{drive.DriveLetter}: unavailable ({drive.Error ?? "unknown"})"
            : $"{drive.DriveLetter}: {DiskMonitor.FormatBytes(drive.FreeBytes.Value)} free"));

    public string LimitSummary => !HasSelectedDrives
        ? "none"
        : string.Join(", ", Drives.Select(drive =>
            $"{drive.DriveLetter} <= {DiskMonitor.FormatGigabytes(drive.RedLimitGb)}"));

    private string BuildIconText()
    {
        if (!HasSelectedDrives)
        {
            return "?";
        }

        if (!string.IsNullOrWhiteSpace(LowDriveLetters))
        {
            return FormatLetters(LowDriveLetters);
        }

        if (HasUnavailableDrives)
        {
            return "?";
        }

        return FormatLetters(string.Concat(Drives.Select(drive => drive.DriveLetter)));
    }

    private static string FormatLetters(string letters)
    {
        if (letters.Length <= 3)
        {
            return letters;
        }

        return $"{letters[..2]}+";
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

internal sealed class CounterWebSocketServer : IDisposable
{
    private const int Port = 31001;
    private const string WebSocketPath = "/ws";
    private const string WebSocketUrl = "ws://127.0.0.1:31001/ws";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _sync = new();
    private readonly List<ClientConnection> _clients = [];
    private Task? _acceptTask;
    private string _latestJson = "{}";
    private bool _started;
    private bool _disposed;

    public void Start(string initialJson)
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CounterWebSocketServer));
            }

            _latestJson = initialJson;
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _started = true;
            _acceptTask = Task.Run(AcceptLoopAsync);
        }
    }

    public void Publish(string json)
    {
        ClientConnection[] clients;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _latestJson = json;
            clients = _clients.ToArray();
        }

        if (clients.Length > 0)
        {
            _ = BroadcastAsync(json, clients);
        }
    }

    public void Dispose()
    {
        ClientConnection[] clients;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            clients = _clients.ToArray();
            _clients.Clear();
        }

        _cancellation.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (ClientConnection client in clients)
        {
            client.Abort();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleContextAsync(context);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        if (context.Request.IsWebSocketRequest &&
            string.Equals(context.Request.Url?.AbsolutePath, WebSocketPath, StringComparison.Ordinal))
        {
            await HandleWebSocketAsync(context);
            return;
        }

        if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(context.Request.Url?.AbsolutePath, "/", StringComparison.Ordinal))
        {
            await WriteJsonResponseAsync(
                context,
                200,
                "{\"websocket\":\"" + WebSocketUrl + "\",\"path\":\"/ws\"}");
            return;
        }

        await WriteJsonResponseAsync(context, 404, "{\"error\":\"not found\"}");
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        ClientConnection? client = null;
        try
        {
            WebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null);
            client = new ClientConnection(webSocketContext.WebSocket);
            string initialJson;
            lock (_sync)
            {
                if (_disposed)
                {
                    client.Abort();
                    return;
                }

                _clients.Add(client);
                initialJson = _latestJson;
            }

            await SendAsync(client, initialJson);
            await ReceiveUntilClosedAsync(client);
        }
        catch (HttpListenerException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (client is not null)
            {
                RemoveClient(client);
                client.Abort();
            }
            else
            {
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                }
            }
        }
    }

    private async Task ReceiveUntilClosedAsync(ClientConnection client)
    {
        byte[] buffer = new byte[1024];
        while (!_cancellation.IsCancellationRequested &&
               client.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            WebSocketReceiveResult result = await client.Socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                _cancellation.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private async Task BroadcastAsync(string json, IReadOnlyList<ClientConnection> clients)
    {
        foreach (ClientConnection client in clients)
        {
            try
            {
                await SendAsync(client, json);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException)
            {
                RemoveClient(client);
                client.Abort();
            }
            catch (ObjectDisposedException)
            {
                RemoveClient(client);
            }
        }
    }

    private async Task SendAsync(ClientConnection client, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        await client.SendLock.WaitAsync(_cancellation.Token);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                await client.Socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    _cancellation.Token);
            }
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private void RemoveClient(ClientConnection client)
    {
        lock (_sync)
        {
            _clients.Remove(client);
        }
    }

    private static async Task WriteJsonResponseAsync(
        HttpListenerContext context,
        int statusCode,
        string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = payload.Length;
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        await context.Response.OutputStream.WriteAsync(payload);
        context.Response.Close();
    }

    private sealed class ClientConnection
    {
        public ClientConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public void Abort()
        {
            try
            {
                Socket.Abort();
            }
            catch
            {
            }

            Socket.Dispose();
            SendLock.Dispose();
        }
    }
}

internal static class TrayIconRenderer
{
    private const int IconSize = 16;
    private const int GlyphWidth = 4;
    private const int GlyphHeight = 7;
    private const int GlyphSpacing = 1;

    public const string CodexUnavailableIconKey = "codex:?:?";
    public const string ClaudeUnavailableIconKey = "claude:?:?";
    public const string KimiUnavailableIconKey = "kimi:?";
    public const string DeepSeekUnavailableIconKey = "deepseek:?";
    public const string DiskUnavailableIconKey = "disk:?";
    public const string OpenCodeUnavailableIconKey = "opencode:?";
    public const string DevinUnavailableIconKey = "devin:?";
    public const string OpenRouterUnavailableIconKey = "openrouter:?";
    public const string TemperatureUnavailableIconKey = "temperature:?";
    public const string MemoryUnavailableIconKey = "memory:?";

    // Brand marker colors are fixed, not theme-dependent.
    private const uint OpenAiBrandColor = 0xFF10A37F;
    private const uint ClaudeBrandColor = 0xFFD97757;
    private const uint KimiBrandColor = 0xFF23B7F0;
    private const uint DeepSeekBrandColor = 0xFF4D6BFE;
    private const uint DiskBrandColor = 0xFF4A90E2;
    private const uint OpenCodeBrandColor = 0xFF7B61FF;
    private const uint DevinBrandColor = 0xFF6E5AF7;
    private const uint OpenRouterBrandColor = 0xFF94A3B8;
    private const uint TemperatureBrandColor = 0xFFE76F51;
    private const uint MemoryBrandColor = 0xFF5B8DEF;
    private const uint CpuBarColor = 0xFF39A96B;
    private const uint GpuBarColor = 0xFF9B6BFF;
    private const uint RamBarColor = 0xFF2CA9E1;
    private const uint VramBarColor = 0xFFFFA940;

    private static readonly IconPalette LightThemePalette = new(
        UnknownColor: 0xFF444444,
        DangerColor: 0xFF9D2B22,
        WarningColor: 0xFF8C5D00,
        SafeColor: 0xFF1E6F36);
    private static readonly IconPalette DarkThemePalette = new(
        UnknownColor: 0xFFD8D8D8,
        DangerColor: 0xFFFF6C62,
        WarningColor: 0xFFF1C84A,
        SafeColor: 0xFF72E089);

    private static readonly IReadOnlyDictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
    {
        ['0'] = ["0110", "1001", "1001", "1001", "1001", "1001", "0110"],
        ['1'] = ["0010", "0110", "0010", "0010", "0010", "0010", "0111"],
        ['2'] = ["0110", "1001", "0001", "0010", "0100", "1000", "1111"],
        ['3'] = ["1110", "0001", "0001", "0110", "0001", "0001", "1110"],
        ['4'] = ["1001", "1001", "1001", "1111", "0001", "0001", "0001"],
        ['5'] = ["1111", "1000", "1000", "1110", "0001", "0001", "1110"],
        ['6'] = ["0111", "1000", "1000", "1110", "1001", "1001", "0110"],
        ['7'] = ["1111", "0001", "0001", "0010", "0010", "0100", "0100"],
        ['8'] = ["0110", "1001", "1001", "0110", "1001", "1001", "0110"],
        ['9'] = ["0110", "1001", "1001", "0111", "0001", "0001", "1110"],
        ['?'] = ["1110", "0001", "0010", "0010", "0000", "0010", "0000"],
        ['A'] = ["0110", "1001", "1001", "1111", "1001", "1001", "1001"],
        ['B'] = ["1110", "1001", "1001", "1110", "1001", "1001", "1110"],
        ['C'] = ["0111", "1000", "1000", "1000", "1000", "1000", "0111"],
        ['D'] = ["1110", "1001", "1001", "1001", "1001", "1001", "1110"],
        ['E'] = ["1111", "1000", "1000", "1110", "1000", "1000", "1111"],
        ['F'] = ["1111", "1000", "1000", "1110", "1000", "1000", "1000"],
        ['G'] = ["0111", "1000", "1000", "1011", "1001", "1001", "0111"],
        ['H'] = ["1001", "1001", "1001", "1111", "1001", "1001", "1001"],
        ['I'] = ["1111", "0010", "0010", "0010", "0010", "0010", "1111"],
        ['J'] = ["0011", "0001", "0001", "0001", "0001", "1001", "0110"],
        ['K'] = ["1001", "1010", "1100", "1100", "1010", "1001", "1001"],
        ['L'] = ["1000", "1000", "1000", "1000", "1000", "1000", "1111"],
        ['M'] = ["1001", "1111", "1111", "1001", "1001", "1001", "1001"],
        ['N'] = ["1001", "1101", "1101", "1011", "1011", "1001", "1001"],
        ['O'] = ["0110", "1001", "1001", "1001", "1001", "1001", "0110"],
        ['P'] = ["1110", "1001", "1001", "1110", "1000", "1000", "1000"],
        ['Q'] = ["0110", "1001", "1001", "1001", "1011", "0110", "0001"],
        ['R'] = ["1110", "1001", "1001", "1110", "1010", "1001", "1001"],
        ['S'] = ["0111", "1000", "1000", "0110", "0001", "0001", "1110"],
        ['T'] = ["1111", "0010", "0010", "0010", "0010", "0010", "0010"],
        ['U'] = ["1001", "1001", "1001", "1001", "1001", "1001", "0110"],
        ['V'] = ["1001", "1001", "1001", "1001", "1001", "0110", "0110"],
        ['W'] = ["1001", "1001", "1001", "1111", "1111", "1111", "1001"],
        ['X'] = ["1001", "1001", "0110", "0110", "0110", "1001", "1001"],
        ['Y'] = ["1001", "1001", "0110", "0010", "0010", "0010", "0010"],
        ['Z'] = ["1111", "0001", "0010", "0100", "1000", "1000", "1111"],
        ['+'] = ["0010", "0010", "1111", "0010", "0010", "0000", "0000"]
    };

    public static IntPtr CreateUsageIcon(CodexUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int weeklyRemaining = CodexUsageMath.GetWeeklyRemainingPercent(snapshot);

        return CreateCenteredIcon(
            weeklyRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(weeklyRemaining, palette),
            OpenAiBrandColor,
            CodexUsageMath.GetWeeklyResetAt(snapshot));
    }

    public static string GetCodexIconKey(CodexUsageSnapshot snapshot)
    {
        int weeklyRemaining = CodexUsageMath.GetWeeklyRemainingPercent(snapshot);
        int resetDays = GetResetDayDotCount(CodexUsageMath.GetWeeklyResetAt(snapshot));
        return $"codex:{weeklyRemaining}:{resetDays}";
    }

    public static IntPtr CreateUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateCenteredIcon("?", palette.UnknownColor, OpenAiBrandColor);
    }

    public static IntPtr CreateClaudeIcon(ClaudeUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);

        return CreateIcon(
            fiveHourRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(fiveHourRemaining, palette),
            sevenDayRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(sevenDayRemaining, palette),
            ClaudeBrandColor,
            snapshot.SevenDayResetAt);
    }

    public static string GetClaudeIconKey(ClaudeUsageSnapshot snapshot)
    {
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);
        int resetDays = GetResetDayDotCount(snapshot.SevenDayResetAt);
        return $"claude:{fiveHourRemaining}:{sevenDayRemaining}:{resetDays}";
    }

    public static IntPtr CreateClaudeUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon("?", palette.UnknownColor, "?", palette.UnknownColor, ClaudeBrandColor);
    }

    public static IntPtr CreateKimiIcon(KimiUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int fiveHourRemaining = KimiUsageMath.GetRemainingPercent(snapshot.FiveHourRemainingPercent);
        int sevenDayRemaining = KimiUsageMath.GetRemainingPercent(snapshot.SevenDayRemainingPercent);

        return CreateIcon(
            fiveHourRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(fiveHourRemaining, palette),
            sevenDayRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(sevenDayRemaining, palette),
            KimiBrandColor,
            snapshot.SevenDayResetAt);
    }

    public static string GetKimiIconKey(KimiUsageSnapshot snapshot)
    {
        int fiveHourRemaining = KimiUsageMath.GetRemainingPercent(snapshot.FiveHourRemainingPercent);
        int sevenDayRemaining = KimiUsageMath.GetRemainingPercent(snapshot.SevenDayRemainingPercent);
        int resetDays = GetResetDayDotCount(snapshot.SevenDayResetAt);
        return $"kimi:{fiveHourRemaining}:{sevenDayRemaining}:{resetDays}";
    }

    public static IntPtr CreateKimiUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon("?", palette.UnknownColor, "?", palette.UnknownColor, KimiBrandColor);
    }

    public static IntPtr CreateDeepSeekIcon(DeepSeekBalanceSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        uint balanceColor = ColorForBalance(snapshot.TotalBalance, snapshot.IsAvailable, palette);
        string text = FormatBalanceIconText(snapshot.TotalBalance);

        if (text == "infinity")
        {
            uint[] infinityPixels = new uint[IconSize * IconSize];
            DrawBrandTriangle(infinityPixels, DeepSeekBrandColor);
            DrawInfinity(infinityPixels, balanceColor);
            return CreateNativeIcon(infinityPixels);
        }

        return CreateCenteredIcon(text, balanceColor, DeepSeekBrandColor);
    }

    public static string GetDeepSeekIconKey(DeepSeekBalanceSnapshot snapshot)
    {
        return $"deepseek:{FormatBalanceIconText(snapshot.TotalBalance)}:{snapshot.IsAvailable}";
    }

    public static IntPtr CreateDeepSeekUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateCenteredIcon("?", palette.UnknownColor, DeepSeekBrandColor);
    }

    public static IntPtr CreateOpenRouterIcon(OpenRouterBalanceSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        if (snapshot.SpendableBalance is not { } balance)
        {
            return CreateOpenRouterUnavailableIcon();
        }

        uint balanceColor = ColorForBalance(balance, isAvailable: balance > 0, palette);
        string text = FormatBalanceIconText(balance);

        if (text == "infinity")
        {
            uint[] infinityPixels = new uint[IconSize * IconSize];
            DrawBrandTriangle(infinityPixels, OpenRouterBrandColor);
            DrawInfinity(infinityPixels, balanceColor);
            return CreateNativeIcon(infinityPixels);
        }

        return CreateCenteredIcon(text, balanceColor, OpenRouterBrandColor);
    }

    public static string GetOpenRouterIconKey(OpenRouterBalanceSnapshot snapshot)
    {
        return snapshot.SpendableBalance is { } balance
            ? $"openrouter:{FormatBalanceIconText(balance)}:{balance > 0}:{ColorForBalance(balance, balance > 0, GetPalette())}"
            : OpenRouterUnavailableIconKey;
    }

    public static IntPtr CreateOpenRouterUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateCenteredIcon("?", palette.UnknownColor, OpenRouterBrandColor);
    }

    public static IntPtr CreateDiskIcon(DiskSpaceSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        uint color = !snapshot.HasSelectedDrives
            ? palette.UnknownColor
            : !string.IsNullOrWhiteSpace(snapshot.LowDriveLetters)
                ? palette.DangerColor
                : snapshot.HasUnavailableDrives
                    ? palette.UnknownColor
                    : palette.SafeColor;
        return CreateCenteredIcon(snapshot.IconText, color, DiskBrandColor);
    }

    public static string GetDiskIconKey(DiskSpaceSnapshot snapshot)
    {
        return $"disk:{snapshot.IconText}:{snapshot.LowDriveLetters}:{snapshot.UnavailableDriveLetters}";
    }

    public static IntPtr CreateDiskUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateCenteredIcon("?", palette.UnknownColor, DiskBrandColor);
    }

    public static IntPtr CreateOpenCodeIcon(OpenCodeGoUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int rollingRemaining = CodexUsageMath.GetRemainingPercent(snapshot.Rolling.UsedPercent);
        int weeklyRemaining = CodexUsageMath.GetRemainingPercent(snapshot.Weekly.UsedPercent);
        return CreateIcon(
            rollingRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(rollingRemaining, palette),
            weeklyRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(weeklyRemaining, palette),
            OpenCodeBrandColor,
            snapshot.Weekly.ResetAt);
    }

    public static string GetOpenCodeIconKey(OpenCodeGoUsageSnapshot snapshot)
    {
        int rollingRemaining = CodexUsageMath.GetRemainingPercent(snapshot.Rolling.UsedPercent);
        int weeklyRemaining = CodexUsageMath.GetRemainingPercent(snapshot.Weekly.UsedPercent);
        int resetDays = GetResetDayDotCount(snapshot.Weekly.ResetAt);
        return $"opencode:{rollingRemaining}:{weeklyRemaining}:{resetDays}";
    }

    public static IntPtr CreateOpenCodeUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateCenteredIcon("?", palette.UnknownColor, OpenCodeBrandColor);
    }

    public static IntPtr CreateDevinIcon(DevinUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int dailyRemaining = GetRemainingPercent(snapshot.DailyRemainingPercent);
        int weeklyRemaining = GetRemainingPercent(snapshot.WeeklyRemainingPercent);
        return CreateIcon(
            dailyRemaining.ToString(CultureInfo.InvariantCulture),
            snapshot.DailyRemainingPercent is null
                ? palette.UnknownColor
                : ColorForRemaining(dailyRemaining, palette),
            weeklyRemaining.ToString(CultureInfo.InvariantCulture),
            snapshot.WeeklyRemainingPercent is null
                ? palette.UnknownColor
                : ColorForRemaining(weeklyRemaining, palette),
            DevinBrandColor,
            snapshot.WeeklyResetAt);
    }

    public static string GetDevinIconKey(DevinUsageSnapshot snapshot)
    {
        int dailyRemaining = GetRemainingPercent(snapshot.DailyRemainingPercent);
        int weeklyRemaining = GetRemainingPercent(snapshot.WeeklyRemainingPercent);
        int resetDays = GetResetDayDotCount(snapshot.WeeklyResetAt);
        return $"devin:{dailyRemaining}:{weeklyRemaining}:{resetDays}";
    }

    public static IntPtr CreateDevinUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon("?", palette.UnknownColor, "?", palette.UnknownColor, DevinBrandColor);
    }

    public static IntPtr CreateTemperatureIcon(HardwareSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        return CreateIcon(
            FormatTemperatureIconText(snapshot.CpuTemperatureC),
            ColorForTemperature(snapshot.CpuTemperatureC, palette),
            FormatTemperatureIconText(snapshot.GpuTemperatureC),
            ColorForTemperature(snapshot.GpuTemperatureC, palette),
            TemperatureBrandColor,
            drawBrandMarker: false);
    }

    public static string GetTemperatureIconKey(HardwareSnapshot snapshot)
    {
        return $"temperature:{FormatTemperatureIconText(snapshot.CpuTemperatureC)}:" +
               $"{FormatTemperatureIconText(snapshot.GpuTemperatureC)}";
    }

    public static IntPtr CreateTemperatureUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon(
            "?",
            palette.UnknownColor,
            "?",
            palette.UnknownColor,
            TemperatureBrandColor,
            drawBrandMarker: false);
    }

    public static IntPtr CreateMemoryIcon(HardwareSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        uint[] pixels = new uint[IconSize * IconSize];
        DrawUsageBar(
            pixels,
            0,
            GetUsageRatio(snapshot.CpuUsagePercent),
            ColorForUsage(snapshot.CpuUsagePercent, palette, CpuBarColor));
        DrawUsageBar(
            pixels,
            4,
            GetUsageRatio(snapshot.GpuUsagePercent),
            ColorForUsage(snapshot.GpuUsagePercent, palette, GpuBarColor));
        DrawUsageBar(
            pixels,
            8,
            GetUsageRatio(snapshot.RamUsagePercent),
            ColorForUsage(snapshot.RamUsagePercent, palette, RamBarColor));
        DrawUsageBar(
            pixels,
            12,
            GetUsageRatio(snapshot.VramUsagePercent),
            ColorForUsage(snapshot.VramUsagePercent, palette, VramBarColor));
        return CreateNativeIcon(pixels);
    }

    public static string GetMemoryIconKey(HardwareSnapshot snapshot)
    {
        return $"usage:{GetUsageBarCount(snapshot.CpuUsagePercent)}:" +
               $"{GetUsageBarCount(snapshot.GpuUsagePercent)}:" +
               $"{GetUsageBarCount(snapshot.RamUsagePercent)}:" +
               $"{GetUsageBarCount(snapshot.VramUsagePercent)}";
    }

    public static IntPtr CreateMemoryUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateCenteredIcon(
            "?",
            palette.UnknownColor,
            MemoryBrandColor,
            drawBrandMarker: false);
    }

    private static IntPtr CreateIcon(
        string topText,
        uint topColor,
        string bottomText,
        uint bottomColor,
        uint brandMarkerColor,
        DateTimeOffset? weeklyResetAt = null,
        bool drawBrandMarker = true)
    {
        uint[] pixels = new uint[IconSize * IconSize];
        if (drawBrandMarker)
        {
            DrawBrandTriangle(pixels, brandMarkerColor);
        }

        DrawText(pixels, topText, 0, topColor);
        DrawText(pixels, bottomText, 8, bottomColor);
        DrawResetDayDots(pixels, GetResetDayDotCount(weeklyResetAt), bottomColor);
        return CreateNativeIcon(pixels);
    }

    private static IntPtr CreateCenteredIcon(
        string text,
        uint textColor,
        uint brandMarkerColor,
        DateTimeOffset? weeklyResetAt = null,
        bool drawBrandMarker = true)
    {
        uint[] pixels = new uint[IconSize * IconSize];
        if (drawBrandMarker)
        {
            DrawBrandTriangle(pixels, brandMarkerColor);
        }

        DrawText(pixels, text, (IconSize - GlyphHeight) / 2, textColor);
        DrawResetDayDots(pixels, GetResetDayDotCount(weeklyResetAt), textColor);
        return CreateNativeIcon(pixels);
    }

    private static int GetResetDayDotCount(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return 0;
        }

        double daysRemaining = (resetAt.Value - DateTimeOffset.Now).TotalDays;
        return daysRemaining <= 0
            ? 0
            : Math.Clamp((int)Math.Ceiling(daysRemaining), 0, 7);
    }

    private static int GetRemainingPercent(double? remainingPercent)
    {
        return remainingPercent is { } value && double.IsFinite(value)
            ? Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100)
            : 0;
    }

    private static void DrawResetDayDots(uint[] pixels, int count, uint color)
    {
        ReadOnlySpan<int> rows = [0, 2, 4, 7, 10, 12, 14];
        for (int index = 0; index < count; index++)
        {
            pixels[rows[index] * IconSize] = color;
        }
    }

    private static void DrawBrandTriangle(uint[] pixels, uint color)
    {
        const int markerSize = 2;
        int startY = IconSize - markerSize;

        for (int y = startY; y < IconSize; y++)
        {
            int startX = IconSize - 1 - (y - startY);
            for (int x = startX; x < IconSize; x++)
            {
                pixels[(y * IconSize) + x] = color;
            }
        }
    }

    private static void DrawText(uint[] pixels, string text, int y, uint fillColor)
    {
        int width = (text.Length * GlyphWidth) + Math.Max(0, text.Length - 1) * GlyphSpacing;
        int startX = Math.Max(0, (IconSize - width) / 2);

        for (int index = 0; index < text.Length; index++)
        {
            int glyphX = startX + (index * (GlyphWidth + GlyphSpacing));
            DrawGlyph(pixels, text[index], glyphX, y, fillColor);
        }
    }

    private static void DrawGlyph(uint[] pixels, char value, int x, int y, uint color)
    {
        char glyphKey = Glyphs.ContainsKey(value) ? value : '?';
        string[] rows = Glyphs[glyphKey];

        for (int rowIndex = 0; rowIndex < GlyphHeight; rowIndex++)
        {
            string row = rows[rowIndex];
            for (int columnIndex = 0; columnIndex < GlyphWidth; columnIndex++)
            {
                if (row[columnIndex] != '1')
                {
                    continue;
                }

                int pixelX = x + columnIndex;
                int pixelY = y + rowIndex;
                if (pixelX < 0 || pixelX >= IconSize || pixelY < 0 || pixelY >= IconSize)
                {
                    continue;
                }

                pixels[(pixelY * IconSize) + pixelX] = color;
            }
        }
    }

    private static void DrawUsageBar(uint[] pixels, int y, double? ratio, uint fillColor)
    {
        const int segmentCount = 5;
        const int segmentWidth = 2;
        const int segmentGap = 1;
        const int startX = 1;
        const int height = 3;
        const uint EmptyBarColor = 0xFF555555;
        int filledSegments = ratio is null
            ? 0
            : Math.Clamp((int)Math.Round(ratio.Value * segmentCount, MidpointRounding.AwayFromZero), 0, segmentCount);

        for (int segment = 0; segment < segmentCount; segment++)
        {
            uint color = ratio is null
                ? EmptyBarColor
                : segment < filledSegments
                    ? fillColor
                    : EmptyBarColor;
            int x = startX + (segment * (segmentWidth + segmentGap));
            for (int row = y; row < y + height; row++)
            {
                for (int column = x; column < x + segmentWidth; column++)
                {
                    pixels[(row * IconSize) + column] = color;
                }
            }
        }
    }

    private static void DrawInfinity(uint[] pixels, uint color)
    {
        string[] rows =
        [
            "00110001100",
            "01001010010",
            "10000100001",
            "01001010010",
            "00110001100"
        ];
        const int startX = 2;
        const int startY = 5;

        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column] == '1')
                {
                    pixels[((startY + row) * IconSize) + startX + column] = color;
                }
            }
        }
    }

    private static string FormatBalanceIconText(decimal amount)
    {
        decimal wholeDollars = Math.Floor(Math.Max(0, amount));
        if (wholeDollars > 100)
        {
            return "infinity";
        }

        return wholeDollars.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string FormatTemperatureIconText(double? temperatureC)
    {
        if (temperatureC is null || !double.IsFinite(temperatureC.Value) || temperatureC.Value < 0)
        {
            return "?";
        }

        return Math.Clamp(
                (int)Math.Round(temperatureC.Value, MidpointRounding.AwayFromZero),
                0,
                999)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static uint ColorForTemperature(double? temperatureC, IconPalette palette)
    {
        if (temperatureC is null || !double.IsFinite(temperatureC.Value))
        {
            return palette.UnknownColor;
        }

        return temperatureC.Value >= 85d
            ? palette.DangerColor
            : temperatureC.Value >= 70d
                ? palette.WarningColor
                : palette.SafeColor;
    }

    private static double? GetUsageRatio(double? usagePercent)
    {
        if (usagePercent is null || !double.IsFinite(usagePercent.Value))
        {
            return null;
        }

        return Math.Clamp(usagePercent.Value / 100d, 0d, 1d);
    }

    private static int GetUsageBarCount(double? usagePercent)
    {
        double? ratio = GetUsageRatio(usagePercent);
        return ratio is null
            ? -1
            : Math.Clamp((int)Math.Round(ratio.Value * 5d, MidpointRounding.AwayFromZero), 0, 5);
    }

    private static uint ColorForUsage(
        double? usagePercent,
        IconPalette palette,
        uint normalColor)
    {
        if (usagePercent is null || !double.IsFinite(usagePercent.Value))
        {
            return palette.UnknownColor;
        }

        return usagePercent.Value >= 90d
            ? palette.DangerColor
            : usagePercent.Value >= 70d
                ? palette.WarningColor
                : normalColor;
    }

    private static IntPtr CreateNativeIcon(uint[] pixels)
    {
        byte[] rawBytes = new byte[pixels.Length * sizeof(uint)];
        Buffer.BlockCopy(pixels, 0, rawBytes, 0, rawBytes.Length);

        NativeMethods.BITMAPINFO bitmapInfo = new()
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = IconSize,
                biHeight = -IconSize,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            }
        };

        IntPtr colorBitmap = NativeMethods.CreateDIBSection(
            IntPtr.Zero,
            ref bitmapInfo,
            NativeMethods.DIB_RGB_COLORS,
            out IntPtr pixelBuffer,
            IntPtr.Zero,
            0);

        if (colorBitmap == IntPtr.Zero || pixelBuffer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.Copy(rawBytes, 0, pixelBuffer, rawBytes.Length);

        byte[] maskBytes = new byte[(IconSize * IconSize) / 8];
        GCHandle maskHandle = GCHandle.Alloc(maskBytes, GCHandleType.Pinned);
        IntPtr maskBitmap;
        try
        {
            maskBitmap = NativeMethods.CreateBitmap(IconSize, IconSize, 1, 1, maskHandle.AddrOfPinnedObject());
        }
        finally
        {
            maskHandle.Free();
        }

        if (maskBitmap == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(colorBitmap);
            return IntPtr.Zero;
        }

        NativeMethods.ICONINFO iconInfo = new()
        {
            fIcon = true,
            hbmColor = colorBitmap,
            hbmMask = maskBitmap
        };

        IntPtr iconHandle = NativeMethods.CreateIconIndirect(ref iconInfo);
        NativeMethods.DeleteObject(colorBitmap);
        NativeMethods.DeleteObject(maskBitmap);
        return iconHandle;
    }

    private static uint ColorForRemaining(int remainingPercent, IconPalette palette)
    {
        if (remainingPercent <= 15)
        {
            return palette.DangerColor;
        }

        if (remainingPercent <= 40)
        {
            return palette.WarningColor;
        }

        return palette.SafeColor;
    }

    private static uint ColorForBalance(decimal balance, bool isAvailable, IconPalette palette)
    {
        if (!isAvailable || balance <= 1)
        {
            return palette.DangerColor;
        }

        if (balance <= 5)
        {
            return palette.WarningColor;
        }

        return palette.SafeColor;
    }

    private static IconPalette GetPalette()
    {
        return IsLightTaskbarTheme() ? LightThemePalette : DarkThemePalette;
    }

    private static bool IsLightTaskbarTheme()
    {
        const string personalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        try
        {
            using RegistryKey? personalizeKey = Registry.CurrentUser.OpenSubKey(personalizeKeyPath, writable: false);
            object? value = personalizeKey?.GetValue("SystemUsesLightTheme");
            return value switch
            {
                int intValue => intValue != 0,
                byte byteValue => byteValue != 0,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private sealed record IconPalette(uint UnknownColor, uint DangerColor, uint WarningColor, uint SafeColor);
}

internal sealed class DiskSettingsWindow : IDisposable
{
    private const string WindowClassName = "limits.disk-settings";
    private const uint SaveButtonId = 1;
    private const uint CancelButtonId = 2;
    private const uint FirstRowControlId = 100;

    private static readonly NativeMethods.WndProcDelegate WindowProcedure = HandleWindowMessage;
    private static DiskSettingsWindow? Current;

    private readonly DiskMonitorSettings _initialSettings;
    private readonly Action<DiskMonitorSettings> _onSaved;
    private readonly Action _onClosed;
    private readonly List<DiskSettingsRow> _rows;
    private IntPtr _windowHandle;
    private bool _classRegistered;
    private bool _closed;

    public DiskSettingsWindow(
        DiskMonitorSettings initialSettings,
        Action<DiskMonitorSettings> onSaved,
        Action onClosed)
    {
        _initialSettings = DiskMonitorSettings.Normalize(initialSettings.Drives);
        _onSaved = onSaved;
        _onClosed = onClosed;
        _rows = BuildRows(_initialSettings);
    }

    public void Show(IntPtr owner)
    {
        if (Current is not null)
        {
            throw new InvalidOperationException("Disk settings are already open.");
        }

        Current = this;
        RegisterWindowClass();

        int width = 560;
        int height = 130 + (_rows.Count * 34);
        int x = Math.Max(0, (NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) - width) / 2);
        int y = Math.Max(0, (NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) - height) / 2);
        _windowHandle = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_DLGMODALFRAME,
            WindowClassName,
            "limits disk space settings",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            x,
            y,
            width,
            height,
            owner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            CompleteClose();
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        CreateControls();
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_SHOW);
        NativeMethods.UpdateWindow(_windowHandle);
        NativeMethods.SetForegroundWindow(_windowHandle);
        NativeMethods.SetFocus(_rows.FirstOrDefault()?.LimitEdit ?? _windowHandle);
    }

    public void Activate()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(_windowHandle);
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_windowHandle);
        }

        CompleteClose();
        UnregisterWindowClass();

        GC.SuppressFinalize(this);
    }

    private static List<DiskSettingsRow> BuildRows(DiskMonitorSettings settings)
    {
        Dictionary<string, DiskDriveSetting> configured = settings.Drives.ToDictionary(
            drive => drive.DriveLetter,
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> driveLetters = new(configured.Keys, StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string? driveLetter = TryGetDriveLetter(drive.Name);
                if (driveLetter is null || drive.DriveType == DriveType.CDRom)
                {
                    continue;
                }

                driveLetters.Add(driveLetter);
            }
        }
        catch
        {
            // Keep configured drives available even if drive enumeration fails.
        }

        return driveLetters
            .OrderBy(letter => letter, StringComparer.OrdinalIgnoreCase)
            .Select(letter =>
            {
                configured.TryGetValue(letter, out DiskDriveSetting? setting);
                return new DiskSettingsRow(
                    letter,
                    setting?.RedLimitGb ?? 5,
                    setting is not null);
            })
            .Select((row, index) =>
            {
                row.RowIndex = index;
                return row;
            })
            .ToList();
    }

    private static string? TryGetDriveLetter(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return null;
        }

        char letter = char.ToUpperInvariant(root[0]);
        return letter is >= 'A' and <= 'Z' ? letter.ToString() : null;
    }

    private void RegisterWindowClass()
    {
        NativeMethods.WNDCLASSEX windowClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            hInstance = NativeMethods.GetModuleHandle(null),
            hbrBackground = NativeMethods.GetSysColorBrush(NativeMethods.COLOR_WINDOW),
            lpszClassName = WindowClassName
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _classRegistered = true;
    }

    private void CreateControls()
    {
        int y = 14;
        CreateControl(
            "STATIC",
            "Select disks to monitor. Alert when free space is at or below the red limit (GB).",
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
            16,
            y,
            525,
            22,
            10);
        y += 30;

        CreateControl("STATIC", "Disk", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE, 20, y, 50, 22, 11);
        CreateControl("STATIC", "Monitor", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE, 82, y, 90, 22, 12);
        CreateControl("STATIC", "Red limit", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE, 230, y, 100, 22, 13);
        CreateControl("STATIC", "GB free", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE, 340, y, 80, 22, 14);
        y += 24;

        foreach (DiskSettingsRow row in _rows)
        {
            row.Checkbox = CreateControl(
                "BUTTON",
                "Monitor",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_AUTOCHECKBOX,
                78,
                y,
                100,
                24,
                row.CheckboxId);
            NativeMethods.SendMessage(
                row.Checkbox,
                NativeMethods.BM_SETCHECK,
                new IntPtr(row.IsSelected ? (int)NativeMethods.BST_CHECKED : (int)NativeMethods.BST_UNCHECKED),
                IntPtr.Zero);

            CreateControl("STATIC", $"{row.DriveLetter}:", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE, 20, y, 45, 24, row.DriveLabelId);
            row.LimitEdit = CreateControl(
                "EDIT",
                row.RedLimitGb.ToString("0.##", CultureInfo.InvariantCulture),
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.WS_BORDER | NativeMethods.ES_AUTOHSCROLL,
                230,
                y,
                95,
                24,
                row.LimitEditId,
                NativeMethods.WS_EX_CLIENTEDGE);
            CreateControl("STATIC", "GB", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE, 340, y, 50, 24, row.LimitLabelId);
            y += 34;
        }

        CreateControl(
            "BUTTON",
            "Save",
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_DEFPUSHBUTTON,
            350,
            y + 8,
            85,
            28,
            SaveButtonId);
        CreateControl(
            "BUTTON",
            "Cancel",
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP,
            445,
            y + 8,
            85,
            28,
            CancelButtonId);
    }

    private IntPtr CreateControl(
        string className,
        string text,
        uint style,
        int x,
        int y,
        int width,
        int height,
        uint controlId,
        uint extendedStyle = 0)
    {
        IntPtr control = NativeMethods.CreateWindowEx(
            extendedStyle,
            className,
            text,
            style,
            x,
            y,
            width,
            height,
            _windowHandle,
            (IntPtr)controlId,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (control == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed for {className}: {Marshal.GetLastWin32Error()}");
        }

        NativeMethods.SendMessage(
            control,
            NativeMethods.WM_SETFONT,
            NativeMethods.GetStockObject(NativeMethods.DEFAULT_GUI_FONT),
            new IntPtr(1));
        return control;
    }

    private void Save()
    {
        List<DiskDriveSetting> selected = [];
        foreach (DiskSettingsRow row in _rows)
        {
            bool isSelected = NativeMethods.SendMessage(
                row.Checkbox,
                NativeMethods.BM_GETCHECK,
                IntPtr.Zero,
                IntPtr.Zero) == (IntPtr)NativeMethods.BST_CHECKED;
            if (!isSelected)
            {
                continue;
            }

            string text = ReadText(row.LimitEdit).Trim();
            if (!TryParseLimit(text, out double redLimitGb) || redLimitGb < 0 || redLimitGb > 1_000_000)
            {
                NativeMethods.MessageBox(
                    _windowHandle,
                    $"Enter a red limit from 0 to 1,000,000 GB for drive {row.DriveLetter}.",
                    "Invalid disk limit",
                    NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
                NativeMethods.SetFocus(row.LimitEdit);
                return;
            }

            selected.Add(new DiskDriveSetting(row.DriveLetter, redLimitGb));
        }

        try
        {
            _onSaved(DiskMonitorSettings.Normalize(selected));
            CloseWindow();
        }
        catch (Exception exception)
        {
            NativeMethods.MessageBox(
                _windowHandle,
                $"Could not save disk settings: {exception.Message}",
                "Disk settings",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
        }
    }

    private static bool TryParseLimit(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
            double.IsFinite(value))
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string ReadText(IntPtr control)
    {
        int length = NativeMethods.GetWindowTextLength(control);
        StringBuilder text = new(length + 1);
        NativeMethods.GetWindowText(control, text, text.Capacity);
        return text.ToString();
    }

    private static IntPtr HandleWindowMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        return Current?.WndProc(windowHandle, message, wParam, lParam) ??
               NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_COMMAND:
                uint commandId = (uint)(wParam.ToInt64() & 0xffff);
                uint notification = (uint)((wParam.ToInt64() >> 16) & 0xffff);
                if (notification == NativeMethods.BN_CLICKED && commandId == SaveButtonId)
                {
                    Save();
                    return IntPtr.Zero;
                }

                if (notification == NativeMethods.BN_CLICKED && commandId == CancelButtonId)
                {
                    CloseWindow();
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_CLOSE:
                CloseWindow();
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                CompleteClose();
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void CompleteClose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _windowHandle = IntPtr.Zero;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        _onClosed();
    }

    private void CloseWindow()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_windowHandle);
        }

        UnregisterWindowClass();
    }

    private void UnregisterWindowClass()
    {
        if (_classRegistered)
        {
            NativeMethods.UnregisterClass(WindowClassName, NativeMethods.GetModuleHandle(null));
            _classRegistered = false;
        }
    }

    private sealed class DiskSettingsRow
    {
        public DiskSettingsRow(string driveLetter, double redLimitGb, bool isSelected)
        {
            DriveLetter = driveLetter;
            RedLimitGb = redLimitGb;
            IsSelected = isSelected;
        }

        public string DriveLetter { get; }
        public double RedLimitGb { get; }
        public bool IsSelected { get; }
        public uint DriveLabelId => FirstRowControlId + (uint)(RowIndex * 4);
        public uint CheckboxId => DriveLabelId + 1;
        public uint LimitEditId => DriveLabelId + 2;
        public uint LimitLabelId => DriveLabelId + 3;
        public int RowIndex { get; set; }
        public IntPtr Checkbox { get; set; }
        public IntPtr LimitEdit { get; set; }
    }
}

internal static class NativeMethods
{
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_APP = 0x8000;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_GUID = 0x00000020;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_POPUP = 0x00000010;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_CLIENTEDGE = 0x00000200;
    public const uint WS_EX_DLGMODALFRAME = 0x00000001;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const uint BS_AUTOCHECKBOX = 0x00000003;
    public const uint BS_DEFPUSHBUTTON = 0x00000001;
    public const uint ES_AUTOHSCROLL = 0x00000080;
    public const uint BM_GETCHECK = 0x00F0;
    public const uint BM_SETCHECK = 0x00F1;
    public const uint BST_UNCHECKED = 0x0000;
    public const uint BST_CHECKED = 0x0001;
    public const uint BN_CLICKED = 0;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int COLOR_WINDOW = 5;
    public const int DEFAULT_GUI_FONT = 17;
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int MA_NOACTIVATE = 3;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint DT_LEFT = 0x00000000;
    public const uint DT_RIGHT = 0x00000002;
    public const uint DT_VCENTER = 0x00000004;
    public const uint DT_SINGLELINE = 0x00000020;
    public const uint DT_END_ELLIPSIS = 0x00008000;
    public const int TRANSPARENT = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const uint MB_OK = 0x00000000;
    public const uint MB_ICONERROR = 0x00000010;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_ICONWARNING = 0x00000030;

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    public delegate IntPtr WndProcDelegate(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr hdc;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fErase;
        public RECT rcPaint;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fRestore;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public long ToInt64()
        {
            return ((long)HighDateTime << 32) | LowDateTime;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(
        out SYSTEM_FILETIME idleTime,
        out SYSTEM_FILETIME kernelTime,
        out SYSTEM_FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClass(string className, IntPtr instanceHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentHandle,
        IntPtr menuHandle,
        IntPtr instanceHandle,
        IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG message, IntPtr windowHandle, uint minimumMessage, uint maximumMessage);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(IntPtr windowHandle, nuint timerId, uint intervalMilliseconds, IntPtr timerFunction);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr windowHandle, nuint timerId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetWindowText(IntPtr windowHandle, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern IntPtr GetSysColorBrush(int index);

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int objectIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr BeginPaint(IntPtr windowHandle, ref PAINTSTRUCT paint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(IntPtr windowHandle, ref PAINTSTRUCT paint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr windowHandle, out RECT rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr windowHandle, out RECT rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InvalidateRect(IntPtr windowHandle, IntPtr rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int FillRect(IntPtr deviceContext, ref RECT rectangle, IntPtr brush);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int FrameRect(IntPtr deviceContext, ref RECT rectangle, IntPtr brush);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(IntPtr deviceContext, uint colorRef);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr deviceContext, int mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
    public static extern int DrawText(IntPtr deviceContext, string text, int characterCount, ref RECT rectangle, uint format);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr objectHandle);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW", SetLastError = true)]
    public static extern IntPtr CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint characterSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromRect(ref RECT rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MessageBeep(uint type);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconRectangle);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr menuHandle, uint flags, nuint itemId, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr menuHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenu(
        IntPtr menuHandle,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr windowHandle,
        IntPtr rectangle);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BITMAPINFO bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr iconHandle);
}
