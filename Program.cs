using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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

        using TrayApplication application = new();
        return application.Run();
    }
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
    private const uint KimiTrayIconId = 3;
    private const nuint RefreshTimerId = 1;
    private const uint RefreshIntervalMs = 300_000;
    private const int ClaudeRefreshTimeoutSeconds = 25;
    private const uint CommandRefresh = 1001;
    private const uint CommandOpenCodexSessions = 1002;
    private const uint CommandExit = 1003;
    private const uint CommandOpenClaudeUsage = 1004;
    private const uint CommandOpenKimiSessions = 1005;
    private const string ShutdownEventName = @"Local\Limits.Shutdown";

    private static readonly Guid CodexTrayIconGuid = new("2a642a8d-169a-4035-ad86-ea43b5e87764");
    private static readonly Guid ClaudeTrayIconGuid = new("4654b565-47c7-49af-a257-8f26d82c0ec0");
    private static readonly Guid KimiTrayIconGuid = new("918bd040-6a80-4b43-ae66-13a8f5bb1d57");

    private static readonly NativeMethods.WndProcDelegate WindowProcedure = HandleWindowMessage;
    private static TrayApplication? Current;

    private readonly object _shellNotifyQueueLock = new();
    private readonly CodexUsageReader _codexUsageReader = new();
    private readonly ClaudeUsageReader _claudeUsageReader = new();
    private readonly KimiUsageReader _kimiUsageReader = new();
    private readonly LimitWatchdog _limitWatchdog = new();
    private readonly string _windowClassName = $"limits.{Environment.ProcessId}";
    private readonly EventWaitHandle _shutdownEvent;
    private readonly RegisteredWaitHandle _shutdownRegistration;
    private readonly uint _taskbarCreatedMessage;
    private Task _shellNotifyQueue = Task.CompletedTask;

    private IntPtr _windowHandle;
    private IntPtr _codexIconHandle;
    private IntPtr _claudeIconHandle;
    private IntPtr _kimiIconHandle;
    private bool _codexTrayIconAdded;
    private bool _claudeTrayIconAdded;
    private bool _kimiTrayIconAdded;
    private bool _windowClassRegistered;
    private string? _codexIconKey;
    private string? _claudeIconKey;
    private string? _kimiIconKey;
    private string? _codexAppliedTooltip;
    private string? _claudeAppliedTooltip;
    private string? _kimiAppliedTooltip;
    private string _codexTooltip = "limits";
    private string _codexStatusText = "Loading Codex usage...";
    private string _codexDetailText = "Reading local Codex sessions.";
    private string _codexSparkUsageText = "Spark usage: loading...";
    private string _codexUpdatedText = string.Empty;
    private string _codexSourceText = string.Empty;
    private string _claudeTooltip = "limits";
    private string _claudeStatusText = "Loading Claude usage...";
    private string _claudeDetailText = "Reading Claude OAuth usage.";
    private string _claudeUpdatedText = string.Empty;
    private string _claudeSourceText = string.Empty;
    private string _kimiTooltip = "limits";
    private string _kimiStatusText = "Loading Kimi usage...";
    private string _kimiDetailText = "Reading Kimi Code quota.";
    private string _kimiTokenUsageText = "Kimi tokens: loading...";
    private string _kimiUpdatedText = string.Empty;
    private string _kimiSourceText = string.Empty;
    private KimiUsageSnapshot? _lastKimiSnapshot;
    private volatile bool _codexRefreshInFlight;
    private volatile UsageReadResult? _pendingCodexResult;
    private volatile bool _claudeRefreshInFlight;
    private volatile ClaudeUsageReadResult? _pendingClaudeResult;
    private volatile bool _kimiRefreshInFlight;
    private volatile KimiUsageReadResult? _pendingKimiResult;
    private volatile bool _limitWatchdogInFlight;

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
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
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

    public int Run()
    {
        Current = this;
        RegisterWindowClass();
        CreateMessageWindow();
        UpdateCodexTrayIcon(TrayIconRenderer.CreateUnavailableIcon(), TrayIconRenderer.CodexUnavailableIconKey);
        UpdateClaudeTrayIcon(TrayIconRenderer.CreateClaudeUnavailableIcon(), TrayIconRenderer.ClaudeUnavailableIconKey);
        UpdateKimiTrayIcon(TrayIconRenderer.CreateKimiUnavailableIcon(), TrayIconRenderer.KimiUnavailableIconKey);
        RefreshUsage();
        NativeMethods.SetTimer(_windowHandle, RefreshTimerId, RefreshIntervalMs, IntPtr.Zero);

        while (NativeMethods.GetMessage(out NativeMethods.MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        return 0;
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_windowHandle);
        }

        CleanupNativeResources();
        _shutdownRegistration.Unregister(null);
        _shutdownEvent.Dispose();
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
    }

    private void RefreshUsage()
    {
        RefreshCodexUsage();
        RefreshClaudeUsage();
        RefreshKimiUsage();
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

                break;

            case CodexResultMessage:
                ApplyCodexResult();
                return IntPtr.Zero;

            case ClaudeResultMessage:
                ApplyClaudeResult();
                return IntPtr.Zero;

            case KimiResultMessage:
                ApplyKimiResult();
                return IntPtr.Zero;

            case ShutdownMessage:
                NativeMethods.DestroyWindow(_windowHandle);
                return IntPtr.Zero;

            case TrayCallbackMessage:
                uint iconId = (uint)wParam.ToInt64();
                if (iconId is not CodexTrayIconId and not ClaudeTrayIconId and not KimiTrayIconId)
                {
                    break;
                }

                switch ((uint)lParam.ToInt64())
                {
                    case NativeMethods.WM_LBUTTONDBLCLK:
                        RefreshUsage();
                        return IntPtr.Zero;

                    case NativeMethods.WM_RBUTTONUP:
                    case NativeMethods.WM_CONTEXTMENU:
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

    private void RecreateTrayIcons()
    {
        _codexTrayIconAdded = false;
        _claudeTrayIconAdded = false;
        _kimiTrayIconAdded = false;
        _codexIconKey = null;
        _claudeIconKey = null;
        _kimiIconKey = null;
        _codexAppliedTooltip = null;
        _claudeAppliedTooltip = null;
        _kimiAppliedTooltip = null;

        UpdateCodexTrayIcon(TrayIconRenderer.CreateUnavailableIcon(), TrayIconRenderer.CodexUnavailableIconKey);
        UpdateClaudeTrayIcon(TrayIconRenderer.CreateClaudeUnavailableIcon(), TrayIconRenderer.ClaudeUnavailableIconKey);
        UpdateKimiTrayIcon(TrayIconRenderer.CreateKimiUnavailableIcon(), TrayIconRenderer.KimiUnavailableIconKey);
        RefreshUsage();
    }

    private void ShowContextMenu(uint iconId)
    {
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
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandRefresh, "Refresh now");
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, openCommand, openLabel);
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

    private void PostQuit()
    {
        _windowHandle = IntPtr.Zero;
        Current = null;
        NativeMethods.PostQuitMessage(0);
    }

    private void CleanupNativeResources()
    {
        RemoveTrayIcon(CodexTrayIconId, ref _codexTrayIconAdded);
        RemoveTrayIcon(ClaudeTrayIconId, ref _claudeTrayIconAdded);
        RemoveTrayIcon(KimiTrayIconId, ref _kimiTrayIconAdded);

        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_windowHandle, RefreshTimerId);
        }

        DestroyIconHandle(ref _codexIconHandle);
        DestroyIconHandle(ref _claudeIconHandle);
        DestroyIconHandle(ref _kimiIconHandle);

        if (_windowClassRegistered)
        {
            NativeMethods.UnregisterClass(_windowClassName, NativeMethods.GetModuleHandle(null));
            _windowClassRegistered = false;
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
    private const int MaxFilesToScan = 32;
    private const int MaxTailBytesToRead = 4 * 1024 * 1024;
    private const int MaxExpandedTailBytesToRead = 64 * 1024 * 1024;
    private const int MaxExpandedTailFilesToScan = 8;
    private const int MaxModelPrefixLinesToRead = 200;

    public string CodexHomePath { get; } = ResolveCodexHome();
    public string SessionsPath => Path.Combine(CodexHomePath, "sessions");

    public UsageReadResult ReadLatestSnapshot()
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

            if (latest is null || parsed.Timestamp > latest.Timestamp)
            {
                latest = parsed;
            }

            if (IsSparkSnapshot(parsed) && (latestSpark is null || parsed.Timestamp > latestSpark.Timestamp))
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
            string? snapshotModel = ResolveSnapshotModel(payload, model);

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

    private static string? ResolveSnapshotModel(JsonElement payload, string? contextModel)
    {
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
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string ClaudeHomePath { get; } = ResolveClaudeHome();
    public string CredentialsPath => Path.Combine(ClaudeHomePath, ".credentials.json");

    public ClaudeUsageReadResult ReadLatestSnapshot()
    {
        if (!File.Exists(CredentialsPath))
        {
            return new ClaudeUsageReadResult(null, $"Missing Claude credentials file: {CredentialsPath}");
        }

        try
        {
            string? accessToken = ReadAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new ClaudeUsageReadResult(null, "Claude OAuth access token was not found.");
            }

            using HttpRequestMessage request = new(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "gpttrack/1.0");

            using HttpResponseMessage response = HttpClient.Send(request);
            string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                string reason = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Claude token expired - re-login in Claude Code"
                    : $"Claude usage request failed: HTTP {(int)response.StatusCode}";
                return new ClaudeUsageReadResult(null, reason);
            }

            ClaudeUsageSnapshot? snapshot = ParseUsageResponse(responseBody);
            return snapshot is null
                ? new ClaudeUsageReadResult(null, "Claude usage response did not include five_hour and seven_day limits.")
                : new ClaudeUsageReadResult(snapshot, null);
        }
        catch (Exception exception)
        {
            return new ClaudeUsageReadResult(null, exception.Message);
        }
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

    private string? ReadAccessToken()
    {
        string? environmentToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return environmentToken;
        }

        using FileStream stream = new(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("claudeAiOauth", out JsonElement oauth) &&
            oauth.TryGetProperty("accessToken", out JsonElement accessTokenElement))
        {
            return accessTokenElement.GetString();
        }

        return null;
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

internal sealed class LimitWatchdog
{
    private const double ThresholdRemainingPercent = 5.0d;
    private const long MinimumFreeDiskBytes = 5L * 1024L * 1024L * 1024L;

    private static readonly string[] DrivesToMonitor = ["C", "D"];

    private static readonly string PauseBatchPath =
        @"C:\Users\flcl\Desktop\Pause Strategy Hunt.bat";

    private static readonly string RunBatchPath =
        @"C:\Users\flcl\Desktop\Run Strategy Hunt.bat";

    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "limits");

    private static readonly string LogPath = Path.Combine(AppDataPath, "limits.log");
    private string _state = "unknown";

    public void Check(ClaudeUsageSnapshot? snapshot)
    {
        Directory.CreateDirectory(AppDataPath);

        string state = _state;
        LimitUsage? usage = snapshot is null ? null : LimitUsage.FromSnapshot(snapshot);
        DiskSnapshot disk = ReadDiskSnapshot();

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

    private static DiskSnapshot ReadDiskSnapshot()
    {
        List<DriveFreeSpace> drives = [];
        foreach (string driveName in DrivesToMonitor)
        {
            try
            {
                DriveInfo drive = new($@"{driveName}:\");
                if (!drive.IsReady)
                {
                    drives.Add(new DriveFreeSpace(driveName, null, "not ready"));
                    continue;
                }

                drives.Add(new DriveFreeSpace(driveName, drive.AvailableFreeSpace, null));
            }
            catch (Exception exception)
            {
                drives.Add(new DriveFreeSpace(driveName, null, exception.Message));
            }
        }

        return new DiskSnapshot(drives);
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

    private static string FormatBytes(long bytes)
    {
        double gib = bytes / 1024d / 1024d / 1024d;
        return $"{gib:0.##} GB";
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

    private sealed record DiskSnapshot(IReadOnlyList<DriveFreeSpace> Drives)
    {
        public bool HasLowDisk => Drives.Any(drive =>
            drive.FreeBytes is null || drive.FreeBytes.Value < MinimumFreeDiskBytes);

        public string Summary => string.Join(", ", Drives.Select(drive =>
            drive.FreeBytes is null
                ? $"{drive.Name}: unavailable ({drive.Error ?? "unknown"})"
                : $"{drive.Name}: {FormatBytes(drive.FreeBytes.Value)} free"));
    }

    private sealed record DriveFreeSpace(string Name, long? FreeBytes, string? Error);
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

    // Brand marker colors are fixed, not theme-dependent.
    private const uint OpenAiBrandColor = 0xFF10A37F;
    private const uint ClaudeBrandColor = 0xFFD97757;
    private const uint KimiBrandColor = 0xFF23B7F0;

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
        ['?'] = ["1110", "0001", "0010", "0010", "0000", "0010", "0000"]
    };

    public static IntPtr CreateUsageIcon(CodexUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int weeklyRemaining = CodexUsageMath.GetWeeklyRemainingPercent(snapshot);

        return CreateCenteredIcon(
            weeklyRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(weeklyRemaining, palette),
            OpenAiBrandColor);
    }

    public static string GetCodexIconKey(CodexUsageSnapshot snapshot)
    {
        int weeklyRemaining = CodexUsageMath.GetWeeklyRemainingPercent(snapshot);
        return $"codex:{weeklyRemaining}";
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
            ClaudeBrandColor);
    }

    public static string GetClaudeIconKey(ClaudeUsageSnapshot snapshot)
    {
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);
        return $"claude:{fiveHourRemaining}:{sevenDayRemaining}";
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
            KimiBrandColor);
    }

    public static string GetKimiIconKey(KimiUsageSnapshot snapshot)
    {
        int fiveHourRemaining = KimiUsageMath.GetRemainingPercent(snapshot.FiveHourRemainingPercent);
        int sevenDayRemaining = KimiUsageMath.GetRemainingPercent(snapshot.SevenDayRemainingPercent);
        return $"kimi:{fiveHourRemaining}:{sevenDayRemaining}";
    }

    public static IntPtr CreateKimiUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon("?", palette.UnknownColor, "?", palette.UnknownColor, KimiBrandColor);
    }

    private static IntPtr CreateIcon(
        string topText,
        uint topColor,
        string bottomText,
        uint bottomColor,
        uint brandMarkerColor)
    {
        uint[] pixels = new uint[IconSize * IconSize];
        DrawBrandTriangle(pixels, brandMarkerColor);
        DrawText(pixels, topText, 0, topColor);
        DrawText(pixels, bottomText, 8, bottomColor);
        return CreateNativeIcon(pixels);
    }

    private static IntPtr CreateCenteredIcon(
        string text,
        uint textColor,
        uint brandMarkerColor)
    {
        uint[] pixels = new uint[IconSize * IconSize];
        DrawBrandTriangle(pixels, brandMarkerColor);
        DrawText(pixels, text, (IconSize - GlyphHeight) / 2, textColor);
        return CreateNativeIcon(pixels);
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

internal static class NativeMethods
{
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_TIMER = 0x0113;
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
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

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
