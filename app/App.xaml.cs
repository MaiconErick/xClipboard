using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace xClipboard;

public partial class App : Application
{
    const string PipeName = "xClipboard-commands";
    static Mutex? singleInstance;

    public static ClipboardService Clips { get; private set; } = null!;
    public static WinVHook Hook { get; private set; } = null!;
    public static MainWindow Popup { get; private set; } = null!;
    static RefundWindow? refund;
    static Forms.NotifyIcon? tray;
    static DispatcherTimer? hookKeeper;

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var cmd = e.Args.FirstOrDefault(a => a.StartsWith("--"))?.TrimStart('-').ToLowerInvariant() ?? "";
        if (cmd == "parse-test") { ParseTest(e.Args.Skip(1).ToArray()); Shutdown(); return; }
        if (cmd == "screenshot") { Screenshot(e.Args.Skip(1).ToArray()); return; }
        if (cmd == "refund-test") { RefundTest(e.Args.Skip(1).ToArray()); return; }

        singleInstance = new Mutex(true, "xClipboard-single-instance", out bool first);
        if (!first)
        {
            // --background (used by build.ps1) only makes sure it's running; it never pops the window open.
            if (cmd != "background") Send(cmd.Length > 0 ? cmd : "show");
            Shutdown();
            return;
        }
        if (cmd == "quit") { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { Log.Error("unhandled", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("fatal", ex.ExceptionObject as Exception);
        base.OnStartup(e);

        Store.Load();
        Theme.Apply(Store.Data.Settings.Theme);
        Clips = new ClipboardService();
        Popup = new MainWindow();
        Hook = new WinVHook();
        Hook.Pressed += () => Popup.Toggle();
        // Windows silently drops low-level hooks that ever time out; re-arm periodically to be safe.
        hookKeeper = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        hookKeeper.Tick += (_, _) => { if (!Popup.IsVisible) Hook.Install(); };
        hookKeeper.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, _) => Dispatcher.BeginInvoke(() => Hook.Install());

        CreateTray();
        StartPipeServer();
        Log.Info($"started v{Version}, hook {(Hook.Installed ? "ok" : "FAILED")}");

        if (cmd == "show") Popup.ShowPopup();
        if (cmd == "refund") ShowRefund();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Store.SaveNow(); } catch { }
        try { Hook?.Dispose(); } catch { }
        try { Clips?.Dispose(); } catch { }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        base.OnExit(e);
    }

    public static void Quit()
    {
        Store.SaveNow();
        Current.Shutdown();
    }

    public static void ShowRefund()
    {
        if (refund == null)
        {
            refund = new RefundWindow();
            refund.Closed += (_, _) => refund = null;
            refund.Show();
        }
        if (refund.WindowState == WindowState.Minimized) refund.WindowState = WindowState.Normal;
        refund.Show();
        refund.Activate();
    }

    public static void RefreshRefundTheme() => refund?.ApplyThemeExtras();

    public static void SetTheme(string id)
    {
        Store.Data.Settings.Theme = id;
        Store.Save();
        Theme.Apply(id);
        RefreshRefundTheme();
    }

    // ===================================================================== tray

    static void CreateTray()
    {
        tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "xClipboard — Win+V", Visible = true };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Current.Dispatcher.BeginInvoke(() => Popup.Toggle());
        };
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = new Drawing.Font("Segoe UI", 9.5f) };
        menu.Items.Add("Open  (Win+V)", null, (_, _) => Current.Dispatcher.BeginInvoke(() => Popup.ShowPopup()));
        menu.Items.Add("Refund info sheet", null, (_, _) => Current.Dispatcher.BeginInvoke(ShowRefund));
        var themes = new Forms.ToolStripMenuItem("Theme");
        foreach (var p in Theme.All)
        {
            var pal = p;
            themes.DropDownItems.Add(new Forms.ToolStripMenuItem($"{pal.Emoji}  {pal.Name}", null, (_, _) => Current.Dispatcher.BeginInvoke(() => SetTheme(pal.Id))));
        }
        themes.DropDown.Renderer = new DarkRenderer();
        menu.Items.Add(themes);
        var pause = new Forms.ToolStripMenuItem("Pause history capture");
        pause.Click += (_, _) => { Store.Data.Settings.Paused = !Store.Data.Settings.Paused; Store.Save(); };
        menu.Items.Add(pause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add($"xClipboard v{Version}").Enabled = false;
        menu.Items.Add("Quit", null, (_, _) => Current.Dispatcher.BeginInvoke(Quit));
        menu.Opening += (_, _) =>
        {
            pause.Checked = Store.Data.Settings.Paused;
            foreach (Forms.ToolStripMenuItem it in themes.DropDownItems) it.Checked = it.Text!.EndsWith(Theme.P.Name);
        };
        tray.ContextMenuStrip = menu;
    }

    /// <summary>The "clip" pixel sprite scaled 2x into a 32px icon.</summary>
    static Drawing.Icon TrayIcon()
    {
        var (px, w, h) = Sprites.Pixels("clip");
        using var bmp = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
        int ox = (32 - w * 2) / 2, oy = (32 - h * 2) / 2;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = Drawing.Color.FromArgb(unchecked((int)px[y * w + x]));
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        bmp.SetPixel(ox + x * 2 + dx, oy + y * 2 + dy, c);
            }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Drawing.Color.FromArgb(0xF2, 0xE8, 0xD5) : Drawing.Color.FromArgb(0x9A, 0x8B, 0xB0);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Drawing.Color.FromArgb(0xF2, 0xE8, 0xD5);
            base.OnRenderArrow(e);
        }
    }

    sealed class DarkColors : Forms.ProfessionalColorTable
    {
        static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x1F, 0x12, 0x30), Hi = Drawing.Color.FromArgb(0x3A, 0x25, 0x52), Edge = Drawing.Color.FromArgb(0x3A, 0x25, 0x52);
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuItemSelected => Hi;
        public override Drawing.Color MenuItemBorder => Hi;
        public override Drawing.Color MenuBorder => Edge;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hi;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hi;
        public override Drawing.Color MenuItemPressedGradientBegin => Hi;
        public override Drawing.Color MenuItemPressedGradientEnd => Hi;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color SeparatorDark => Edge;
        public override Drawing.Color SeparatorLight => Bg;
        public override Drawing.Color CheckBackground => Hi;
        public override Drawing.Color CheckSelectedBackground => Hi;
        public override Drawing.Color CheckPressedBackground => Hi;
    }

    // ===================================================================== single instance IPC

    static void Send(string cmd)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2500);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(cmd);
        }
        catch (Exception e) { Log.Error("could not reach the running xClipboard", e); }
    }

    void StartPipeServer()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    var cmd = r.ReadLine()?.Trim() ?? "";
                    Dispatcher.BeginInvoke(() =>
                    {
                        switch (cmd)
                        {
                            case "quit": Quit(); break;
                            case "refund": ShowRefund(); break;
                            default: Popup.ShowPopup(); break;
                        }
                    });
                }
                catch (Exception e) { Log.Error("pipe server", e); Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "xclipboard-ipc" };
        t.Start();
    }

    /// <summary>--screenshot out.png [main|refund|edit] [theme]: renders a window off-screen with sample data (never saves).</summary>
    void Screenshot(string[] args)
    {
        Store.ReadOnly = true;
        Store.Data.Tabs.Clear();
        var history = new ClipTab { Name = "History", Icon = "🕓", IsHistory = true };
        history.Items.AddRange([
            new ClipItem { Text = "https://assetstore.unity.com/packages/3d/vegetation/stylized-farm-crops", Pinned = true, Source = "vivaldi", Created = DateTime.Now.AddHours(-3) },
            new ClipItem { Kind = ClipKind.Files, Files = [@"C:\DEV\Windows\xClipboard\build.ps1", @"C:\DEV\Windows\xClipboard\xClipboard.png", Path.Combine(Path.GetTempPath(), "xclip_sample.png")], Text = "", Source = "explorer", Created = DateTime.Now.AddHours(-2) },
            new ClipItem { Text = "A2607212249255253", Source = "vivaldi", Created = DateTime.Now.AddMinutes(-1) },
            new ClipItem { Text = "Hi! Thanks for reaching out.\nI've issued the refund, it should show up in 5-10 business days.", Source = "Discord", Created = DateTime.Now.AddMinutes(-12) },
            new ClipItem { Kind = ClipKind.Image, Image = Path.Combine(Path.GetTempPath(), "xclip_sample.png"), Source = "SnippingTool", Created = DateTime.Now.AddMinutes(-5) },
            new ClipItem { Text = "git commit -m \"Fix the refund sheet\"", Source = "WindowsTerminal", Created = DateTime.Now.AddDays(-1) },
        ]);
        var work = new ClipTab { Name = "Work", Icon = "💼" };
        work.Items.AddRange([
            new ClipItem { Kind = ClipKind.Refund, Title = "Refund info", Pinned = true },
            new ClipItem { Title = "Refund approved reply", Text = "Hello! Your refund has been approved and processed.\nPlease allow 5-10 business days.", Pinned = true },
            new ClipItem { Title = "Invoice request", Text = "Could you send me the invoice number from your receipt email?", Pinned = true },
        ]);
        Store.Data.Tabs.AddRange([history, work, new ClipTab { Name = "Snippets", Icon = "📝" }]);
        Store.Data.Settings.LastTab = args.Length > 1 && args[1] == "history" ? history.Id : work.Id;
        Store.Data.Refund = new RefundSheet
        {
            OrderId = "A2607212249255253", Date = "07/21/2026", Status = "Refunded", Reason = "Bought the wrong pack by mistake.",
            Products = [new() { Name = "Stylized Farm Crops - Ultimate Low Poly Asset Pack", Price = "USD $20.99" }, new() { Name = "Low Poly Trees", Price = "" }],
        };
        Theme.Apply(args.Length > 2 ? args[2] : "halloween");
        Clips = new ClipboardService();
        Hook = new WinVHook();
        Hook.Dispose();

        Window w = args.Length > 1 && args[1] == "refund" ? new RefundWindow() : Popup = new MainWindow();
        w.ShowActivated = false;
        w.Left = -10000;
        w.Top = -10000;
        w.Show();
        Popup?.ShowPopupForTest(args.Length > 1 && args[1] == "edit");
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            await Task.Delay(1200);
            var root = (FrameworkElement)w.Content;
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * 1.5), (int)(root.ActualHeight * 1.5), 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(args[0])) enc.Save(fs);
            Shutdown();
        });
    }

    /// <summary>--refund-test out.json "copy1" "copy2"…: feeds copies to an off-screen refund sheet and dumps the result.</summary>
    void RefundTest(string[] args)
    {
        Store.ReadOnly = true;
        Store.Data.Tabs.Add(new ClipTab { Name = "History", IsHistory = true });
        Theme.Apply("halloween");
        Clips = new ClipboardService();
        var w = new RefundWindow { ShowActivated = false, Left = -10000, Top = -10000 };
        w.Show();
        foreach (var a in args.Skip(1)) w.OnCopied(a.Replace("\\n", "\n").Replace("\\t", "\t"));
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(args[0], System.Text.Json.JsonSerializer.Serialize(Store.Data, opts));
        Shutdown();
    }

    /// <summary>--parse-test out.txt "text1" "text2"…: prints how the refund parser classifies each input.</summary>
    static void ParseTest(string[] args)
    {
        if (args.Length == 0) return;
        var lines = new List<string>();
        foreach (var a in args.Skip(1))
        {
            var text = a.Replace("\\n", "\n").Replace("\\t", "\t");
            var parts = RefundParser.Classify(text);
            lines.Add($"{a}  =>  {string.Join(" | ", parts.Select(p => $"{p.field}: {p.value}"))}");
        }
        File.WriteAllLines(args[0], lines);
    }
}
