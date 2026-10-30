// Hue Tray for Windows: control Philips Hue lights from the notification area, and let other
// programs control them through a local HTTP API. See README.md in the repo root.
//
// Builds as two programs:
//   HueTray.exe              the tray app (what runs at login)
//   hue-tray.exe <command>   the terminal commands (see core/Commands.cs)

namespace HueTray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
#if CLI
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        return Commands.Run(args, "HueTray.exe", on => LoginItem.Enabled = on).GetAwaiter().GetResult();
#else
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        // Only one copy at a time. (A second couldn't open the API port anyway.)
        using var mutex = new Mutex(true, @"Local\hue-tray", out bool first);
        if (!first) return 0;
        Application.Run(new TrayApp());
        return 0;
#endif
    }
}

static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Hue Tray";

    public static string TrayExe => Path.Combine(AppContext.BaseDirectory, "HueTray.exe");

    public static bool Enabled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        set
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{TrayExe}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
