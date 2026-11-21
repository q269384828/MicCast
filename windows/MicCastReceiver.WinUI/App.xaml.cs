using Microsoft.UI.Xaml;

namespace MicCastReceiver.WinUI;

public partial class App : Application
{
    private readonly StartOptions _options;
    private MainWindow? _window;

    public App(StartOptions options)
    {
        _options = options;
        InitializeComponent();

        // WinUI 的异常默认会被吞掉或只让窗口消失，必须自己记下来
        UnhandledException += (_, e) =>
        {
            Log($"UNHANDLED {e.Exception.GetType().Name}: {e.Message}`n{e.Exception.StackTrace}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow(_options);
            if (_options.Minimized)
            {
                _window.HideAtStartup();
            }
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log($"LAUNCH FAILED {ex.GetType().Name}: {ex.Message}`n{ex.StackTrace}");
            throw;
        }
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "winui-crash.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }
}