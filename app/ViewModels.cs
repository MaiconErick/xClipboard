using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace xClipboard;

/// <summary>One image row: preview on the left, name / folder / size on the right.</summary>
public sealed record ThumbVM(BitmapImage Image, string Name, string Folder, string Info, double Height)
{
    public bool HasFolder => Folder.Length > 0;
    /// <summary>Fixed frame width so the text beside every preview lines up.</summary>
    public double Width => Math.Round(Height * 1.45);
    public string FullPath => Folder.Length > 0 ? Path.Combine(Folder, Name) : Name;
}

public sealed class InverseVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>One card in the list: wraps a stored clip plus its transient edit state.</summary>
public sealed class ClipVM : INotifyPropertyChanged
{
    public ClipItem Item { get; }
    public ClipTab Tab { get; }
    /// <summary>Shown when the list mixes tabs (search results).</summary>
    public bool ShowTabName { get; init; }

    public ClipVM(ClipItem item, ClipTab tab) { Item = item; Tab = tab; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public bool IsRefund => Item.Kind == ClipKind.Refund;
    public bool IsImage => Item.Kind == ClipKind.Image;
    public bool CanEditContent => Item.Kind == ClipKind.Text;
    public bool CanEdit => !IsRefund;
    public bool HasTitle => !string.IsNullOrWhiteSpace(Item.Title);

    public string Glyph => Item.Kind switch
    {
        ClipKind.Image => "",
        ClipKind.Files => HasImageFiles ? "" : "",
        ClipKind.Refund => "",
        _ => HasTitle ? "" : "",
    };

    public string DisplayTitle
    {
        get
        {
            if (HasTitle) return Item.Title.Trim();
            return Item.Kind switch
            {
                ClipKind.Image => "Image",
                ClipKind.Files => Item.Files is { Count: 1 } f ? Path.GetFileName(f[0]) : $"{Item.Files?.Count ?? 0} files",
                _ => FirstLine(Item.Text),
            };
        }
    }

    public string Preview
    {
        get
        {
            if (IsRefund) return "Opens the refund sheet · copy anything and it lands in the right field";
            if (Item.Kind == ClipKind.Files)
            {
                // image files are listed beside their previews; only the other files are named here
                var files = (Item.Files ?? []).Where(f => !HasThumbs || !IsImageFile(f)).ToList();
                return files.Count > 1 ? string.Join("  ·  ", files.Take(4).Select(Path.GetFileName)) : files.FirstOrDefault() ?? "";
            }
            if (Item.Kind == ClipKind.Image) return "";
            var text = Item.Text.Replace("\r", "");
            if (!HasTitle)
            {
                var nl = text.TrimStart().IndexOf('\n');
                text = nl < 0 ? "" : text.TrimStart()[(nl + 1)..];
            }
            return Squash(text, 220);
        }
    }

    public bool HasPreview => Preview.Length > 0;

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (ShowTabName) parts.Add($"{Tab.Icon} {Tab.Name}");
            if (Item.Kind == ClipKind.Text && !IsRefund) parts.Add($"{Item.Text.Length:N0} chars");
            if (!string.IsNullOrEmpty(Item.Source) && Tab.IsHistory) parts.Add(Item.Source!);
            if (!IsRefund) parts.Add(Ago(Item.Created));
            return string.Join("  ·  ", parts);
        }
    }

    public bool HasMeta => Meta.Length > 0;

    public bool IsPinned => Item.Pinned || !Tab.IsHistory;
    public string PinGlyph => IsPinned ? "" : "";
    public string PinTip => !Tab.IsHistory
        ? $"Pinned in {Tab.Name} — click to unpin (sends it back to History)"
        : Item.Pinned ? "Pinned — click to unpin" : "Pin (kept forever, stays on top)";

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif" };

    static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Image files among a file-drop clip (copied in Explorer).</summary>
    public bool HasImageFiles => Item.Kind == ClipKind.Files && Item.Files?.Any(IsImageFile) == true;

    List<ThumbVM>? thumbs;
    /// <summary>Previews: the copied image itself, or up to 4 image files of a file clip.</summary>
    public IReadOnlyList<ThumbVM> Thumbs
    {
        get
        {
            if (thumbs != null) return thumbs;
            thumbs = new();
            IEnumerable<string> paths = Item.Kind switch
            {
                ClipKind.Image when Item.Image != null => [Path.Combine(Store.ImagesDir, Item.Image)],
                ClipKind.Files when Item.Files != null => Item.Files.Where(IsImageFile).Take(4),
                _ => [],
            };
            var list = paths.ToList();
            double h = list.Count > 1 ? 64 : 96; // one preview gets more room than several
            foreach (var p in list)
            {
                if (Load(p) is not { } img) continue;
                var pasted = Item.Kind == ClipKind.Image;
                thumbs.Add(new ThumbVM(img, pasted ? "Pasted image" : Path.GetFileName(p), pasted ? "" : Path.GetDirectoryName(p) ?? "", Info(p), h));
            }
            return thumbs;
        }
    }

    public bool HasThumbs => Thumbs.Count > 0;

    /// <summary>Image clips show their details beside the preview, so the usual title/preview lines are hidden
    /// unless the user gave the clip a title or the clip also holds non-image files.</summary>
    public bool ShowHeader => !HasThumbs || HasTitle || (Item.Kind == ClipKind.Files && (Item.Files?.Any(f => !IsImageFile(f)) ?? false));

    /// <summary>"1920×1080 · 245 KB" (original size, read from the file header).</summary>
    static string Info(string path)
    {
        var parts = new List<string>();
        try
        {
            var dec = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var f = dec.Frames[0];
            parts.Add($"{f.PixelWidth}×{f.PixelHeight}");
        }
        catch { }
        try
        {
            var len = new FileInfo(path).Length;
            parts.Add(len >= 1 << 20 ? $"{len / 1048576.0:0.0} MB" : $"{Math.Max(1, len / 1024)} KB");
        }
        catch { }
        return string.Join("  ·  ", parts);
    }

    static BitmapImage? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.DecodePixelHeight = 380; // sharp enough for the hover preview too
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; } // unsupported codec (e.g. no HEIC extension) → just no preview
    }

    // ---------- editing ----------
    bool expanded;
    public bool IsExpanded { get => expanded; set { if (expanded == value) return; expanded = value; On(); } }
    /// <summary>Created with "New clip" and not saved yet (cancel removes it).</summary>
    public bool IsNew { get; set; }

    string editTitle = "", editText = "";
    public string EditTitle { get => editTitle; set { editTitle = value; On(); On(nameof(TitleHintVisible)); } }
    public string EditText { get => editText; set { editText = value; On(); On(nameof(TextHintVisible)); } }
    public Visibility TitleHintVisible => editTitle.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextHintVisible => editText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool Dirty => expanded && (editTitle != Item.Title || (CanEditContent && editText != Item.Text));

    public void BeginEdit()
    {
        EditTitle = Item.Title;
        EditText = Item.Text;
        IsExpanded = true;
    }

    public void Commit()
    {
        Item.Title = editTitle.Trim();
        if (CanEditContent) Item.Text = editText;
        IsExpanded = false;
        IsNew = false;
        RefreshAll();
    }

    static string FirstLine(string s)
    {
        var t = s.Replace("\r", "").Trim();
        var nl = t.IndexOf('\n');
        if (nl >= 0) t = t[..nl];
        return Squash(t, 140);
    }

    static string Squash(string s, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"[ \t]+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\n\s*\n+", "\n");
        return t.Length > max ? t[..max] + "…" : t;
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return t.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }
}
