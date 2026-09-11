using System.Runtime.InteropServices;
using System.Text;

namespace HeCalendar.Shell;

/// <summary>
/// Locates a tight rectangle over the Win11 taskbar clock/date only —
/// not the whole TrayNotifyWnd (which also contains the bell / other icons).
/// </summary>
internal static class TrayClockGeometry
{
    public static bool TryGetPrimaryClockRect(out RECT rect)
    {
        rect = default;
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        return TryGetClockRect(tray, out rect);
    }

    public static bool TryGetClockRect(IntPtr tray, out RECT rect)
    {
        rect = default;
        if (tray == IntPtr.Zero) return false;

        // Classic / older: dedicated clock HWND.
        foreach (var name in new[] { "TrayClockWClass", "ClockButton", "TrayClock" })
        {
            var clock = FindWindowEx(tray, IntPtr.Zero, name, null);
            if (clock != IntPtr.Zero && GetWindowRect(clock, out rect) && Area(rect) > 200)
            {
                // Never wider than a plausible clock strip.
                return ClampToClockWidth(ref rect, tray);
            }
        }

        // Prefer carving from TrayNotifyWnd rather than taking a large XAML island.
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var notifyRect))
        {
            rect = CarveClockFromNotifyArea(notifyRect, tray);
            return rect.Width >= 48 && rect.Height >= 20;
        }

        if (!GetWindowRect(tray, out var trayRect)) return false;
        rect = CarveClockFromNotifyArea(trayRect, tray);
        return rect.Width >= 48;
    }

    /// <summary>
    /// Win11 tray right edge layout (approx, LTR):
    /// [icons…][bell][clock/date][show-desktop ~6–14px]
    /// We only cover the clock/date band.
    /// </summary>
    private static RECT CarveClockFromNotifyArea(RECT notifyRect, IntPtr tray)
    {
        var h = Math.Max(1, notifyRect.Height);
        var showDesktop = MeasureShowDesktopWidth(tray, notifyRect, h);
        // Clock text strip: slightly tighter than before so it matches system clock footprint.
        var clockW = h switch
        {
            <= 40 => 70,
            <= 52 => 80,
            <= 64 => 90,
            _ => 100,
        };

        // Cap: never take more than ~40% of the notify area.
        var notifyW = notifyRect.Width;
        if (notifyW > 0)
            clockW = Math.Min(clockW, Math.Max(64, (int)(notifyW * 0.38)));

        var right = notifyRect.Right - showDesktop;
        var left = right - clockW;
        if (left < notifyRect.Left + 24)
            left = notifyRect.Left + Math.Min(24, notifyW / 3);

        return new RECT
        {
            Left = left,
            Top = notifyRect.Top,
            Right = right,
            Bottom = notifyRect.Bottom,
        };
    }

    private static bool ClampToClockWidth(ref RECT rect, IntPtr tray)
    {
        if (!GetWindowRect(tray, out var trayRect)) return true;
        if (rect.Width <= 140) return true;
        // Found HWND was too wide (whole notify island) — carve instead.
        rect = CarveClockFromNotifyArea(rect, tray);
        return true;
    }

    private static int MeasureShowDesktopWidth(IntPtr tray, RECT notifyRect, int trayHeight)
    {
        // Known class on some builds.
        var btn = FindWindowEx(tray, IntPtr.Zero, "TrayShowDesktopButtonWClass", null);
        if (btn != IntPtr.Zero && GetWindowRect(btn, out var br) && br.Width is > 0 and < 40)
            return br.Width;

        // Heuristic: thin strip at the far right of the tray.
        return Math.Clamp(trayHeight / 6, 6, 14);
    }

    private static int Area(RECT r) => Math.Max(0, r.Width) * Math.Max(0, r.Height);

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
