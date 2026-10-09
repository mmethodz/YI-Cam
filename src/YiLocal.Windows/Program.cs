namespace YiLocal.Windows;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "YILocal.Windows.SingleInstance", out bool first);
        if (!first) { MessageBox.Show("YI Local is already running.", "YI Local"); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
