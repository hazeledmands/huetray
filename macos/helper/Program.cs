// hue-tray for macOS: the terminal commands (see core/Commands.cs). Lives inside Hue Tray.app,
// which also runs it as its helper. See README.md in the repo root.

using System.Security;

namespace HueTray;

static class Program
{
    static int Main(string[] args) =>
        Commands.Run(args, "Hue Tray.app", on => LoginItem.Enabled = on).GetAwaiter().GetResult();
}

/// Starts Hue Tray.app at login with a LaunchAgent. The menu bar app's Start at Login item runs
/// `hue-tray login on|off` and checks for the same file.
static class LoginItem
{
    const string Label = "io.github.hazeledmands.huetray";

    static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                            "Library", "LaunchAgents", Label + ".plist");

    // hue-tray is in Hue Tray.app/Contents/MacOS.
    static string AppPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));

    public static bool Enabled
    {
        get => File.Exists(PlistPath);
        set
        {
            if (!value)
            {
                File.Delete(PlistPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>{Label}</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>/usr/bin/open</string>
                        <string>-a</string>
                        <string>{SecurityElement.Escape(AppPath)}</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """);
        }
    }
}
