using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HeCalendar.Shell;

/// <summary>
/// Intercepts taskbar-clock clicks (eats the mouse event so Explorer never opens
/// the system calendar), then shows HeCalendar instead.
/// </summary>
public sealed class CalendarFlyoutHook : IDisposable
{
    private readonly FlyoutWindow _flyout;
    private IntPtr _ourHwnd = IntPtr.Zero;
    private IntPtr _hookShow = IntPtr.Zero;
    private IntPtr _hookMouse = IntPtr.Zero;
    private WinEventDelegate? _winEventCallback;
    private LowLevelMouseProc? _mouseCallback;
    private Timer? _pollTimer;
    private DateTime _armedUntil = DateTime.MinValue;
    private DateTime _lastToggleAt = DateTime.MinValue;
    private readonly HashSet<string> _seenCandidates = new(StringComparer.Ordinal);
    private GCHandle _mouseCallbackHandle;
    private int _pollErrorLogged;
    private volatile bool _disposed;

    public CalendarFlyoutHook(FlyoutWindow flyout)
    {
        _flyout = flyout;
    }

    public void Start()
    {
        try { _ourHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_flyout); }
        catch (Exception ex) { Log($"GetWindowHandle failed: {ex}"); }

        _winEventCallback = OnWinEvent;
        _hookShow = SetWinEventHook(
            EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
            IntPtr.Zero, _winEventCallback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _mouseCallback = OnMouse;
        _mouseCallbackHandle = GCHandle.Alloc(_mouseCallback);
        _hookMouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseCallback, GetModuleHandle(null), 0);
        Log($"CalendarFlyoutHook started mouseLL={_hookMouse != IntPtr.Zero} ourHwnd={_ourHwnd} (eat clock clicks)");

        _pollTimer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));

        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var tr))
            Log($"Primary tray rect={tr.Left},{tr.Top}-{tr.Right},{tr.Bottom}");
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && !_disposed)
            {
                var msg = wParam.ToInt32();
                if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var x = info.pt.X;
                    var y = info.pt.Y;

                    if (IsPointOnTrayClock(x, y))
                    {
                        // Swallow so Explorer never opens the system calendar/clock flyout.
                        if (msg == WM_LBUTTONUP)
                        {
                            ToggleOrShowFromClock();
                        }
                        return (IntPtr)1;
                    }

                    if (msg == WM_LBUTTONUP &&
                        _flyout.VisibleFlag &&
                        (DateTime.UtcNow - _flyout.ShownAtUtc).TotalMilliseconds > 450 &&
                        !IsPointInOurFlyout(x, y))
                    {
                        Log($"Outside click — hide");
                        _armedUntil = DateTime.MinValue;
                        _flyout.DispatcherQueue?.TryEnqueue(_flyout.HideFlyout);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"OnMouse error: {ex}");
        }

        return CallNextHookEx(_hookMouse, nCode, wParam, lParam);
    }

    private void ToggleOrShowFromClock()
    {
        if ((DateTime.UtcNow - _lastToggleAt).TotalMilliseconds < 350) return;
        _lastToggleAt = DateTime.UtcNow;

        if (_flyout.VisibleFlag)
        {
            Log("TrayClock click — hide (swallowed)");
            _armedUntil = DateTime.MinValue;
            _flyout.DispatcherQueue?.TryEnqueue(_flyout.HideFlyout);
            return;
        }

        Log("TrayClock click — show (swallowed, system blocked)");
        ArmTakeover();
    }

    private void ArmTakeover()
    {
        _armedUntil = DateTime.UtcNow.AddSeconds(5);
        _seenCandidates.Clear();

        _flyout.DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                _flyout.ShowFlyoutNearClock();
                _flyout.BringToFront();
            }
            catch (Exception ex)
            {
                Log($"Show enqueue failed: {ex.Message}");
            }
        });

        // Backup: if anything still pops (Win+Alt+D, race), keep killing it briefly.
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 50 && !_disposed; i++)
                {
                    HideSystemCalendarPopups($"arm-{i}");
                    if (i % 4 == 0)
                    {
                        _flyout.DispatcherQueue?.TryEnqueue(() =>
                        {
                            try
                            {
                                if (_flyout.VisibleFlag) _flyout.BringToFront();
                            }
                            catch { /* ignore */ }
                        });
                    }
                    await Task.Delay(40);
                    if (DateTime.UtcNow > _armedUntil) break;
                }
            }
            catch (Exception ex)
            {
                Log($"ArmTakeover error: {ex}");
            }
        });
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero || idObject != OBJID_WINDOW) return;
        if (DateTime.UtcNow > _armedUntil && !_flyout.VisibleFlag) return;
        HideIfSystemCalendar(hwnd, $"event={eventType}");
    }

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            if (DateTime.UtcNow <= _armedUntil || _flyout.VisibleFlag)
                HideSystemCalendarPopups("poll");
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _pollErrorLogged) <= 5)
                Log($"Poll error: {ex}");
        }
    }

    private void HideSystemCalendarPopups(string reason)
    {
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            HideIfSystemCalendar(hwnd, reason);
            return true;
        }, IntPtr.Zero);
    }

    private void HideIfSystemCalendar(IntPtr hwnd, string reason)
    {
        try
        {
            if (hwnd == _ourHwnd || hwnd == IntPtr.Zero) return;
            if (!IsWindowVisible(hwnd)) return;
            if (!GetWindowRect(hwnd, out var rect)) return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 50 || height < 50) return;
            if (width > 1400 || height > 2000) return;

            var processName = GetProcessName(hwnd);
            if (processName.Equals("HeCalendar.Shell", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                return;

            var className = GetClassName(hwnd);
            var title = GetWindowTitle(hwnd);
            var armed = DateTime.UtcNow <= _armedUntil;
            var corner = IsNearTrayCorner(rect) || IsNearBottomRight(rect);
            var bottomRightHalf =
                rect.Left > GetSystemMetrics(SM_CXSCREEN) / 2 &&
                rect.Top > GetSystemMetrics(SM_CYSCREEN) / 5;

            if (title.Contains("新通知", StringComparison.OrdinalIgnoreCase)) return;

            var isXamlPopup = className.Equals("Xaml_WindowedPopupClass", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Xaml", StringComparison.OrdinalIgnoreCase);
            var isCoreWindow = className.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Windows.UI", StringComparison.OrdinalIgnoreCase);
            var isExplorer = processName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            var isShellHost =
                processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ShellHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase);

            // While our flyout is up / armed: aggressively hide explorer/shell popups in the clock corner.
            var match =
                (isExplorer || isShellHost) &&
                (isXamlPopup || isCoreWindow || className.Contains("Popup", StringComparison.OrdinalIgnoreCase)) &&
                (corner || bottomRightHalf || armed) &&
                height >= 50 && width >= 50;

            // Also catch large notification-center sized panels near the right edge.
            if (!match && (isExplorer || isShellHost) && bottomRightHalf && width is >= 280 and <= 900 && height >= 280)
                match = true;

            if (!match)
            {
                if (armed && (isExplorer || isShellHost) && height >= 60)
                {
                    var key = $"c:{processName}|{className}|{width}x{height}|{rect.Left},{rect.Top}";
                    if (_seenCandidates.Add(key) && _seenCandidates.Count <= 80)
                        Log($"Hook candidate: proc={processName} class={className} title='{title}' size={width}x{height} pos={rect.Left},{rect.Top}");
                }
                return;
            }

            Log($"Hook hide ({reason}): proc={processName} class={className} title='{title}' size={width}x{height} pos={rect.Left},{rect.Top}");
            ShowWindow(hwnd, SW_HIDE);
            SetWindowPos(hwnd, HWND_BOTTOM, -32000, -32000, width, height,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _pollErrorLogged) <= 8)
                Log($"HideIfSystemCalendar error: {ex.Message}");
        }
    }

    private bool IsPointInOurFlyout(int x, int y)
    {
        return _flyout.IsPointInShellWindows(x, y);
    }

    private static bool IsPointOnTrayClock(int x, int y)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && TrayClockGeometry.TryGetClockRect(tray, out var rect))
        {
            // Inflate slightly for easier hit / overlay edge.
            if (x >= rect.Left - 4 && x <= rect.Right + 4 && y >= rect.Top - 4 && y <= rect.Bottom + 4)
                return true;
        }

        var secondary = IntPtr.Zero;
        while (true)
        {
            secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
            if (secondary == IntPtr.Zero) break;
            if (TrayClockGeometry.TryGetClockRect(secondary, out var sec) &&
                x >= sec.Left - 4 && x <= sec.Right + 4 && y >= sec.Top - 4 && y <= sec.Bottom + 4)
                return true;
        }

        return false;
    }

    private static bool IsNearBottomRight(RECT rect)
    {
        foreach (var wa in EnumerateMonitorWorkAreas())
        {
            var insideRight = wa.Left + (wa.Right - wa.Left) - rect.Right;
            var insideBottom = wa.Top + (wa.Bottom - wa.Top) - rect.Bottom;
            if (insideRight is >= -40 and <= 220 && insideBottom is >= -40 and <= 220) return true;
        }
        return false;
    }

    private static bool IsNearTrayCorner(RECT rect)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var trayRect)) return false;
        var trayWidth = Math.Max(1, trayRect.Right - trayRect.Left);
        var rightBand = trayRect.Right - Math.Max(320, trayWidth / 4);
        var inRightBand = rect.Right >= rightBand || rect.Left >= rightBand - 100;
        var aboveTray = trayRect.Top - rect.Bottom;
        var verticallyNear = aboveTray is >= -80 and <= 260
            || (rect.Bottom >= trayRect.Top - 60 && rect.Top < trayRect.Bottom + 20);
        return inRightBand && verticallyNear;
    }

    private static List<RECT> EnumerateMonitorWorkAreas()
    {
        var list = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info)) list.Add(info.rcWork);
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0)
        {
            list.Add(new RECT
            {
                Left = 0, Top = 0,
                Right = GetSystemMetrics(SM_CXSCREEN),
                Bottom = GetSystemMetrics(SM_CYSCREEN),
            });
        }
        return list;
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeCalendar");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shell.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        _disposed = true;
        _pollTimer?.Dispose();
        if (_hookShow != IntPtr.Zero) UnhookWinEvent(_hookShow);
        if (_hookMouse != IntPtr.Zero) UnhookWindowsHookEx(_hookMouse);
        if (_mouseCallbackHandle.IsAllocated) _mouseCallbackHandle.Free();
        _hookShow = IntPtr.Zero;
        _hookMouse = IntPtr.Zero;
    }

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const int OBJID_WINDOW = 0;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const int SW_HIDE = 0;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
