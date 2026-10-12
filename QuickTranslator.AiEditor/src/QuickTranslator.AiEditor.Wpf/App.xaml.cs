using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
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
