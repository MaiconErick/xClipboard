using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace xClipboard;

/// <summary>
/// Always-on-top refund sheet. While open it listens to every text copy and drops each value into the field it
/// looks like (order ID, product, price, date, status, reason). Fields stay editable; Ctrl+Z undoes an auto-fill.
/// </summary>
public partial class RefundWindow : Window
{
    RefundSheet Sheet => Store.Data.Refund;
    readonly Dictionary<string, (Border row, TextBox box)> fields = new();
    readonly Stack<string> undo = new();
    bool building, listening = true;

    public RefundWindow()
    {
        InitializeComponent();
        Frame.SizeChanged += (_, _) => ClipHost.Clip = new RectangleGeometry(new Rect(0, 0, Frame.ActualWidth - 2, Frame.ActualHeight - 2), 15, 15);
        PreviewKeyDown += OnKey;
        LocationChanged += (_, _) => { if (IsLoaded) { Sheet.Left = Left; Sheet.Top = Top; Store.Save(); } };
        Status.Cursor = Cursors.Hand;
        Status.MouseLeftButtonUp += (_, _) => ToggleListening();
        Dot.Cursor = Cursors.Hand;
        Dot.MouseLeftButtonUp += (_, _) => ToggleListening();
        App.Clips.TextCopied += OnCopied;
        Closed += (_, _) => App.Clips.TextCopied -= OnCopied;
        ApplyThemeExtras();
        Build();
        PlaceInitially();
    }

    public void ApplyThemeExtras()
    {
        Ambient.Kind = Store.Data.Settings.Ambient ? Theme.P.Ambient : "off";
        TitleGlow.Color = Theme.P.Accent;
        HeaderSprite.Content = Sprites.Make(Theme.P.Sprites.FirstOrDefault() ?? "clip", 22);
    }

    void PlaceInitially()
    {
        var wa = SystemParameters.WorkArea;
        if (Sheet.Left is double l && Sheet.Top is double t && double.IsFinite(l) && double.IsFinite(t) &&
            l > SystemParameters.VirtualScreenLeft - 100 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            t > SystemParameters.VirtualScreenTop - 20 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = wa.Right - Width - 20;
            Top = wa.Top + 60;
        }
    }

    // ===================================================================== rows

    static readonly (string key, string label)[] Singles =
        [("order", "Order ID:"), ("date", "Date of purchase:"), ("status", "Refund Status:"), ("reason", "Reason:")];

    void Build()
    {
        building = true;
        Rows.Children.Clear();
        fields.Clear();
        AddRow("order", "Order ID:", Sheet.OrderId);
        bool many = Sheet.Products.Count > 1;
        for (int i = 0; i < Sheet.Products.Count; i++)
        {
            AddRow($"name:{i}", "Product name:", Sheet.Products[i].Name, many ? i + 1 : null, wrap: true);
            AddRow($"price:{i}", "Price:", Sheet.Products[i].Price, many ? i + 1 : null);
        }
        AddRow("date", "Date of purchase:", Sheet.Date);
        AddRow("status", "Refund Status:", Sheet.Status);
        AddRow("reason", "Reason:", Sheet.Reason, multiline: true);
        ProductCount.Text = Sheet.Products.Count == 1 ? "1 product" : $"{Sheet.Products.Count} products";
        RemoveProductBtn.IsEnabled = Sheet.Products.Count > 1;
        building = false;
    }

    void AddRow(string key, string label, string value, int? number = null, bool multiline = false, bool wrap = false)
    {
        var row = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 3, 2, 3), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = new Thickness(0, multiline ? 6 : 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "Click to copy this value" };
        var lbl = new TextBlock { Text = label, FontWeight = FontWeights.Bold, FontSize = 13 };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        head.Children.Add(lbl);
        if (number != null)
        {
            var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            var bt = new TextBlock { Text = number.ToString(), FontSize = 10, FontWeight = FontWeights.SemiBold };
            bt.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            badge.Child = bt;
            head.Children.Add(badge);
        }
        head.MouseLeftButtonUp += (_, _) => CopyValue(key, label);
        g.Children.Add(head);

        var box = new TextBox { Style = (Style)FindResource("Input"), Text = value, Padding = new Thickness(8, 5, 8, 5), FontSize = 13 };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 58;
            box.MaxHeight = 160;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        else if (wrap) box.TextWrapping = TextWrapping.Wrap; // long product names stay readable
        box.TextChanged += (_, _) => { if (!building) { Write(key, box.Text); Store.Save(); } };
        box.ContextMenu = FieldMenu(key, box);
        Grid.SetColumn(box, 1);
        g.Children.Add(box);

        var clear = new Button { Style = (Style)FindResource("IconBtn"), Content = "", Width = 26, Height = 26, FontSize = 11, ToolTip = "Clear",
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(2, multiline ? 3 : 0, 0, 0) };
        clear.Click += (_, _) => { PushUndo(); box.Text = ""; };
        Grid.SetColumn(clear, 2);
        g.Children.Add(clear);

        row.Child = g;
        Rows.Children.Add(row);
        fields[key] = (row, box);
    }

    ContextMenu FieldMenu(string key, TextBox box)
    {
        var m = new ContextMenu();
        var move = new MenuItem { Header = "Move value to" };
        m.Opened += (_, _) =>
        {
            move.Items.Clear();
            foreach (var (k, label) in AllKeys().Where(k => k.key != key))
            {
                var target = k;
                var mi = new MenuItem { Header = label };
                mi.Click += (_, _) =>
                {
                    PushUndo();
                    var v = Read(key);
                    Write(target, v);
                    Write(key, "");
                    Build();
                    Flash(target);
                    Store.Save();
                };
                move.Items.Add(mi);
            }
            move.IsEnabled = box.Text.Length > 0;
        };
        m.Items.Add(move);
        var clear = new MenuItem { Header = "Clear" };
        clear.Click += (_, _) => { PushUndo(); box.Text = ""; };
        m.Items.Add(clear);
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = "Cut", Command = ApplicationCommands.Cut, CommandTarget = box });
        m.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = box });
        m.Items.Add(new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste, CommandTarget = box });
        return m;
    }

    IEnumerable<(string key, string label)> AllKeys()
    {
        yield return ("order", "Order ID");
        bool many = Sheet.Products.Count > 1;
        for (int i = 0; i < Sheet.Products.Count; i++)
        {
            yield return ($"name:{i}", many ? $"Product name {i + 1}" : "Product name");
            yield return ($"price:{i}", many ? $"Price {i + 1}" : "Price");
        }
        yield return ("date", "Date of purchase");
        yield return ("status", "Refund Status");
        yield return ("reason", "Reason");
    }

    string Read(string key)
    {
        var (kind, i) = Split(key);
        return kind switch
        {
            "order" => Sheet.OrderId, "date" => Sheet.Date, "status" => Sheet.Status, "reason" => Sheet.Reason,
            "name" => Sheet.Products[i].Name, "price" => Sheet.Products[i].Price, _ => "",
        };
    }

    void Write(string key, string v)
    {
        var (kind, i) = Split(key);
        switch (kind)
        {
            case "order": Sheet.OrderId = v; break;
            case "date": Sheet.Date = v; break;
            case "status": Sheet.Status = v; break;
            case "reason": Sheet.Reason = v; break;
            case "name": Sheet.Products[i].Name = v; break;
            case "price": Sheet.Products[i].Price = v; break;
        }
    }

    static (string kind, int index) Split(string key)
    {
        var c = key.IndexOf(':');
        return c < 0 ? (key, 0) : (key[..c], int.Parse(key[(c + 1)..]));
    }

    // ===================================================================== auto-fill

    void ToggleListening()
    {
        listening = !listening;
        Dot.SetResourceReference(Shape_Fill, listening ? "Good" : "Faint");
        Status.Text = listening ? "Listening — copy the order details in any order, they sort themselves" : "Paused — click here to listen again";
    }

    static readonly DependencyProperty Shape_Fill = System.Windows.Shapes.Shape.FillProperty;

    public void OnCopied(string text)
    {
        if (!listening || !IsVisible) return;
        var parts = RefundParser.Classify(text);
        if (parts.Count == 0) return;
        // Ignore a copy that is already on the sheet (copied twice). Prices may legitimately repeat across products.
        parts = parts.Where(p => p.field == RefundField.Price || AllKeys().All(k => !string.Equals(Read(k.key), p.value, StringComparison.Ordinal))).ToList();
        if (parts.Count == 0) { SetStatus("Already on the sheet"); return; }

        PushUndo();
        var filled = new List<string>();
        bool rebuilt = false;
        foreach (var (field, value) in parts)
        {
            var key = Place(field, value, ref rebuilt);
            filled.Add(key);
        }
        if (rebuilt) Build();
        else
        {
            building = true;
            foreach (var k in filled) fields[k].box.Text = Read(k);
            building = false;
        }
        foreach (var k in filled) Flash(k);
        Store.Save();
        var names = filled.Select(k => AllKeys().First(x => x.key == k).label).ToList();
        SetStatus($"✔ {string.Join(", ", names)}  ← {Shorten(text)}");
    }

    /// <summary>Put a value in the first free slot of its kind; returns the key it went to.</summary>
    string Place(RefundField field, string value, ref bool rebuilt)
    {
        var products = Sheet.Products;
        switch (field)
        {
            case RefundField.OrderId: Sheet.OrderId = value; return "order";
            case RefundField.Date: Sheet.Date = value; return "date";
            case RefundField.Status: Sheet.Status = value; return "status";
            case RefundField.Reason: Sheet.Reason = value; return "reason";
            case RefundField.ProductName:
            {
                var i = products.FindIndex(p => p.Name.Length == 0);
                if (i < 0) { products.Add(new()); i = products.Count - 1; rebuilt = true; }
                products[i].Name = value;
                return $"name:{i}";
            }
            default:
            {
                // Prefer the product that already has a name but no price, else the first empty price.
                var i = products.FindIndex(p => p.Price.Length == 0 && p.Name.Length > 0);
                if (i < 0) i = products.FindIndex(p => p.Price.Length == 0);
                if (i < 0) { products.Add(new()); i = products.Count - 1; rebuilt = true; }
                products[i].Price = value;
                return $"price:{i}";
            }
        }
    }

    void Flash(string key)
    {
        if (!fields.TryGetValue(key, out var f)) return;
        var c = Theme.P.Accent;
        var brush = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
        f.row.Background = brush;
        brush.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.6)) { BeginTime = TimeSpan.FromSeconds(0.4) });
    }

    void SetStatus(string text) => Status.Text = text;

    static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    // ===================================================================== undo / buttons

    void PushUndo()
    {
        undo.Push(JsonSerializer.Serialize(Sheet));
        if (undo.Count > 40)
        {
            var keep = undo.Take(30).Reverse().ToList();
            undo.Clear();
            foreach (var k in keep) undo.Push(k);
        }
        UndoBtn.IsEnabled = true;
    }

    void Undo()
    {
        if (undo.Count == 0) return;
        var prev = JsonSerializer.Deserialize<RefundSheet>(undo.Pop());
        if (prev == null) return;
        Sheet.OrderId = prev.OrderId;
        Sheet.Products = prev.Products.Count > 0 ? prev.Products : [new()];
        Sheet.Date = prev.Date;
        Sheet.Status = prev.Status;
        Sheet.Reason = prev.Reason;
        Build();
        Store.Save();
        UndoBtn.IsEnabled = undo.Count > 0;
        SetStatus("Undone");
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        Sheet.OrderId = Sheet.Date = Sheet.Status = Sheet.Reason = "";
        Sheet.Products = [new()];
        Build();
        Store.Save();
        SetStatus("Cleared — ready for the next refund (Ctrl+Z brings it back)");
    }

    void AddProduct_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        Sheet.Products.Add(new());
        Build();
        Store.Save();
    }

    void RemoveProduct_Click(object sender, RoutedEventArgs e)
    {
        if (Sheet.Products.Count <= 1) return;
        PushUndo();
        Sheet.Products.RemoveAt(Sheet.Products.Count - 1);
        Build();
        Store.Save();
    }

    void CopyValue(string key, string label)
    {
        var v = Read(key);
        if (v.Length == 0) return;
        var d = new DataObject();
        d.SetText(v);
        if (App.Clips.Set(d)) SetStatus($"Copied {label.TrimEnd(':')}");
    }

    void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var lines = new List<(string label, string value)> { ("Order ID:", Sheet.OrderId) };
        foreach (var p in Sheet.Products) { lines.Add(("Product name:", p.Name)); lines.Add(("Price:", p.Price)); }
        lines.Add(("Date of purchase:", Sheet.Date));
        lines.Add(("Refund Status:", Sheet.Status));
        lines.Add(("Reason:", Sheet.Reason));

        var plain = string.Join(Environment.NewLine, lines.Select(l => $"{l.label} {l.value}".TrimEnd()));
        var html = string.Join("<br>", lines.Select(l => $"<b>{WebUtility.HtmlEncode(l.label)}</b> {WebUtility.HtmlEncode(l.value).Replace("\n", "<br>")}"));
        var d = new DataObject();
        d.SetText(plain, TextDataFormat.UnicodeText);
        d.SetData(DataFormats.Html, CfHtml(html));
        if (App.Clips.Set(d)) SetStatus("Copied the whole sheet — headings are bold in rich-text editors");
    }

    /// <summary>Wrap an HTML fragment in the CF_HTML header Windows expects (offsets in UTF-8 bytes).</summary>
    static string CfHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body>\r\n<!--StartFragment-->", post = "<!--EndFragment-->\r\n</body></html>";
        int hl = string.Format(header, 0, 0, 0, 0).Length;
        int startFrag = hl + Encoding.UTF8.GetByteCount(pre);
        int endFrag = startFrag + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFrag + Encoding.UTF8.GetByteCount(post);
        return string.Format(header, hl, endHtml, startFrag, endFrag) + pre + fragment + post;
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInButton(d)) return;
        try { DragMove(); } catch { }
    }

    static bool IsInButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z && Keyboard.FocusedElement is not TextBox) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Escape) { WindowState = WindowState.Minimized; e.Handled = true; }
    }
}
