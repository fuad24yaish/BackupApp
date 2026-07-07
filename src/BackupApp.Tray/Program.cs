namespace BackupApp.Tray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "BackupApp.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("BackupApp is already running — look for its icon in the system tray.",
                "BackupApp", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayAppContext();
        Application.Run(context);
    }
}
