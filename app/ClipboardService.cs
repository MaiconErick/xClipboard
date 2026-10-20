using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace xClipboard;

/// <summary>
/// Watches the system clipboard (AddClipboardFormatListener on a message-only window), files new copies into
/// the History tab and writes clips back when the user pastes one.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    readonly HwndSource source;
    readonly DispatcherTimer debounce;
    DateTime suppressUntil;

    /// <summary>A history item was added or bumped to the top.</summary>
    public event Action? HistoryChanged;
    /// <summary>Any text copied by another app (the refund sheet listens to this).</summary>
    public event Action<string>? TextCopied;

    public ClipboardService()
    {
        var p = new HwndSourceParameters("xClipboard-listener") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
        source = new HwndSource(p);
        source.AddHook(WndProc);
        if (!Native.AddClipboardFormatListener(source.Handle)) Log.Error($"AddClipboardFormatListener failed ({Marshal.GetLastWin32Error()})");
        debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        debounce.Tick += (_, _) => { debounce.Stop(); Capture(); };
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            // Our own writes (pasting a clip, "Copy all" in the refund sheet, Ctrl+C in our text boxes) are ignored.
            if (DateTime.Now < suppressUntil || Native.ClipboardOwnedByUs()) return IntPtr.Zero;
            debounce.Stop();
            debounce.Start();
        }
        return IntPtr.Zero;
    }

    static IDataObject? Open()
    {
        for (int i = 0; i < 8; i++)
        {
            try { return Clipboard.GetDataObject(); }
            catch { Thread.Sleep(35); }
        }
        return null;
    }

    void Capture()
    {
        if (Store.ReadOnly) return; // screenshot/test runs stay deterministic
        var data = Open();
        if (data == null) return;
        string[] formats;
        try { formats = data.GetFormats(false); } catch { return; }

        // Password managers & co. ask clipboard tools to stay away.
        if (formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || formats.Contains("Clipboard Viewer Ignore")) return;
        if (formats.Contains("CanIncludeInClipboardHistory") && ReadDword(data, "CanIncludeInClipboardHistory") == 0) return;

        ClipItem? item = null;
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                item = new ClipItem { Kind = ClipKind.Files, Files = files.ToList(), Text = string.Join(Environment.NewLine, files) };
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
            {
                TextCopied?.Invoke(text);
                if (!string.IsNullOrWhiteSpace(text)) item = new ClipItem { Kind = ClipKind.Text, Text = text };
            }
            else if (formats.Contains("PNG") || data.GetDataPresent(DataFormats.Bitmap))
            {
                var name = SaveImage(data, formats);
                if (name != null) item = new ClipItem { Kind = ClipKind.Image, Image = name };
            }
        }
        catch (Exception e) { Log.Error("reading clipboard", e); }

        if (item == null || Store.Data.Settings.Paused) return;
        item.Source = Native.ProcessNameOf(Native.GetForegroundWindow());
        AddToHistory(item);
    }

    static int ReadDword(IDataObject data, string format)
    {
        try
        {
            if (data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                var b = ms.ToArray();
                return BitConverter.ToInt32(b, 0);
            }
        }
        catch { }
        return -1;
    }

    /// <summary>Encode the clipboard image as PNG named by its hash (identical copies share one file).</summary>
    static string? SaveImage(IDataObject data, string[] formats)
    {
        BitmapSource? bmp = null;
        if (formats.Contains("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            try { bmp = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); } catch { }
        }
        if (bmp == null)
        {
            var img = Clipboard.GetImage();
            if (img == null) return null;
            bmp = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0); // CF_DIB alpha is usually garbage
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();
        var name = Convert.ToHexString(SHA1.HashData(bytes))[..20].ToLowerInvariant() + ".png";
        var path = Path.Combine(Store.ImagesDir, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return name;
    }

    static bool Same(ClipItem a, ClipItem b) => a.Kind == b.Kind && a.Kind switch
    {
        ClipKind.Image => a.Image == b.Image,
        ClipKind.Files => a.Files != null && b.Files != null && a.Files.SequenceEqual(b.Files, StringComparer.OrdinalIgnoreCase),
        _ => a.Text == b.Text,
    };

    /// <summary>New copies go right below the pinned block; duplicates move up instead of repeating.</summary>
    public void AddToHistory(ClipItem item)
    {
        var h = Store.History;
        lock (Store.Data)
        {
            var dup = h.Items.FirstOrDefault(i => Same(i, item));
            if (dup != null)
            {
                dup.Used = DateTime.Now;
                if (dup.Pinned) { HistoryChanged?.Invoke(); return; }
                h.Items.Remove(dup);
                item = dup;
            }
            h.Items.Insert(h.Items.Count(i => i.Pinned), item);
            var limit = Math.Max(10, Store.Data.Settings.HistoryLimit);
            int unpinned = 0;
            for (int i = 0; i < h.Items.Count; i++)
            {
                if (h.Items[i].Pinned) continue;
                if (++unpinned > limit) { h.Items.RemoveAt(i); i--; }
            }
        }
        Store.Save();
        HistoryChanged?.Invoke();
    }

    /// <summary>Put a clip on the system clipboard (our own change is not re-captured).</summary>
    public bool Put(ClipItem item, bool plainTextOnly = false)
    {
        var d = new DataObject();
        switch (item.Kind)
        {
            case ClipKind.Image when !plainTextOnly && item.Image != null:
                var path = Path.Combine(Store.ImagesDir, item.Image);
                if (!File.Exists(path)) return false;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                d.SetImage(bmp);
                d.SetData("PNG", new MemoryStream(File.ReadAllBytes(path)));
                break;
            case ClipKind.Files when !plainTextOnly && item.Files is { Count: > 0 }:
                var sc = new StringCollection();
                sc.AddRange(item.Files.ToArray());
                d.SetFileDropList(sc);
                d.SetText(item.Text);
                break;
            default:
                d.SetText(item.Text ?? "", TextDataFormat.UnicodeText);
                break;
        }
        return Set(d);
    }

    public bool Set(DataObject d)
    {
        suppressUntil = DateTime.Now.AddMilliseconds(700);
        for (int i = 0; i < 8; i++)
        {
            try { Clipboard.SetDataObject(d, true); return true; }
            catch { Thread.Sleep(40); }
        }
        Log.Error("could not write to the clipboard");
        return false;
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(source.Handle);
        source.Dispose();
    }
}

/// <summary>Low-level keyboard hook that takes Win+V away from Windows' own clipboard panel.</summary>
public sealed class WinVHook : IDisposable
{
    readonly Native.LowLevelKeyboardProc proc;
    IntPtr hook;
    bool swallowing;

    public event Action? Pressed;
    public bool Installed => hook != IntPtr.Zero;

    public WinVHook()
    {
        proc = Callback; // keep the delegate alive for the hook's lifetime
        Install();
    }

    public void Install()
    {
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Error($"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()})");
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            // Injected input counts too (Moonlight/Apollo streaming sends real keys that way); our own Ctrl+V never has Win down.
            if (k.vkCode == Native.VK_V)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                if (swallowing)
                {
                    if (!down) swallowing = false;
                    return 1; // auto-repeat and the release of the V we took
                }
                if (down && (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
                    && !Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU) && !Native.IsDown(Native.VK_SHIFT))
                {
                    swallowing = true;
                    Native.MaskWinKey();
                    Application.Current.Dispatcher.BeginInvoke(() => Pressed?.Invoke());
                    return 1;
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }
}
