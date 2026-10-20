using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace xClipboard;

/// <summary>One colour scheme. All UI brushes are DynamicResources, so switching rewrites them live.</summary>
public sealed record Palette(
    string Id, string Name, string Emoji,
    Color Bg0, Color Bg1, Color Bg2, Color Bg3, Color Edge,
    Color Accent, Color AccentDeep, Color Accent2,
    Color Good, Color Bad, Color Text, Color Gold, Color Muted, Color Faint,
    string DisplayFont, string Ambient, string[] Sprites);

public static class Theme
{
    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    const string Creepster = "pack://application:,,,/Fonts/#Creepster";

    public static readonly Palette[] All =
    [
        new("halloween", "Halloween", "🎃",
            C(0x0B0612), C(0x150C21), C(0x1F1230), C(0x2B1A40), C(0x3A2552),
            C(0xFF7A18), C(0xC24E00), C(0x8B3DFF),
            C(0x7CFF4F), C(0xFF2E4D), C(0xF2E8D5), C(0xFFD54A), C(0x9A8BB0), C(0x5E4F75),
            Creepster, "bats", ["pumpkin", "ghost", "bat1"]),
        new("bloodmoon", "Blood Moon", "🩸",
            C(0x0D0305), C(0x170609), C(0x220A0E), C(0x301015), C(0x4A1820),
            C(0xFF2E4D), C(0x9E0F2A), C(0xFFD54A),
            C(0x7CFF4F), C(0xFF2E4D), C(0xF5E6E6), C(0xFFD54A), C(0xB08A90), C(0x6E4A50),
            Creepster, "embers", ["moon", "skull", "bat2"]),
        new("witchbrew", "Witch's Brew", "🧪",
            C(0x060E08), C(0x0B170E), C(0x112216), C(0x18301F), C(0x244A30),
            C(0x7CFF4F), C(0x2FA81C), C(0xB36BFF),
            C(0x7CFF4F), C(0xFF2E4D), C(0xE8F5E0), C(0xFFD54A), C(0x8FB09A), C(0x4F6E58),
            Creepster, "bubbles", ["cauldron", "hat", "spider"]),
        new("neon", "Neon", "💠",
            C(0x05060A), C(0x0B0D16), C(0x121626), C(0x1B2036), C(0x26304F),
            C(0x00F0FF), C(0x0090B0), C(0xFF2BD6),
            C(0x39FF14), C(0xFF3864), C(0xE8F7FF), C(0xFFE600), C(0x8A95B8), C(0x4A5478),
            "Bahnschrift SemiBold", "stars", []),
        new("frost", "Frost", "❄",
            C(0x060B14), C(0x0B1420), C(0x111D2E), C(0x18283E), C(0x263C58),
            C(0x6FD3FF), C(0x2A8BC0), C(0xB9A8FF),
            C(0x7CFFB2), C(0xFF6B81), C(0xEAF4FF), C(0xFFF3B0), C(0x8EA4C0), C(0x4E6380),
            "Segoe UI Semibold", "snow", ["ghost"]),
        new("sakura", "Sakura", "🌸",
            C(0x120A10), C(0x1C0F18), C(0x271522), C(0x341D2E), C(0x4A2A42),
            C(0xF7A6C4), C(0xC2668A), C(0x9FD8FF),
            C(0xA8F0B0), C(0xFF6B81), C(0xFFF0F6), C(0xFFE3A3), C(0xB897AC), C(0x6E5266),
            "Segoe Print", "petals", []),
        new("aurora", "Aurora", "🌌",
            C(0x03080C), C(0x071219), C(0x0C1C25), C(0x132833), C(0x1E3D4C),
            C(0x4FD8FF), C(0x1F8FB0), C(0x7CFF4F),
            C(0x7CFF4F), C(0xFF5A6E), C(0xE6FAFF), C(0xFFE48A), C(0x86A9B5), C(0x466572),
            "Bahnschrift", "aurora", ["moon"]),
    ];

    /// <summary>The palette currently applied (the ambient layer reads its colours every frame).</summary>
    public static Palette P { get; private set; } = All[0];

    public static event Action? Changed;

    public static Palette Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    public static void Apply(string? id)
    {
        var p = Find(id);
        P = p;
        var r = Application.Current.Resources;
        void S(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); r[key] = b; }
        S("Night", p.Bg0); S("Crypt", p.Bg1); S("Crypt2", p.Bg2); S("Crypt3", p.Bg3); S("Edge", p.Edge);
        S("Accent", p.Accent); S("AccentDeep", p.AccentDeep); S("Accent2", p.Accent2);
        S("Good", p.Good); S("Bad", p.Bad); S("Text", p.Text); S("Gold", p.Gold); S("Muted", p.Muted); S("Faint", p.Faint);
        S("CardBg", Mix(p.Bg2, p.Bg1, 0.5)); S("CardHover", p.Bg3); S("CardSel", Mix(p.Bg3, p.Accent, 0.12));
        S("AccentSoft", Color.FromArgb(0x33, p.Accent.R, p.Accent.G, p.Accent.B));
        r["AccentColor"] = p.Accent;

        var grad = new LinearGradientBrush(Lighten(p.Accent, 0.18), p.AccentDeep, 45) { GradientStops = { new GradientStop(Lighten(p.Accent, 0.18), 0), new GradientStop(p.Accent, 0.55), new GradientStop(p.AccentDeep, 1) } };
        grad.Freeze();
        r["AccentGrad"] = grad;
        var win = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.4, 1),
            GradientStops = { new GradientStop(p.Bg0, 0), new GradientStop(p.Bg1, 0.6), new GradientStop(Mix(p.Bg1, p.Bg3, 0.35), 1) } };
        win.Freeze();
        r["WindowGrad"] = win;
        r["Display"] = new FontFamily(p.DisplayFont);
        Changed?.Invoke();
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);
}

/// <summary>Pixel-art sprites (ported from xVideoWatcher via xPlayer). '.' = transparent.</summary>
public static class Sprites
{
    static readonly Dictionary<char, uint> palette = new()
    {
        ['o'] = 0xFFFF7A18, ['O'] = 0xFFC24E00, ['d'] = 0xFF7A2E00, ['y'] = 0xFFFFD54A, ['Y'] = 0xFFFFF3B0,
        ['g'] = 0xFF5BD44A, ['G'] = 0xFF2E7D32, ['w'] = 0xFFF4F0FF, ['s'] = 0xFFB9A8E0, ['k'] = 0xFF140A1C,
        ['p'] = 0xFF8B3DFF, ['P'] = 0xFF4B1C99, ['r'] = 0xFFFF2E4D, ['R'] = 0xFF9E0F2A, ['b'] = 0xFF7A4FB0,
        ['B'] = 0xFF3E2366, ['e'] = 0xFFFF2E4D, ['c'] = 0xFF7CFF4F, ['C'] = 0xFF2FA81C, ['m'] = 0xFFFFF7D6,
        ['M'] = 0xFFE8D9A0, ['n'] = 0xFF6E6E80, ['N'] = 0xFF3F3F4F, ['l'] = 0xFFA0A0B8, ['h'] = 0xFF5A3A1A,
    };

    static readonly Dictionary<string, string[]> rows = new()
    {
        // App icon: a pumpkin-orange clipboard whose sheet is a little ghost.
        ["clip"] =
        [
            "......nnnn......", ".....nllllN.....", "..OOOnyyyyNOOO..", ".OooOOOOOOOOooO.", ".OomwwwwwwwwmoO.",
            ".OowwwwwwwwwwoO.", ".OowwkkwwkkwwoO.", ".OowwkkwwkkwwoO.", ".OowwwwwwwwwwoO.", ".OowwwwkkwwwwoO.",
            ".OowwwwkkwwwwoO.", ".OowwwwwwwwwwoO.", ".Oow.ww.ww.wwoO.", ".OooooooooooooO.", "..OOOOOOOOOOOO..",
        ],
        ["pumpkin"] =
        [
            ".......GG.......", "......GgG.......", "...OOOoGGoOOO...", "..OoooOooOoooO..", ".OoooOooooOoooO.",
            ".OooyyOooOyyooO.", "OooyyyyOOyyyyooO", "OoooooOooOoooooO", "OoooooOyyOoooooO", "OoyooOooooOooyoO",
            "OoyyyyyyyyyyyyoO", ".OoyyOyyyyOyyoO.", ".OoooOOyyOOoooO.", "..OOooOooOooOO..", "....OOOOOOOO....",
        ],
        ["ghost"] =
        [
            ".....wwwww......", "...wwwwwwwws....", "..wwwwwwwwwss...", ".wwwwwwwwwwwss..", ".wwkkwwwkkwwss..",
            ".wwkkwwwkkwwss..", "wwwwwwwwwwwwwss.", "wwwwwwkkkwwwwss.", "wwwwwkkkkkwwwss.", "wwwwwwkkkwwwwss.",
            "wwwwwwwwwwwwwss.", "wwwwwwwwwwwwwss.", "wwwwwwwwwwwwwss.", "ww.wwww.wwww.ss.", "w...ww...ww...s.",
        ],
        ["bat1"] =
        [
            "B..............B", "BB............BB", "BbB....bb....BbB", "BbbB..bBBb..BbbB", "BbbbBbBBBBbBbbbB",
            ".BbbbBBeBeBBbbB.", "..BBbBBBBBBbBB..", "....B.BBBB.B....", ".......BB.......",
        ],
        ["bat2"] =
        [
            "................", "................", ".......bb.......", "......bBBb......", ".....BBBBBB.....",
            "..BBBBBeBeBBBB..", ".BbbbBBBBBBbbbB.", "BbbB.B.BB.B.BbbB", "BB......BB....BB",
        ],
        ["skull"] =
        [
            "....mmmmmm....", "..mmmmmmmmmm..", ".mmmmmmmmmmmM.", ".mmmmmmmmmmmM.", "mmkkkmmmmkkkmM", "mmkkkmmmmkkkmM",
            "mmkkkmmmmkkkmM", ".mmmmmkkmmmmM.", "..mmmmkkmmmM..", "...mmmmmmmM...", "...mkmkmkmM...", "...mmmmmmmM...",
            "....MMMMMM....",
        ],
        ["spider"] =
        [
            ".......l.......", ".......l.......", ".......l.......", ".......l.......", "..P...PPP...P..",
            "...P.PpppP.P...", "P...PppppppP..P", ".PPPPpepepPPPP.", "....PpppppP....", ".PPPPPpppPPPPP.",
            "P...PPpppPP...P", "...P.PPPPP.P...", "..P...PPP...P..",
        ],
        ["cauldron"] =
        [
            "...c..c...c...", "..c.cccc.c.c..", ".CcccccccccccC", "NNNNNNNNNNNNNN", ".nnNnnnnnnnnN.", "nnlnnnnnnnnnnN",
            "nlnnnnnnnnnnnN", "nnnnnnnnnnnnnN", ".nnnnnnnnnnnN.", "..nnnnnnnnnN..", "..NN......NN..",
        ],
        ["moon"] =
        [
            "....YYYYY....", "..YYYYYYYYY..", ".YYYYyYYYYYY.", ".YYYyyYYYYYYY", "YYYYYYYYYYYYy", "YYYYYYYYyYYYy",
            "YYYYYYYyyYYYy", "YYyYYYYYYYYYy", ".YyyYYYYYYYy.", ".YYYYYYYYYyy.", "..YYYYYYYyy..", "....YYyyy....",
        ],
        ["hat"] =
        [
            ".........PP.....", "........PpP.....", ".......PppP.....", "......PpppP.....", ".....PppppP.....",
            "....PpppppP.....", "....PppppppP....", "...PyyyyyyyyP...", "...PPPPPPPPPP...", "PPPPppppppppPPPP",
            ".PPPPPPPPPPPPPP.",
        ],
    };

    static readonly Dictionary<string, BitmapSource> cache = new();

    public static BitmapSource Get(string id)
    {
        if (cache.TryGetValue(id, out var bmp)) return bmp;
        var r = rows[id];
        int w = r.Max(x => x.Length), h = r.Length;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < r[y].Length; x++)
                if (palette.TryGetValue(r[y][x], out var c)) px[y * w + x] = c;
        bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return cache[id] = bmp;
    }

    /// <summary>Raw ARGB pixels + size (used to build the tray icon).</summary>
    public static (uint[] px, int w, int h) Pixels(string id)
    {
        var r = rows[id];
        int w = r.Max(x => x.Length), h = r.Length;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < r[y].Length; x++)
                if (palette.TryGetValue(r[y][x], out var c)) px[y * w + x] = c;
        return (px, w, h);
    }

    public static Image Make(string id, double height)
    {
        var src = Get(id);
        var img = new Image
        {
            Source = src, Height = height, Width = height * src.PixelWidth / src.PixelHeight,
            Stretch = Stretch.Fill, IsHitTestVisible = false, SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return img;
    }
}

/// <summary>XAML-usable sprite: &lt;local:Sprite Id="pumpkin" Height="32"/&gt;</summary>
public sealed class Sprite : Image
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.Register(
        nameof(Id), typeof(string), typeof(Sprite), new PropertyMetadata(null, (d, e) => ((Sprite)d).Refresh()));

    public string? Id { get => (string?)GetValue(IdProperty); set => SetValue(IdProperty, value); }

    public Sprite()
    {
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    void Refresh() => Source = Id == null ? null : Sprites.Get(Id);
}
