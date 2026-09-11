using System.Threading;
using Microsoft.UI.Xaml;

namespace HeCalendar.Shell;

public partial class App : Application
{
    public static App? CurrentApp { get; private set; }

    private FlyoutWindow? _flyout;
    private Win32ClockOverlay? _clockOverlay;
    private CalendarFlyoutHook? _hook;
    private Mutex? _singleInstance;

    public App()
    {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HeCalendar");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "shell.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} UNHANDLED {e.Exception}{Environment.NewLine}");
            }
            catch { /* ignore */ }
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, @"Local\HeCalendar.Shell.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            try { _singleInstance.Dispose(); } catch { /* ignore */ }
            Environment.Exit(0);
            return;
        }

        TodoStore.Initialize();
        ReminderService.Start();

        _flyout = new FlyoutWindow();
        _flyout.HideFlyout();

        _clockOverlay = new Win32ClockOverlay(_flyout);

        _hook = new CalendarFlyoutHook(_flyout);
        _hook.Start();
    }

    public void RequestExit()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeCalendar");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "shell.log"),
                $"{DateTime.Now:HH:mm:ss.fff} RequestExit{Environment.NewLine}");
        }
        catch { /* ignore */ }

        try { _hook?.Dispose(); } catch { /* ignore */ }
        _hook = null;
        try { _clockOverlay?.Dispose(); } catch { /* ignore */ }
        _clockOverlay = null;
        try { _flyout?.PrepareExit(); } catch { /* ignore */ }
        _flyout = null;
        try { _singleInstance?.ReleaseMutex(); } catch { /* ignore */ }
        try { _singleInstance?.Dispose(); } catch { /* ignore */ }

        Environment.Exit(0);
    }
}
