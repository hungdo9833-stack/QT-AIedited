using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;

    /// <summary>
    /// Test and smoke hosts construct <see cref="App"/> only for its XAML resources. WPF still
    /// dispatches OnStartup once the dispatcher pumps, which would otherwise create a real
    /// MainWindow on the user's real stores and auto-open their most recent Project.
    /// </summary>
    internal static bool SuppressStartup { get; set; }
    internal static bool RealStartupRan { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (SuppressStartup) return;
        RealStartupRan = true;
        _instance = new Mutex(false, @"Local\QuickTranslatorAiEditor.Phase1");
        try { _ownsInstance = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstance = true; }
        if (!_ownsInstance)
        {
            MessageBox.Show("AI Editor đang mở. Hãy dùng cửa sổ đó để tránh hai ứng dụng ghi đè cùng tệp tiến độ.", "QuickTranslator AI Editor");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
