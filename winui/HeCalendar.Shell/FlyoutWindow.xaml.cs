using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace HeCalendar.Shell;

public sealed partial class FlyoutWindow : Window
{
    private AlmanacWindow? _almanac;
    private readonly string _webUserData;

    public FlyoutWindow()
    {
        _webUserData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeCalendar", "WebView2");
        InitializeComponent();
        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;
        ConfigureFlyoutChrome();
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
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    private void ConfigureFlyoutChrome()
    {
        // Borderless flyout — no title text / caption buttons.
        SystemBackdrop = null;
        Title = "";
        // Do NOT ExtendsContentIntoTitleBar: it reserves a caption strip even when collapsed.
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
        catch (Exception ex)
        {
            Log($"TitleBar collapse failed: {ex.Message}");
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            try { AppWindow.SetIcon(iconPath); } catch (Exception ex) { Log($"SetIcon failed: {ex.Message}"); }
        }

        VisibleFlag = false;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ApplyFlyoutFrame(hwnd, -32000, -32000, topMost: false);
        }
        catch
        {
            AppWindow.Hide();
        }
        Log($"Flyout configured (logical {FlyoutWidth}x{FlyoutHeight}). BaseDir={AppContext.BaseDirectory}");
    }

    // Calendar-only main frame; almanac opens as a separate sliding window on the left.
    private const int FlyoutWidth = 470;
    private const int FlyoutHeight = 500;
    private bool _chromeStripped;
    private double _webDpr;
    private bool _almanacOpen;

    private int LogicalFlyoutWidth => FlyoutWidth;

    private double RasterScale(IntPtr hwnd)
    {
        if (_webDpr >= 1.0) return _webDpr;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi <= 0) dpi = 96;
        return dpi / 96.0;
    }

    private (int w, int h) PhysicalSizeFor(IntPtr hwnd)
    {
        var scale = RasterScale(hwnd);
        return (
            Math.Max(320, (int)Math.Round(LogicalFlyoutWidth * scale)),
            Math.Max(400, (int)Math.Round(FlyoutHeight * scale)));
    }

    private int _frameW;
    private int _frameH;

    private void ResetFrameCache()
    {
        _frameW = 0;
        _frameH = 0;
    }

    private (int w, int h) EnsureFrameSize(IntPtr hwnd)
    {
        if (_frameW > 0 && _frameH > 0) return (_frameW, _frameH);

        if (!_chromeStripped)
        {
            StripFlyoutChrome(hwnd);
            _chromeStripped = true;
        }

        var (needW, needH) = PhysicalSizeFor(hwnd);

        if (VisibleFlag)
        {
            _frameW = needW;
            _frameH = needH;
            Log($"FrameSize cached (live) outer={needW}x{needH}");
            return (needW, needH);
        }

        var w = needW;
        var h = needH;
        SetWindowPos(hwnd, HWND_BOTTOM, -32000, -32000, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        for (var i = 0; i < 4; i++)
        {
            if (!GetClientRect(hwnd, out var rc)) break;
            var cw = rc.Right - rc.Left;
            var ch = rc.Bottom - rc.Top;
            if (cw >= needW - 1 && ch >= needH - 1) break;
            w += Math.Max(0, needW - cw);
            h += Math.Max(0, needH - ch);
            SetWindowPos(hwnd, HWND_BOTTOM, -32000, -32000, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        _frameW = w;
        _frameH = h;
        Log($"FrameSize cached outer={w}x{h} need={needW}x{needH}");
        return (w, h);
    }

    private void PlaceFrame(IntPtr hwnd, int x, int y, bool topMost, bool copyBits = false)
    {
        var (w, h) = EnsureFrameSize(hwnd);
        var after = topMost ? HWND_TOPMOST : HWND_BOTTOM;
        const uint SWP_NOSENDCHANGING = 0x0400;
        const uint SWP_NOCOPYBITS = 0x0100;
        var flags = SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING;
        if (!copyBits) flags |= SWP_NOCOPYBITS;
        SetWindowPos(hwnd, after, x, y, w, h, flags);
        ClipWindowToClient(hwnd, redraw: !copyBits);
    }

    private static void ClipWindowToClient(IntPtr hwnd, bool redraw = true)
    {
        // SetWindowRgn is window-relative (includes NC). Clip strictly to the client
        // so any leftover caption/border strip on top/left cannot paint a white rim.
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
            _ = SetWindowRgn(hwnd, rgn, redraw);
    }

    private void ApplyFlyoutFrame(IntPtr hwnd, int x, int y, bool topMost)
    {
        PlaceFrame(hwnd, x, y, topMost, copyBits: false);
    }

    private static void StripFlyoutChrome(IntPtr hwnd)
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
        const int DWMWA_TEXT_COLOR = 36;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_DONOTROUND = 1;
        const int SWP_FRAMECHANGED = 0x0020;
        const int SWP_NOZORDER = 0x0004;

        var style = WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        _ = SetWindowLong(hwnd, GWL_STYLE, style);

        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        // NOREDIRECTIONBITMAP lets unpainted regions (the 5px gap) stay truly transparent.
        const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        ex |= WS_EX_NOREDIRECTIONBITMAP;
        ex &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, ex);

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Disable NC rendering; COLOR_NONE removes the Win11 light border stroke.
        var ncrp = DWMNCRP_DISABLED;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref ncrp, sizeof(int));
        var corner = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        // DWMWA_COLOR_NONE = 0xFFFFFFFE — hide border/caption color stroke entirely.
        const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        var colorNone = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
        var darkText = 0x00202020;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref darkText, sizeof(int));
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        var immersive = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref immersive, sizeof(int));

        // Disable system backdrop (can paint a white rim under WebView).
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMSBT_NONE = 1;
        var backdrop = DWMSBT_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        // Keep frame out of client for opaque calendar pane.
        var margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);

            var core = WebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += (_, e) => Log($"WebView ProcessFailed kind={e.ProcessFailedKind}");

            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                (() => {
                  const s = document.createElement('style');
                  s.textContent = 'html,body,#app{margin:0!important;background:#202020!important;}';
                  document.documentElement.appendChild(s);
                })();
                window.addEventListener('error', e => {
                  chrome.webview.postMessage(JSON.stringify({type:'diag.error', message:String(e.message||e.error), source:e.filename, line:e.lineno}));
                });
                window.addEventListener('unhandledrejection', e => {
                  chrome.webview.postMessage(JSON.stringify({type:'diag.error', message:String(e.reason)}));
                });
                """);

            var dist = Path.Combine(AppContext.BaseDirectory, "WebAssets", "index.html");
            Log($"WebAssets index exists={File.Exists(dist)} path={dist}");
            if (File.Exists(dist))
            {
                var dir = Path.GetDirectoryName(dist)!;
                core.SetVirtualHostNameToFolderMapping(
                    "hecalendar.local",
                    dir,
                    CoreWebView2HostResourceAccessKind.Allow);
                var url = "http://hecalendar.local/index.html?mode=flyout";
                Log($"Navigate {url}");
                core.Navigate(url);
            }
            else
            {
                Log("WebAssets missing, fallback localhost:5173");
                core.Navigate("http://localhost:5173/?mode=flyout");
            }

            // Pre-create almanac window so first open is snappy.
            EnsureAlmanacWindow();
        }
        catch (Exception ex)
        {
            Log($"InitWebAsync failed: {ex}");
        }
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        Log($"NavigationCompleted ok={args.IsSuccess} status={args.WebErrorStatus} url={sender.Source}");
        if (!args.IsSuccess) return;
        try
        {
            var result = await sender.ExecuteScriptAsync("""
                (() => {
                  const app = document.getElementById('app');
                  return JSON.stringify({
                    title: document.title,
                    readyState: document.readyState,
                    appHtmlLen: app ? app.innerHTML.length : -1,
                    viewport: [window.innerWidth, window.innerHeight],
                    dpr: window.devicePixelRatio,
                    flyout: document.body.classList.contains('is-flyout')
                  });
                })()
                """);
            Log($"DOM probe {result}");
            TryCaptureDpr(result);
            if (_webDpr >= 1.0) ResetFrameCache();
            // Size only — do not move on-screen window here (avoids post-anim jitter).
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                _ = EnsureFrameSize(hwnd);
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            Log($"DOM probe failed: {ex.Message}");
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

            if (json.Contains("\"type\":\"shell.setAlmanacExpanded\"", StringComparison.Ordinal))
            {
                var expanded = false;
                string? date = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("expanded", out var el))
                        expanded = el.ValueKind == System.Text.Json.JsonValueKind.True;
                    if (doc.RootElement.TryGetProperty("date", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
                        date = d.GetString();
                }
                catch { /* ignore */ }
                SetAlmanacExpanded(expanded, date);
            }

            if (response != null)
            {
                sender.PostWebMessageAsString(response);
            }

            if (json.Contains("\"type\":\"shell.hide\"", StringComparison.Ordinal))
            {
                DispatcherQueue.TryEnqueue(HideFlyout);
            }
            else if (json.Contains("\"type\":\"shell.exit\"", StringComparison.Ordinal))
            {
                DispatcherQueue.TryEnqueue(() => App.CurrentApp?.RequestExit());
            }
            else if (json.Contains("\"type\":\"shell.syncAlmanacDate\"", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("date", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
                        _almanac?.SyncDate(d.GetString());
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Log($"OnWebMessage failed: {ex.Message}");
            Debug.WriteLine(ex);
        }
    }

    /// <summary>Thread-safe visibility flag for hooks running off the UI thread.</summary>
    public bool VisibleFlag { get; private set; }

    /// <summary>UTC time when the flyout was last shown (for outside-click grace).</summary>
    public DateTime ShownAtUtc { get; private set; } = DateTime.MinValue;

    private int _anchorX;
    private int _anchorY;
    private bool _animating;

    public void SetAlmanacExpanded(bool expanded, string? dateYmd = null)
    {
        EnsureAlmanacWindow();
        if (_almanac == null) return;

        _almanacOpen = expanded;
        if (!expanded)
        {
            _almanac.HideAnimated();
            try
            {
                WebView.CoreWebView2?.PostWebMessageAsString("{\"type\":\"almanac.collapse\"}");
            }
            catch { /* ignore */ }
            Log("AlmanacExpanded=false");
            return;
        }

        if (!VisibleFlag)
        {
            Log("AlmanacExpanded ignored — main flyout hidden");
            return;
        }

        var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _almanac.ShowBeside(mainHwnd, FlyoutHeight, dateYmd);
        Log($"AlmanacExpanded=true date={dateYmd}");
    }

    private void EnsureAlmanacWindow()
    {
        if (_almanac != null) return;
        try
        {
            _almanac = new AlmanacWindow(_webUserData);
            Log("AlmanacWindow created");
        }
        catch (Exception ex)
        {
            Log($"AlmanacWindow create failed: {ex.Message}");
        }
    }

    public bool IsPointInShellWindows(int x, int y)
    {
        var main = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (GetWindowRect(main, out var mr) &&
            x >= mr.Left && x <= mr.Right && y >= mr.Top && y <= mr.Bottom)
            return true;

        if (_almanac != null && _almanac.VisibleFlag)
        {
            var ah = _almanac.GetHwnd();
            if (GetWindowRect(ah, out var ar) &&
                x >= ar.Left && x <= ar.Right && y >= ar.Top && y <= ar.Bottom)
                return true;
        }
        return false;
    }

    /// <summary>Show flyout (if needed) and ask the web UI to open a panel.</summary>
    public void OpenWebPanel(string panel)
    {
        var wasHidden = !VisibleFlag;
        if (wasHidden)
            ShowFlyoutNearClock();

        void Send()
        {
            try
            {
                WebView.CoreWebView2?.PostWebMessageAsString(
                    $"{{\"type\":\"shell.openPanel\",\"panel\":\"{panel}\"}}");
            }
            catch (Exception ex)
            {
                Log($"OpenWebPanel failed: {ex.Message}");
            }
        }

        if (!wasHidden)
        {
            Send();
            return;
        }

        // Wait for slide-in so the panel anchors against a settled frame.
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(220);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Send();
        };
        timer.Start();
    }

    public void PostToWeb(string json)
    {
        try { WebView.CoreWebView2?.PostWebMessageAsString(json); }
        catch (Exception ex) { Log($"PostToWeb failed: {ex.Message}"); }
        try { _almanac?.SyncRaw(json); } catch { /* ignore */ }
    }

    public void ShowFlyoutNearClock()
    {
        if (_animating) return;
        CollapseAlmanacForNextShow();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var (w, h) = EnsureFrameSize(hwnd);
        ComputeAnchor(w, h, out var x, out var y);
        _anchorX = x;
        _anchorY = y;
        AnimateShow(x, y, w, h);
    }

    private void AnimateShow(int x, int yFinal, int w, int h)
    {
        _animating = true;
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            presenter?.SetBorderAndTitleBar(false, false);
        }
        catch { /* ignore */ }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Short travel (~28% height) feels snappier and less stuttery than full height.
        var yStart = yFinal + Math.Max(48, (int)(h * 0.28));
        const uint SWP_NOSENDCHANGING = 0x0400;
        const uint SWP_NOCOPYBITS = 0x0100;
        SetWindowPos(hwnd, HWND_TOPMOST, x, yStart, w, h,
            SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_NOCOPYBITS);
        ClipWindowToClient(hwnd);
        try { AppWindow.Show(); } catch { /* ignore */ }

        VisibleFlag = true;
        ShownAtUtc = DateTime.UtcNow;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int durationMs = 160;
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
            var eased = 1 - Math.Pow(1 - t, 3);
            var y = (int)Math.Round(yStart + (yFinal - yStart) * eased);
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h,
                SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_NOCOPYBITS);
            if (t < 1) return;
            timer.Stop();
            SetWindowPos(hwnd, HWND_TOPMOST, x, yFinal, w, h,
                SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_NOCOPYBITS);
            try { Activate(); } catch { /* ignore */ }
            BringToFront();
            _animating = false;
            Log($"ShowFlyoutNearClock at {x},{yFinal} size={w}x{h} (slide-up {durationMs}ms)");
        };
        timer.Start();
    }

    private async Task ProbeViewportAsync()
    {
        try
        {
            if (WebView.CoreWebView2 == null) return;
            var result = await WebView.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({vw:window.innerWidth,vh:window.innerHeight,dpr:window.devicePixelRatio})");
            Log($"Viewport probe {result}");
            var prev = _webDpr;
            TryCaptureDpr(result);
            if (_webDpr >= 1.0 && Math.Abs(_webDpr - prev) > 0.01)
                ResetFrameCache();
        }
        catch (Exception ex)
        {
            Log($"Viewport probe failed: {ex.Message}");
        }
    }

    private void TryCaptureDpr(string jsonish)
    {
        try
        {
            var s = jsonish.Trim();
            if (s.StartsWith('"') && s.EndsWith('"'))
                s = System.Text.Json.JsonSerializer.Deserialize<string>(s) ?? s;
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            if (doc.RootElement.TryGetProperty("dpr", out var dprEl) && dprEl.TryGetDouble(out var dpr) && dpr >= 1.0)
            {
                if (Math.Abs(dpr - _webDpr) > 0.01)
                    Log($"WebView DPR captured {_webDpr:F3} -> {dpr:F3}");
                _webDpr = dpr;
            }
        }
        catch (Exception ex)
        {
            Log($"TryCaptureDpr failed: {ex.Message}");
        }
    }

    private void LogClientSize(IntPtr hwnd)
    {
        if (GetClientRect(hwnd, out var rc))
            Log($"Win32 client={rc.Right - rc.Left}x{rc.Bottom - rc.Top} window={AppWindow.Size.Width}x{AppWindow.Size.Height}");
    }

    public void BringToFront()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        catch (Exception ex)
        {
            Log($"BringToFront failed: {ex.Message}");
        }
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
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    public void HideFlyout()
    {
        if (_animating) return;
        if (!VisibleFlag && AppWindow.Position.X <= -30000) return;
        CollapseAlmanacForNextShow();
        _ = AnimateHideAsync();
    }

    private void CollapseAlmanacForNextShow()
    {
        _almanacOpen = false;
        try { _almanac?.HideImmediate(); } catch { /* ignore */ }
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsString("{\"type\":\"almanac.collapse\"}");
        }
        catch { /* ignore */ }
    }

    private async Task AnimateHideAsync()
    {
        _animating = true;
        VisibleFlag = false;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var (w, h) = EnsureFrameSize(hwnd);
            var x = _anchorX;
            var yStart = _anchorY;
            var yEnd = yStart + Math.Max(48, (int)(h * 0.28));
            const uint SWP_NOSENDCHANGING = 0x0400;
            const uint SWP_NOCOPYBITS = 0x0100;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int durationMs = 120;
            var tcs = new TaskCompletionSource();
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = true;
            timer.Interval = TimeSpan.FromMilliseconds(8);
            timer.Tick += (_, _) =>
            {
                var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
                var eased = t * t;
                var y = (int)Math.Round(yStart + (yEnd - yStart) * eased);
                SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_NOCOPYBITS);
                if (t < 1) return;
                timer.Stop();
                tcs.TrySetResult();
            };
            timer.Start();
            await tcs.Task;

            PlaceFrame(hwnd, -32000, -32000, topMost: false);
            Log("HideFlyout (slide-down)");
        }
        catch (Exception ex)
        {
            Log($"AnimateHide failed: {ex.Message}");
            try { AppWindow.Hide(); } catch { /* ignore */ }
        }
        finally
        {
            _animating = false;
        }
    }

    private void ComputeAnchor(int width, int height, out int x, out int y)
    {
        var point = GetCursorPoint();
        var display = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);
        var work = display.WorkArea;

        x = work.X + work.Width - width - 16;
        y = work.Y + work.Height - height - 16;

        if (x < work.X + 8) x = work.X + 8;
        if (y < work.Y + 8) y = work.Y + 8;
        if (x + width > work.X + work.Width) x = work.X + Math.Max(8, work.Width - width - 8);
        if (y + height > work.Y + work.Height) y = work.Y + Math.Max(8, work.Height - height - 8);
    }

    private void PositionNearTrayClock()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var (w, h) = EnsureFrameSize(hwnd);
        ComputeAnchor(w, h, out var x, out var y);
        PlaceFrame(hwnd, x, y, topMost: true);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private static PointInt32 GetCursorPoint()
    {
        if (GetCursorPos(out var p)) return new PointInt32(p.X, p.Y);
        return new PointInt32(0, 0);
    }

    public void PrepareExit()
    {
        try { HideFlyout(); } catch { /* ignore */ }
        try { _almanac?.PrepareExit(); } catch { /* ignore */ }
        _almanac = null;
        try
        {
            if (WebView.CoreWebView2 != null)
                WebView.Close();
        }
        catch { /* ignore */ }
    }
}
