using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace HeCalendar.Shell;

/// <summary>
/// Separate almanac pane that slides in to the left of the main calendar flyout.
/// </summary>
public sealed partial class AlmanacWindow : Window
{
    public const int LogicalWidth = 400;
    public const int GapPx = 5;

    private readonly string _userDataFolder;
    private double _webDpr;
    private int _frameW;
    private int _frameH;
    private bool _chromeStripped;
    private bool _animating;

    public bool VisibleFlag { get; private set; }

    public AlmanacWindow(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        InitializeComponent();
        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;
        ConfigureChrome();
        _ = InitWebAsync();
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeCalendar");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "shell.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [Almanac] {message}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    private void ConfigureChrome()
    {
        SystemBackdrop = null;
        Title = "";
        ExtendsContentIntoTitleBar = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.Title = "";
        AppWindow.IsShownInSwitchers = false;

        try
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        }
        catch { /* ignore */ }

        VisibleFlag = false;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            PlaceOffscreen(hwnd);
        }
        catch
        {
            try { AppWindow.Hide(); } catch { /* ignore */ }
        }
        Log($"Configured logical {LogicalWidth}px");
    }

    private double RasterScale(IntPtr hwnd)
    {
        if (_webDpr >= 1.0) return _webDpr;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi <= 0) dpi = 96;
        return dpi / 96.0;
    }

    private (int w, int h) EnsureFrameSize(IntPtr hwnd, int logicalHeight)
    {
        if (_frameW > 0 && _frameH > 0) return (_frameW, _frameH);

        if (!_chromeStripped)
        {
            StripChrome(hwnd);
            _chromeStripped = true;
        }

        var scale = RasterScale(hwnd);
        var needW = Math.Max(240, (int)Math.Round(LogicalWidth * scale));
        var needH = Math.Max(400, (int)Math.Round(logicalHeight * scale));
        _frameW = needW;
        _frameH = needH;
        Log($"FrameSize {needW}x{needH} scale={scale:F3}");
        return (_frameW, _frameH);
    }

    private void PlaceOffscreen(IntPtr hwnd)
    {
        EnsureFrameSize(hwnd, 580);
        SetWindowPos(hwnd, HWND_BOTTOM, -32000, -32000, _frameW, _frameH, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ClipWindowToClient(hwnd);
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);

            var core = WebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += async (_, e) =>
            {
                Log($"Nav ok={e.IsSuccess}");
                if (!e.IsSuccess) return;
                try
                {
                    var json = await core.ExecuteScriptAsync("window.devicePixelRatio");
                    if (double.TryParse(json?.Trim('"'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var dpr) && dpr >= 1)
                        _webDpr = dpr;
                }
                catch { /* ignore */ }
            };

            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                (() => {
                  const s = document.createElement('style');
                  s.textContent = 'html,body,#app{margin:0!important;background:#202020!important;}';
                  document.documentElement.appendChild(s);
                  document.documentElement.dataset.shell = 'winui-almanac';
                })();
                """);

            var dist = Path.Combine(AppContext.BaseDirectory, "WebAssets", "index.html");
            if (File.Exists(dist))
            {
                var dir = Path.GetDirectoryName(dist)!;
                core.SetVirtualHostNameToFolderMapping(
                    "hecalendar.local",
                    dir,
                    CoreWebView2HostResourceAccessKind.Allow);
                core.Navigate("http://hecalendar.local/index.html?mode=almanac");
            }
            else
            {
                core.Navigate("http://localhost:5173/?mode=almanac");
            }
        }
        catch (Exception ex)
        {
            Log($"InitWeb failed: {ex}");
        }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var json = args.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(json)) return;
            if (json.Contains("\"type\":\"diag.error\"", StringComparison.Ordinal))
            {
                Log($"JS {json}");
                return;
            }

            var response = BridgeRouter.Handle(json);
            if (response != null)
                sender.PostWebMessageAsString(response);
        }
        catch (Exception ex)
        {
            Log($"OnWebMessage failed: {ex.Message}");
            Debug.WriteLine(ex);
        }
    }

    public IntPtr GetHwnd() => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public void SyncDate(string? dateYmd)
    {
        if (string.IsNullOrWhiteSpace(dateYmd)) return;
        var safe = dateYmd.Replace("\\", "\\\\").Replace("\"", "\\\"");
        SyncRaw($"{{\"type\":\"shell.setDate\",\"date\":\"{safe}\"}}");
    }

    public void SyncRaw(string json)
    {
        try { WebView.CoreWebView2?.PostWebMessageAsString(json); }
        catch (Exception ex) { Log($"SyncRaw failed: {ex.Message}"); }
    }

    public void ShowBeside(IntPtr mainHwnd, int mainLogicalHeight, string? dateYmd)
    {
        if (_animating) return;
        var hwnd = GetHwnd();
        if (!GetWindowRect(mainHwnd, out var main)) return;

        // Match main window height in physical pixels.
        var mainH = Math.Max(1, main.Bottom - main.Top);
        _frameH = 0;
        _frameW = 0;
        var scale = RasterScale(hwnd);
        var w = Math.Max(240, (int)Math.Round(LogicalWidth * scale));
        var h = mainH;
        _frameW = w;
        _frameH = h;

        if (!_chromeStripped)
        {
            StripChrome(hwnd);
            _chromeStripped = true;
        }

        var gap = Math.Max(1, (int)Math.Round(GapPx * scale));
        var targetX = main.Left - w - gap;
        var targetY = main.Top;
        var startX = targetX + Math.Min(w / 3, (int)(48 * scale));

        SyncDate(dateYmd);
        AnimateShow(hwnd, startX, targetX, targetY, w, h);
    }

    public void HideAnimated()
    {
        if (_animating || !VisibleFlag) return;
        _ = AnimateHideAsync();
    }

    public void HideImmediate()
    {
        VisibleFlag = false;
        try
        {
            var hwnd = GetHwnd();
            PlaceOffscreen(hwnd);
            AppWindow.Hide();
        }
        catch { /* ignore */ }
    }

    private void AnimateShow(IntPtr hwnd, int xStart, int xFinal, int y, int w, int h)
    {
        _animating = true;
        const uint SWP_NOSENDCHANGING = 0x0400;
        SetWindowPos(hwnd, HWND_TOPMOST, xStart, y, w, h,
            SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
        ClipWindowToClient(hwnd);
        try { AppWindow.Show(); } catch { /* ignore */ }
        VisibleFlag = true;

        var sw = Stopwatch.StartNew();
        const int durationMs = 180;
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
            var eased = 1 - Math.Pow(1 - t, 3);
            var x = (int)Math.Round(xStart + (xFinal - xStart) * eased);
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h,
                SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            if (t < 1) return;
            timer.Stop();
            SetWindowPos(hwnd, HWND_TOPMOST, xFinal, y, w, h,
                SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
            ClipWindowToClient(hwnd);
            _animating = false;
            Log($"Shown at {xFinal},{y} size={w}x{h}");
        };
        timer.Start();
    }

    private async Task AnimateHideAsync()
    {
        _animating = true;
        VisibleFlag = false;
        try
        {
            var hwnd = GetHwnd();
            if (!GetWindowRect(hwnd, out var wr))
            {
                HideImmediate();
                return;
            }

            var w = wr.Right - wr.Left;
            var h = wr.Bottom - wr.Top;
            var xStart = wr.Left;
            var y = wr.Top;
            var xEnd = xStart + Math.Min(w / 3, 80);
            const uint SWP_NOSENDCHANGING = 0x0400;

            var sw = Stopwatch.StartNew();
            const int durationMs = 140;
            var tcs = new TaskCompletionSource();
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = true;
            timer.Interval = TimeSpan.FromMilliseconds(8);
            timer.Tick += (_, _) =>
            {
                var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
                var eased = t * t;
                var x = (int)Math.Round(xStart + (xEnd - xStart) * eased);
                SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                if (t < 1) return;
                timer.Stop();
                tcs.TrySetResult();
            };
            timer.Start();
            await tcs.Task;
            PlaceOffscreen(hwnd);
            try { AppWindow.Hide(); } catch { /* ignore */ }
            Log("Hidden");
        }
        catch (Exception ex)
        {
            Log($"Hide failed: {ex.Message}");
            HideImmediate();
        }
        finally
        {
            _animating = false;
        }
    }

    public void PrepareExit()
    {
        try { HideImmediate(); } catch { /* ignore */ }
        try
        {
            if (WebView.CoreWebView2 != null)
                WebView.Close();
        }
        catch { /* ignore */ }
    }

    private static void StripChrome(IntPtr hwnd)
    {
        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_VISIBLE = 0x10000000;
        const int WS_CLIPSIBLINGS = 0x04000000;
        const int WS_CLIPCHILDREN = 0x02000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_WINDOWEDGE = 0x00000100;
        const int WS_EX_CLIENTEDGE = 0x00000200;
        const int WS_EX_DLGMODALFRAME = 0x00000001;
        const int WS_EX_STATICEDGE = 0x00020000;
        const int DWMWA_NCRENDERING_POLICY = 2;
        const int DWMNCRP_DISABLED = 1;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_CAPTION_COLOR = 35;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_DONOTROUND = 1;
        const int SWP_FRAMECHANGED = 0x0020;
        const int SWP_NOZORDER = 0x0004;
        const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        _ = SetWindowLong(hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN);
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        var ncrp = DWMNCRP_DISABLED;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref ncrp, sizeof(int));
        var corner = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var colorNone = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        var backdrop = 1; // NONE
        _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        var margins = new MARGINS();
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private static void ClipWindowToClient(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var client)) return;
        if (!GetWindowRect(hwnd, out var window)) return;
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return;
        var left = origin.X - window.Left;
        var top = origin.Y - window.Top;
        var right = left + Math.Max(1, client.Right - client.Left);
        var bottom = top + Math.Max(1, client.Bottom - client.Top);
        var rgn = CreateRectRgn(left, top, right, bottom);
        if (rgn != IntPtr.Zero)
            _ = SetWindowRgn(hwnd, rgn, true);
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }
}
