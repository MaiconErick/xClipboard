using System.Text.Json;
using System.Text.Json.Serialization;

namespace xClipboard;

public static class ClipKind
{
    public const string Text = "text", Image = "image", Files = "files", Refund = "refund";
}

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Kind { get; set; } = ClipKind.Text;
    /// <summary>File name (inside the images folder) for image clips.</summary>
    public string? Image { get; set; }
    public List<string>? Files { get; set; }
    public bool Pinned { get; set; }
    public string? Source { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Used { get; set; } = DateTime.Now;
}

public sealed class ClipTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "📁";
    /// <summary>The automatic history tab (only one; can't be renamed or deleted).</summary>
    public bool IsHistory { get; set; }
    public List<ClipItem> Items { get; set; } = new();
}

public sealed class RefundSheet
{
    public string OrderId { get; set; } = "";
    public List<RefundProduct> Products { get; set; } = [new()];
    public string Date { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class RefundProduct
{
    public string Name { get; set; } = "";
    public string Price { get; set; } = "";
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "halloween";
    public bool Ambient { get; set; } = true;
    public int HistoryLimit { get; set; } = 300;
    public bool Paused { get; set; }
    public string? LastTab { get; set; }
}

public sealed class StoreData
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<ClipTab> Tabs { get; set; } = new();
    public RefundSheet Refund { get; set; } = new();
}

/// <summary>Everything persisted in %APPDATA%\xClipboard\data.json (images live next to it).</summary>
public static class Store
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xClipboard");
    public static readonly string ImagesDir = Path.Combine(Dir, "images");
    static readonly string FilePath = Path.Combine(Dir, "data.json");

    static readonly JsonSerializerOptions json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static StoreData Data { get; private set; } = new();
    /// <summary>Only a process that loaded data.json may write it. Secondary instances (--quit, a second launch,
    /// --parse-test) exit through OnExit too and would otherwise overwrite the history with an empty store.</summary>
    static bool loaded;
    static Timer? saveTimer;
    static readonly object saveLock = new();

    public static ClipTab History => Data.Tabs.First(t => t.IsHistory);

    public static void Load()
    {
        Directory.CreateDirectory(ImagesDir);
        try
        {
            if (File.Exists(FilePath)) Data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), json) ?? new();
        }
        catch (Exception e)
        {
            Log.Error("data.json unreadable, keeping a copy and starting fresh", e);
            try { File.Copy(FilePath, FilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
            Data = new();
        }
        EnsureDefaults();
        loaded = true;
    }

    static void EnsureDefaults()
    {
        if (!Data.Tabs.Any(t => t.IsHistory))
            Data.Tabs.Insert(0, new ClipTab { Name = "History", Icon = "🕓", IsHistory = true });
        if (Data.Tabs.Count == 1)
            Data.Tabs.Add(new ClipTab { Name = "Work", Icon = "💼" });
        // The Work tab always carries the refund sheet launcher (re-added if it was deleted by accident).
        var work = Data.Tabs.FirstOrDefault(t => !t.IsHistory && t.Name.Equals("Work", StringComparison.OrdinalIgnoreCase));
        if (work != null && !Data.Tabs.SelectMany(t => t.Items).Any(i => i.Kind == ClipKind.Refund))
            work.Items.Insert(0, new ClipItem { Kind = ClipKind.Refund, Title = "Refund info", Pinned = true });
        if (Data.Refund.Products.Count == 0) Data.Refund.Products.Add(new());
    }

    /// <summary>Debounced save (many changes in a burst = one write).</summary>
    /// <summary>Screenshot/test mode: never touch data.json.</summary>
    public static bool ReadOnly { get; set; }

    public static void Save()
    {
        if (ReadOnly || !loaded) return;
        saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        saveTimer.Change(400, Timeout.Infinite);
    }

    public static void SaveNow()
    {
        if (ReadOnly || !loaded) return;
        lock (saveLock)
        {
            try
            {
                string text;
                lock (Data) text = JsonSerializer.Serialize(Data, json);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e) { Log.Error("save failed", e); }
        }
    }

    /// <summary>Delete image files no clip references any more.</summary>
    public static void CleanImages()
    {
        try
        {
            var used = Data.Tabs.SelectMany(t => t.Items).Where(i => i.Image != null).Select(i => i.Image!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(ImagesDir))
                if (!used.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch (Exception e) { Log.Error("image cleanup", e); }
    }
}

public static class Log
{
    static readonly string FilePath = Path.Combine(Store.Dir, "xClipboard.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : $"{msg}: {e}");

    static void Write(string level, string msg)
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", true);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
