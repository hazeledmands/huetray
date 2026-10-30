using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using HueTray.Api;
using HueTray.Hue;

namespace HueTray;

/// The notification-area icon and its menu.
sealed class TrayApp : ApplicationContext
{
    static readonly int[] BrightnessPresets = { 10, 25, 50, 75, 100 };
    static readonly (string Name, int Kelvin)[] Temperatures = { ("Warm", 2700), ("Neutral", 4000), ("Cool", 6500) };

    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new();
    readonly TrayIcons icons = new();
    readonly SynchronizationContext ui;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 2000 };
    Config cfg = ConfigFile.Load();
    DateTime? configDate = ConfigFile.Modified();
    HueService hue = null!;
    ApiServer? api;
    string? apiError;
    List<BridgeInfo> foundBridges = new();
    bool pairing, refreshQueued;
    BridgeStatus shownStatus;

    public TrayApp()
    {
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("TrayApp needs a UI thread");
        tray = new NotifyIcon { ContextMenuStrip = menu, Text = "Hue Tray" };
        menu.Opening += (_, _) => BuildMenu();
        // Open the menu on a left click too, like a macOS menu bar item.
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(tray, null);
        };

        // Every 2 s: pick up hand edits to config.json, and redraw if the taskbar changed theme.
        poll.Tick += async (_, _) => await PollConfig();
        poll.Start();

        tray.Visible = true;
        _ = StartServices();
        if (!cfg.IsPaired) PairClicked();
    }

    async Task StartServices()
    {
        hue = new HueService(cfg);
        shownStatus = hue.Status;
        hue.State.Changed += _ => QueueRefresh();
        hue.StatusChanged += s => ui.Post(_ => StatusChanged(s), null);
        hue.Start();
        Refresh();
        if (!hue.IsPaired) return;

        api = new ApiServer(hue, cfg.ApiToken);
        try
        {
            await api.StartAsync(cfg.ApiPort);
            apiError = null;
        }
        catch (IOException e)
        {
            await api.DisposeAsync();
            api = null;
            apiError = $"Port {cfg.ApiPort} is in use";
            Log.Write($"Couldn't start the API: {e.Message}");
            Notify("The API isn't running", $"Port {cfg.ApiPort} is in use by another program. Change apiPort in the settings file.");
        }
        Refresh();
    }

    async Task StopServices()
    {
        if (api != null) await api.DisposeAsync();
        api = null;
        await hue.DisposeAsync();
    }

    async Task PollConfig()
    {
        var date = ConfigFile.Modified();
        if (date != configDate && !pairing)
        {
            configDate = date;
            cfg = ConfigFile.Load();
            await StopServices();
            await StartServices();
        }
        Refresh();
    }

    void StatusChanged(BridgeStatus status)
    {
        var was = shownStatus;
        shownStatus = status;
        if (status == BridgeStatus.Unreachable && was == BridgeStatus.Connected)
            Notify($"Lost {hue.BridgeLabel}", "Trying to reconnect…");
        else if (status == BridgeStatus.Connected && was == BridgeStatus.Unreachable)
            Notify($"{hue.BridgeLabel} is back", "The lights can be controlled again.");
        Refresh();
    }

    // State changes can arrive many times a second during a jog spin; redraw once per burst.
    void QueueRefresh()
    {
        lock (menu)
        {
            if (refreshQueued) return;
            refreshQueued = true;
        }
        ui.Post(_ =>
        {
            lock (menu) refreshQueued = false;
            Refresh();
        }, null);
    }

    void Refresh()
    {
        bool anyOn = hue.State.IsLoaded && (hue.State.Home()?.On ?? false);
        tray.Icon = icons.Get(anyOn, dim: hue.Status != BridgeStatus.Connected);
        var text = "Hue Tray: " + StatusText();
        tray.Text = text.Length > 127 ? text[..127] : text;
    }

    string StatusText()
    {
        if (pairing) return "Pairing…";
        return hue.Status switch
        {
            BridgeStatus.Unpaired => "Not paired with a bridge",
            BridgeStatus.Connecting => $"Connecting to {hue.BridgeLabel}…",
            BridgeStatus.Unreachable => $"Can't reach {hue.BridgeLabel}",
            _ => $"Connected to {hue.BridgeLabel}",
        };
    }

    // On Windows 10 and 11 these show as notifications. Showing a new one replaces the last.
    void Notify(string title, string body) => tray.ShowBalloonTip(5000, title, body, ToolTipIcon.None);

    // MARK: Menu

    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(StatusText()) { Enabled = false });
        if (apiError != null) menu.Items.Add(new ToolStripMenuItem($"API off: {apiError}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        if (hue.State.IsLoaded)
        {
            var state = hue.State;
            menu.Items.Add(ScenesMenu(state));
            var lights = state.Lights().ToDictionary(l => l.Id);
            foreach (var group in Shown(state.Rooms().Concat(state.Zones()), cfg.Menu.Rooms, g => g.Id, g => g.Name, showAllIfEmpty: true))
                menu.Items.Add(GroupMenu(group, lights));
            foreach (var light in Shown(lights.Values, cfg.Menu.Lights, l => l.Id, l => l.Name, showAllIfEmpty: false))
                menu.Items.Add(LightMenu(light));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("All Off", false, () => Do(hue.AllOff)));
            menu.Items.Add(new ToolStripSeparator());
        }

        if (foundBridges.Count > 1)
        {
            foreach (var b in foundBridges)
                menu.Items.Add(Item($"Pair with {b.Name} ({b.Ip})", false, () => PairWith(b)));
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem(pairing ? "Pairing…" : hue.IsPaired ? "Pair Another Bridge…" : "Pair Bridge…",
                                                 null, (_, _) => PairClicked()) { Enabled = !pairing });
        }
        menu.Items.Add(Item("Open Settings File", false, OpenSettings));
        menu.Items.Add(Item("Copy API Token", false, () => Clipboard.SetText(cfg.ApiToken)));
        menu.Items.Add(Item("Start at Login", LoginItem.Enabled, () => LoginItem.Enabled = !LoginItem.Enabled));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Quit Hue Tray", false, Quit));
    }

    /// The items named in `wanted` (by name or id), in that order; or all of them if it's empty and `showAllIfEmpty`.
    static IEnumerable<T> Shown<T>(IEnumerable<T> items, List<string> wanted, Func<T, string> id, Func<T, string> name, bool showAllIfEmpty)
    {
        if (wanted.Count == 0) return showAllIfEmpty ? items : Enumerable.Empty<T>();
        var list = items.ToList();
        return wanted.SelectMany(w => list.Where(i => id(i) == w || string.Equals(name(i), w, StringComparison.OrdinalIgnoreCase)));
    }

    ToolStripMenuItem ScenesMenu(HueState state)
    {
        var parent = new ToolStripMenuItem("Scenes");
        var scenes = Shown(state.Scenes(), cfg.Menu.Scenes, s => s.Id, s => s.Name, showAllIfEmpty: true);
        foreach (var group in scenes.GroupBy(s => s.Group ?? "Other").OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            if (parent.DropDownItems.Count > 0) parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add(new ToolStripMenuItem(group.Key) { Enabled = false });
            foreach (var scene in group)
                parent.DropDownItems.Add(Item("    " + scene.Name, scene.Active, () => Do(() => hue.ActivateScene(scene.Id))));
        }
        if (parent.DropDownItems.Count == 0) parent.DropDownItems.Add(new ToolStripMenuItem("No scenes") { Enabled = false });
        return parent;
    }

    ToolStripMenuItem GroupMenu(Group group, Dictionary<string, Light> lights)
    {
        var members = group.LightIds.Select(id => lights.GetValueOrDefault(id)).OfType<Light>().ToList();
        var parent = new ToolStripMenuItem(Title(group.Name, group.On, group.Brightness));
        AddControls(parent, group.On, group.Brightness, members.Any(l => l.SupportsColorTemp),
                    change => hue.SetGroup(group.Kind, group.Id, change));
        if (members.Count > 0)
        {
            parent.DropDownItems.Add(new ToolStripSeparator());
            foreach (var light in members) parent.DropDownItems.Add(LightMenu(light));
        }
        return parent;
    }

    ToolStripMenuItem LightMenu(Light light)
    {
        var parent = new ToolStripMenuItem(Title(light.Name, light.On, light.Brightness));
        AddControls(parent, light.On, light.Brightness, light.SupportsColorTemp, change => hue.SetLight(light.Id, change));
        return parent;
    }

    static string Title(string name, bool on, double? brightness) =>
        on ? $"{name}  ({brightness ?? 100:0}%)" : $"{name}  (off)";

    void AddControls(ToolStripMenuItem parent, bool on, double? brightness, bool colorTemp, Action<LightChange> apply)
    {
        var items = parent.DropDownItems;
        items.Add(Item("On", on, () => Do(() => apply(new LightChange { On = !on }))));
        items.Add(new ToolStripSeparator());
        foreach (var level in BrightnessPresets)
        {
            bool current = on && brightness is { } b && Math.Abs(b - level) < 3;
            items.Add(Item($"{level}%", current, () => Do(() => apply(new LightChange { Brightness = new Level(level, false) }))));
        }
        if (!colorTemp) return;
        items.Add(new ToolStripSeparator());
        foreach (var (name, kelvin) in Temperatures)
            items.Add(Item($"{name} ({kelvin} K)", false, () => Do(() => apply(new LightChange { Kelvin = kelvin }))));
    }

    void Do(Action action)
    {
        try { action(); }
        catch (HueException e) { Notify("Couldn't do that", e.Message); }
    }

    static ToolStripMenuItem Item(string title, bool check, Action action)
    {
        var item = new ToolStripMenuItem(title) { Checked = check };
        item.Click += (_, _) => action();
        return item;
    }

    // MARK: Pairing

    async void PairClicked()
    {
        if (pairing) return;
        pairing = true;
        Refresh();
        try
        {
            Notify("Looking for your Hue Bridge…", "This takes a few seconds.");
            var found = await Discovery.FindAsync();
            if (found.Count == 0 && cfg.BridgeIp != null && await Discovery.DescribeAsync(cfg.BridgeIp) is { } known)
                found.Add(known);
            if (found.Count == 0)
            {
                Notify("No bridge found", "Make sure it's on the same network. If you know its address, put it in bridgeIp in the settings file and choose Pair Bridge again.");
                return;
            }
            if (found.Count > 1)
            {
                foundBridges = found;
                Notify($"Found {found.Count} bridges", "Choose one from the menu.");
                return;
            }
            await Pair(found[0]);
        }
        finally
        {
            pairing = false;
            Refresh();
        }
    }

    async void PairWith(BridgeInfo bridge)
    {
        if (pairing) return;
        pairing = true;
        foundBridges.Clear();
        try { await Pair(bridge); }
        finally
        {
            pairing = false;
            Refresh();
        }
    }

    async Task Pair(BridgeInfo bridge)
    {
        Notify("Press the link button", $"Press the round button on top of {bridge.Name} within 60 seconds.");
        var updated = ConfigFile.Load();
        try
        {
            if (!await Pairing.PairAsync(updated, bridge, TimeSpan.FromSeconds(60)))
            {
                Notify("Pairing timed out", "The link button wasn't pressed. Choose Pair Bridge… to try again.");
                return;
            }
        }
        catch (Exception e) when (e is HueException or HttpRequestException or TaskCanceledException)
        {
            Notify("Couldn't pair", e.Message);
            return;
        }
        ConfigFile.Save(updated);
        configDate = ConfigFile.Modified();
        cfg = updated;
        await StopServices();
        await StartServices();
        Notify($"Paired with {bridge.Name}", "Your rooms and scenes are in the menu.");
    }

    // MARK: Other menu items

    void OpenSettings()
    {
        if (!File.Exists(ConfigFile.Path)) ConfigFile.Save(cfg);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{ConfigFile.Path}\"") { UseShellExecute = true });
    }

    async void Quit()
    {
        tray.Visible = false;
        poll.Stop();
        await StopServices();
        ExitThread();
    }
}

/// Draws the tray icon at runtime: a light bulb, filled while any light is on, faded while the
/// bridge isn't connected, in white or black to suit the taskbar.
sealed class TrayIcons
{
    readonly Dictionary<(bool on, bool dim), Icon> cache = new();
    bool lightTaskbar;
    int size;

    public Icon Get(bool on, bool dim)
    {
        bool light = LightTaskbar();
        int s = SystemInformation.SmallIconSize.Width;
        if (light != lightTaskbar || s != size)
        {
            foreach (var icon in cache.Values) icon.Dispose();
            cache.Clear();
            lightTaskbar = light;
            size = s;
        }
        if (!cache.TryGetValue((on, dim), out var result))
        {
            var color = light ? Color.FromArgb(32, 32, 32) : Color.White;
            if (dim) color = Color.FromArgb(110, color);
            cache[(on, dim)] = result = Draw(size, color, on);
        }
        return result;
    }

    static bool LightTaskbar()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    static Icon Draw(int size, Color color, bool filled)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float stroke = Math.Max(1.5f, size / 10f);
            float c = size / 2f;
            // The glass: a circle in the top two thirds, narrowing into the neck.
            float r = size * 0.32f;
            float cy = size * 0.40f;
            using var glass = new GraphicsPath();
            glass.AddArc(c - r, cy - r, 2 * r, 2 * r, 140, 260);
            glass.AddLine(c + r * 0.55f, cy + r * 0.85f, c + r * 0.45f, size * 0.74f);
            glass.AddLine(c - r * 0.45f, size * 0.74f, c - r * 0.55f, cy + r * 0.85f);
            glass.CloseFigure();
            using var pen = new Pen(color, stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (filled)
            {
                using var brush = new SolidBrush(color);
                g.FillPath(brush, glass);
            }
            g.DrawPath(pen, glass);
            // The base: two short bars.
            float baseY = size * 0.74f + stroke * 1.1f;
            g.DrawLine(pen, c - r * 0.4f, baseY, c + r * 0.4f, baseY);
            g.DrawLine(pen, c - r * 0.25f, baseY + stroke * 1.2f, c + r * 0.25f, baseY + stroke * 1.2f);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
}
