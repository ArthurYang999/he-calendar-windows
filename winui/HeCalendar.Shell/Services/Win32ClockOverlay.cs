using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HeCalendar.Shell;

/// <summary>
/// Win32 tray-clock cover (child of taskbar) + hover clock popup.
/// Survives virtual-desktop switches by parenting to Shell_TrayWnd.
/// </summary>
internal sealed class Win32ClockOverlay : IDisposable
{
    private readonly FlyoutWindow _flyout;
    private readonly WndProc _wndProc;
    private readonly WndProc _hoverWndProc;
    private readonly System.Threading.Timer _timer;
    private IntPtr _hwnd;
    private IntPtr _hoverHwnd;
    private IntPtr _trayHwnd;
    private bool _disposed;
    private bool _hoverVisible;
    private string _line1 = "--:--";
    private string _line2 = "";
    private string _timeFormat = "HH:mm";
    private string _dateFormat = "yyyy/M/d";
    private bool _showDate = true;
    private bool _dark = true;
    private RECT _lastScreenRect;
    private DateTime _lastSettingsLoad = DateTime.MinValue;

    private static readonly string OverlayClass = "HeCalendar.ClockOverlay.v2";
    private static readonly string HoverClass = "HeCalendar.ClockHover.v2";

    public Win32ClockOverlay(FlyoutWindow flyout)
    {
        _flyout = flyout;
        _wndProc = OverlayWndProc;
        _hoverWndProc = HoverWndProc;

        RegisterClass(OverlayClass, _wndProc);
        RegisterClass(HoverClass, _hoverWndProc);

        CreateOverlayWindow();
        CreateHoverWindow();

        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        LoadFormats();
        RefreshTheme();
        RefreshText();
        Reposition(force: true);

        _timer = new System.Threading.Timer(_ => Tick(), null, 0, 250);
        Log("Win32ClockOverlay started (tray-parented)");
    }

    private void OnSystemChanged(object? sender, EventArgs e) =>
        _flyout.DispatcherQueue?.TryEnqueue(() => Reposition(force: true));

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.Window or UserPreferenceCategory.General)
            _flyout.DispatcherQueue?.TryEnqueue(() =>
            {
                RefreshTheme();
                Reposition(force: true);
            });
    }

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            if ((DateTime.UtcNow - _lastSettingsLoad).TotalSeconds >= 2)
                LoadFormats();

            RefreshText();
            EnsureOverlayAlive();
            Reposition(force: false);

            if (_hwnd != IntPtr.Zero)
                InvalidateRect(_hwnd, IntPtr.Zero, false);
            if (_hoverVisible && _hoverHwnd != IntPtr.Zero)
                InvalidateRect(_hoverHwnd, IntPtr.Zero, false);
        }
        catch (Exception ex)
        {
            Log($"Tick error: {ex.Message}");
        }
    }

    private void EnsureOverlayAlive()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
        {
            Log("Overlay hwnd lost — recreating");
            CreateOverlayWindow();
            Reposition(force: true);
        }
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && tray != _trayHwnd)
        {
            _trayHwnd = tray;
            Reposition(force: true);
        }
    }

    private void LoadFormats()
    {
        _lastSettingsLoad = DateTime.UtcNow;
        try
        {
            _timeFormat = TodoStore.GetSetting("tray_time_format", "HH:mm");
            _dateFormat = TodoStore.GetSetting("tray_date_format", "yyyy/M/d");
            _showDate = !string.Equals(TodoStore.GetSetting("tray_show_date", "true"), "false", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(_timeFormat)) _timeFormat = "HH:mm";
        }
        catch { /* ignore */ }
    }

    private void RefreshText()
    {
        var now = DateTime.Now;
        try { _line1 = now.ToString(_timeFormat, CultureInfo.CurrentCulture); }
        catch { _line1 = now.ToString("HH:mm"); }

        if (_showDate && !string.IsNullOrWhiteSpace(_dateFormat))
        {
            try { _line2 = now.ToString(_dateFormat, CultureInfo.CurrentCulture); }
            catch { _line2 = now.ToString("yyyy/M/d"); }
        }
        else _line2 = "";

        if (Environment.TickCount % 4000 < 300) RefreshTheme();
    }

    private void RefreshTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            _dark = value is not int i || i == 0;
        }
        catch { _dark = true; }
    }

    private void CreateOverlayWindow()
    {
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_VISIBLE = 0x10000000;
        const int WS_CHILD = 0x40000000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        _trayHwnd = FindWindow("Shell_TrayWnd", null);
        // Child of tray survives virtual-desktop switches with the taskbar.
        var style = _trayHwnd != IntPtr.Zero ? (WS_CHILD | WS_VISIBLE) : (WS_POPUP | WS_VISIBLE);
        var ex = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            DestroyWindow(_hwnd);

        _hwnd = CreateWindowEx(
            ex, OverlayClass, "HeCalendarClock",
            style,
            0, 0, 80, 40,
            _trayHwnd, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            Log($"CreateOverlay failed: {Marshal.GetLastWin32Error()}");
    }

    private void CreateHoverWindow()
    {
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;

        _hoverHwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            HoverClass, "HeCalendarHoverClock",
            WS_POPUP,
            0, 0, 220, 260,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
    }

    private void Reposition(bool force)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        if (!TrayClockGeometry.TryGetPrimaryClockRect(out var screen)) return;

        var tray = _trayHwnd != IntPtr.Zero ? _trayHwnd : FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;

        // Convert screen rect → tray client coords when parented.
        var pt = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(tray, ref pt);
        var w = Math.Max(64, screen.Width);
        var h = Math.Max(28, screen.Height);

        if (!force &&
            screen.Left == _lastScreenRect.Left && screen.Top == _lastScreenRect.Top &&
            screen.Right == _lastScreenRect.Right && screen.Bottom == _lastScreenRect.Bottom &&
            IsWindowVisible(_hwnd))
        {
            // Keep z-order on top of tray children.
            SetWindowPos(_hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return;
        }

        _lastScreenRect = new RECT { Left = screen.Left, Top = screen.Top, Right = screen.Right, Bottom = screen.Bottom };
        SetWindowPos(_hwnd, HWND_TOP, pt.X, pt.Y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (force) Log($"Overlay client=({pt.X},{pt.Y}) {w}x{h} screen=({screen.Left},{screen.Top})");
    }

    private void ShowHover()
    {
        if (_hoverHwnd == IntPtr.Zero || !TrayClockGeometry.TryGetPrimaryClockRect(out var clock)) return;
        const int hw = 228;
        const int hh = 268;
        var x = clock.Left + clock.Width / 2 - hw / 2;
        var y = clock.Top - hh - 8;
        if (y < 8) y = clock.Bottom + 8;
        SetWindowPos(_hoverHwnd, HWND_TOPMOST, x, y, hw, hh, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _hoverVisible = true;
        InvalidateRect(_hoverHwnd, IntPtr.Zero, true);
    }

    private void HideHover()
    {
        if (!_hoverVisible) return;
        _hoverVisible = false;
        if (_hoverHwnd != IntPtr.Zero)
            ShowWindow(_hoverHwnd, SW_HIDE);
    }

    private IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                PaintOverlay(hWnd);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return (IntPtr)1;
            case WM_LBUTTONUP:
                HideHover();
                _flyout.DispatcherQueue?.TryEnqueue(() =>
                {
                    _flyout.ShowFlyoutNearClock();
                    _flyout.BringToFront();
                });
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                HideHover();
                ShowTrayContextMenu();
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                TrackMouse(hWnd);
                if (!_hoverVisible) ShowHover();
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                HideHover();
                return IntPtr.Zero;
            case WM_MOUSEHOVER:
                ShowHover();
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr HoverWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                PaintHover(hWnd);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return (IntPtr)1;
            case WM_MOUSELEAVE:
            case WM_LBUTTONUP:
                HideHover();
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void TrackMouse(IntPtr hWnd)
    {
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE | TME_HOVER,
            hwndTrack = hWnd,
            dwHoverTime = 80,
        };
        _ = TrackMouseEvent(ref tme);
    }

    private void PaintOverlay(IntPtr hWnd)
    {
        var ps = new PAINTSTRUCT();
        var hdc = BeginPaint(hWnd, out ps);
        try
        {
            GetClientRect(hWnd, out var rc);
            var w = rc.Right - rc.Left;
            var h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) return;

            using var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(_dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 245, 245, 245));

            var fg = _dark ? Color.White : Color.Black;
            // Larger, tray-readable sizes (scale with tray height / DPI).
            var timePx = Math.Clamp(h * 0.34f, 14f, 22f);
            var datePx = Math.Clamp(h * 0.22f, 11f, 15f);
            using var timeFont = new Font("Segoe UI Semibold", timePx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var dateFont = new Font("Segoe UI", datePx, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(fg);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            if (string.IsNullOrEmpty(_line2))
            {
                g.DrawString(_line1, timeFont, brush, new RectangleF(0, 0, w, h), sf);
            }
            else
            {
                g.DrawString(_line1, timeFont, brush, new RectangleF(0, h * 0.05f, w, h * 0.52f), sf);
                g.DrawString(_line2, dateFont, brush, new RectangleF(0, h * 0.50f, w, h * 0.42f), sf);
            }

            using var screen = Graphics.FromHdc(hdc);
            screen.DrawImageUnscaled(bmp, 0, 0);
        }
        finally { EndPaint(hWnd, ref ps); }
    }

    private void PaintHover(IntPtr hWnd)
    {
        var ps = new PAINTSTRUCT();
        var hdc = BeginPaint(hWnd, out ps);
        try
        {
            GetClientRect(hWnd, out var rc);
            var w = rc.Right - rc.Left;
            var h = rc.Bottom - rc.Top;
            using var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var bg = _dark ? Color.FromArgb(245, 28, 28, 28) : Color.FromArgb(245, 250, 250, 250);
            var fg = _dark ? Color.White : Color.FromArgb(255, 20, 20, 20);
            var accent = _dark ? Color.FromArgb(255, 80, 160, 255) : Color.FromArgb(255, 0, 99, 177);
            g.Clear(bg);
            using (var path = RoundedRect(2, 2, w - 4, h - 4, 16))
            using (var br = new SolidBrush(bg))
                g.FillPath(br, path);

            var now = DateTime.Now;
            var cx = w / 2f;
            var cy = h * 0.42f;
            var radius = Math.Min(w, h) * 0.32f;

            using (var pen = new Pen(_dark ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(180, 0, 0, 0), 2f))
                g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);

            // Hour ticks
            for (var i = 0; i < 12; i++)
            {
                var ang = i * 30f * Math.PI / 180.0;
                var x1 = cx + (float)Math.Sin(ang) * radius * 0.78f;
                var y1 = cy - (float)Math.Cos(ang) * radius * 0.78f;
                var x2 = cx + (float)Math.Sin(ang) * radius * 0.92f;
                var y2 = cy - (float)Math.Cos(ang) * radius * 0.92f;
                using var pen = new Pen(fg, i % 3 == 0 ? 2.2f : 1.2f);
                g.DrawLine(pen, x1, y1, x2, y2);
            }

            var hour = now.Hour % 12 + now.Minute / 60f;
            var minute = now.Minute + now.Second / 60f;
            var second = now.Second + now.Millisecond / 1000f;
            DrawHand(g, cx, cy, hour * 30f, radius * 0.5f, fg, 3.2f);
            DrawHand(g, cx, cy, minute * 6f, radius * 0.72f, fg, 2.2f);
            DrawHand(g, cx, cy, second * 6f, radius * 0.8f, accent, 1.2f);
            using (var br = new SolidBrush(accent))
                g.FillEllipse(br, cx - 3, cy - 3, 6, 6);

            using var big = new Font("Segoe UI Semibold", 22f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sub = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(fg);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var digital = now.ToString(_timeFormat, CultureInfo.CurrentCulture);
            g.DrawString(digital, big, brush, new RectangleF(0, h * 0.72f, w, 32), sf);
            if (!string.IsNullOrEmpty(_line2))
                g.DrawString(_line2, sub, brush, new RectangleF(0, h * 0.84f, w, 24), sf);

            using var screen = Graphics.FromHdc(hdc);
            screen.DrawImageUnscaled(bmp, 0, 0);
        }
        finally { EndPaint(hWnd, ref ps); }
    }

    private static void DrawHand(Graphics g, float cx, float cy, float deg, float len, Color color, float width)
    {
        var ang = deg * Math.PI / 180.0;
        var x = cx + (float)Math.Sin(ang) * len;
        var y = cy - (float)Math.Cos(ang) * len;
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx, cy, x, y);
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        path.AddArc(x, y + h - r, r, r, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void RegisterClass(string name, WndProc proc)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            lpszClassName = name,
            hbrBackground = IntPtr.Zero,
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 1410) Log($"RegisterClass {name} failed: {err}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { SystemEvents.DisplaySettingsChanged -= OnSystemChanged; } catch { /* ignore */ }
        try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { /* ignore */ }
        try { _timer.Dispose(); } catch { /* ignore */ }
        HideHover();
        if (_hoverHwnd != IntPtr.Zero) { DestroyWindow(_hoverHwnd); _hoverHwnd = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
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

    private const int IDM_COLOR_LIGHT = 1001;
    private const int IDM_COLOR_DARK = 1002;
    private const int IDM_COLOR_AUTO = 1003;
    private const int IDM_THEME_SYSTEM = 1101;
    private const int IDM_THEME_DEFAULT = 1102;
    private const int IDM_THEME_INK = 1103;
    private const int IDM_THEME_RED = 1104;
    private const int IDM_THEME_GOLD = 1105;
    private const int IDM_THEME_CYAN = 1106;
    private const int IDM_THEME_AUTO = 1107;
    private const int IDM_SETTINGS = 1201;
    private const int IDM_EXIT = 1299;

    private void ShowTrayContextMenu()
    {
        var hMenu = CreatePopupMenu();
        var hTheme = CreatePopupMenu();
        var hColor = CreatePopupMenu();
        if (hMenu == IntPtr.Zero || hTheme == IntPtr.Zero || hColor == IntPtr.Zero) return;

        AppendMenu(hColor, MF_STRING, (UIntPtr)IDM_COLOR_LIGHT, "日间模式");
        AppendMenu(hColor, MF_STRING, (UIntPtr)IDM_COLOR_DARK, "夜间模式");
        AppendMenu(hColor, MF_STRING, (UIntPtr)IDM_COLOR_AUTO, "跟随系统");

        AppendMenu(hTheme, MF_POPUP, (UIntPtr)(ulong)hColor.ToInt64(), "显示模式");
        AppendMenu(hTheme, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
        AppendMenu(hTheme, MF_STRING, (UIntPtr)IDM_THEME_SYSTEM, "系统简约");
        AppendMenu(hTheme, MF_STRING, (UIntPtr)IDM_THEME_DEFAULT, "素雅");
        AppendMenu(hTheme, MF_STRING, (UIntPtr)IDM_THEME_INK, "水墨");
        AppendMenu(hTheme, MF_STRING, (UIntPtr)IDM_THEME_RED, "朱红");
        AppendMenu(hTheme, MF_STRING, (UIntPtr)IDM_THEME_GOLD, "鎏金");
        AppendMenu(hTheme, MF_STRING, (UIntPtr)IDM_THEME_CYAN, "黛蓝");
        AppendMenu(hTheme, MF_STRING, (UIntPtr)IDM_THEME_AUTO, "节气自动");

        AppendMenu(hMenu, MF_POPUP, (UIntPtr)(ulong)hTheme.ToInt64(), "调色盘");
        AppendMenu(hMenu, MF_STRING, (UIntPtr)IDM_SETTINGS, "设置");
        AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
        AppendMenu(hMenu, MF_STRING, (UIntPtr)IDM_EXIT, "退出合社日历");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = (int)TrackPopupMenu(
            hMenu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
            pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(hMenu);

        if (cmd == 0) return;
        _flyout.DispatcherQueue?.TryEnqueue(() => HandleTrayMenuCommand(cmd));
    }

    private void HandleTrayMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case IDM_COLOR_LIGHT:
                _flyout.PostToWeb("{\"type\":\"shell.setColorMode\",\"mode\":\"light\"}");
                break;
            case IDM_COLOR_DARK:
                _flyout.PostToWeb("{\"type\":\"shell.setColorMode\",\"mode\":\"dark\"}");
                break;
            case IDM_COLOR_AUTO:
                _flyout.PostToWeb("{\"type\":\"shell.setColorMode\",\"mode\":\"auto\"}");
                break;
            case IDM_THEME_SYSTEM:
                _flyout.PostToWeb("{\"type\":\"shell.setTheme\",\"themeId\":\"system-minimal\"}");
                break;
            case IDM_THEME_DEFAULT:
                _flyout.PostToWeb("{\"type\":\"shell.setTheme\",\"themeId\":\"default\"}");
                break;
            case IDM_THEME_INK:
                _flyout.PostToWeb("{\"type\":\"shell.setTheme\",\"themeId\":\"ink\"}");
                break;
            case IDM_THEME_RED:
                _flyout.PostToWeb("{\"type\":\"shell.setTheme\",\"themeId\":\"red\"}");
                break;
            case IDM_THEME_GOLD:
                _flyout.PostToWeb("{\"type\":\"shell.setTheme\",\"themeId\":\"gold\"}");
                break;
            case IDM_THEME_CYAN:
                _flyout.PostToWeb("{\"type\":\"shell.setTheme\",\"themeId\":\"cyan\"}");
                break;
            case IDM_THEME_AUTO:
                _flyout.PostToWeb("{\"type\":\"shell.setTheme\",\"themeId\":\"auto\"}");
                break;
            case IDM_SETTINGS:
                _flyout.OpenWebPanel("settings");
                _flyout.BringToFront();
                break;
            case IDM_EXIT:
                App.CurrentApp?.RequestExit();
                break;
        }
    }

    private const int CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001, CS_DBLCLKS = 0x0008;
    private const int IDC_ARROW = 32512;
    private const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSEMOVE = 0x0200, WM_MOUSELEAVE = 0x02A3, WM_MOUSEHOVER = 0x02A1, WM_NULL = 0x0000;
    private const uint TME_HOVER = 0x00000001, TME_LEAVE = 0x00000002;
    private const uint MF_STRING = 0x00000000, MF_SEPARATOR = 0x00000800, MF_POPUP = 0x00000010;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080;
    private const int SW_HIDE = 0;
    private static readonly IntPtr HWND_TOP = new(0);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public int style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc; public bool fErase; public RECT rcPaint;
        public bool fRestore; public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? c, string? n);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
