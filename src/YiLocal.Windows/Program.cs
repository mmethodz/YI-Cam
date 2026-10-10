namespace YiLocal.Windows;

static class Program
{
    [STAThread]
    static void Main()
    {
        L.Initialize(Preferences.Load(out _).Language);
        using var single = new Mutex(true, "YILocal.Windows.SingleInstance", out bool first);
        if (!first) { MessageBox.Show(L.Get("OpenYIIsAlreadyRunning"), "OpenYI"); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
