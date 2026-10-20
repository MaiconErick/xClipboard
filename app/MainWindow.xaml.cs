using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace xClipboard;

public partial class MainWindow : Window
{
    const string DragFormat = "xClipboard.Clip";
    static readonly string[] TabIcons = ["💼", "📁", "⭐", "📝", "💬", "🔗", "🧾", "🛒", "📧", "💻", "🎮", "🎃", "👻", "🦇", "💀", "🕷", "🔮", "🧪", "📦", "🏷"];

    ObservableCollection<ClipVM> items = new();
    ClipTab current;
    IntPtr prevForeground;
    IntPtr hwnd;

    // mouse / drag state
    Point downPoint;
    ClipVM? downVm;
    bool dragging;

    // last deletion (Ctrl+Z)
    (ClipItem item, ClipTab tab, int index)? lastDeleted;
    readonly DispatcherTimer hintTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    const string DefaultHint = "Enter paste · Ctrl+E edit · Ctrl+P pin · Del delete · right-click for more";

    public MainWindow()
    {
        InitializeComponent();
        current = Store.Data.Tabs.FirstOrDefault(t => t.Id == Store.Data.Settings.LastTab) ?? Store.History;
        List.ItemsSource = items;
        hwnd = new WindowInteropHelper(this).EnsureHandle();

        // While the popup is still being brought to the front, Windows can bounce activation (the very first
        // open after launch deactivates us from inside BringWindowToTop), so a deactivation that early is ignored;
        // the outside-click watch still closes the popup if the user clicks elsewhere.
        Deactivated += (_, _) => { if ((DateTime.Now - lastShown).TotalMilliseconds > ActivationGraceMs) HidePopup(); };
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnTextInput;
        Frame.SizeChanged += (_, _) => ClipHost.Clip = new RectangleGeometry(new Rect(0, 0, Frame.ActualWidth - 2, Frame.ActualHeight - 2), 15, 15);
        Theme.Changed += ApplyThemeExtras;
        App.Clips.HistoryChanged += () => { if (IsVisible && (current.IsHistory || Searching) && !items.Any(v => v.IsExpanded)) RefreshList(); };
        hintTimer.Tick += (_, _) => { hintTimer.Stop(); Hint.Text = DefaultHint; };
        outsideWatch.Tick += (_, _) => WatchOutsideClicks();
        ApplyThemeExtras();
    }

    bool Searching => Search.Text.Trim().Length > 0;

    void ApplyThemeExtras()
    {
        var p = Theme.P;
        HeaderSprites.Children.Clear();
        foreach (var id in p.Sprites.Take(2)) { var s = Sprites.Make(id, 22); s.Margin = new Thickness(0, 0, 4, 0); HeaderSprites.Children.Add(s); }
        if (p.Sprites.Length == 0) HeaderSprites.Children.Add(Sprites.Make("clip", 22));
        Ambient.Kind = Store.Data.Settings.Ambient ? p.Ambient : "off";
        TitleGlow.Color = p.Accent;
        PausedBadge.Visibility = Store.Data.Settings.Paused ? Visibility.Visible : Visibility.Collapsed;
        EmptySprite.Content = Sprites.Make(p.Sprites.FirstOrDefault() ?? "clip", 48);
    }

    // ===================================================================== show / hide

    DateTime lastHidden;

    public void Toggle()
    {
        if (IsVisible) HidePopup();
        // A tray click first deactivates (= hides) the popup; don't reopen it right away.
        else if ((DateTime.Now - lastHidden).TotalMilliseconds > 350) ShowPopup();
    }

    DateTime lastShown;
    const int ActivationGraceMs = 400;

    public void ShowPopup()
    {
        lastShown = DateTime.Now;
        var fg = Native.GetForegroundWindow();
        if (fg != hwnd) prevForeground = fg;
        if (Search.Text.Length > 0) Search.Text = "";
        PausedBadge.Visibility = Store.Data.Settings.Paused ? Visibility.Visible : Visibility.Collapsed;
        RefreshTabs();
        RefreshList(NewestItem());
        PlaceNearCaret();
        Show();
        Native.ForceForeground(hwnd);
        Activate();
        Search.Focus();
        Keyboard.Focus(Search);
        mouseWasDown = AnyMouseDown();
        outsideWatch.Start();
        // Windows sometimes refuses the first focus request; try again once things have settled,
        // and once more just before the activation grace period ends.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            foreach (var wait in new[] { 60, 250 })
            {
                await Task.Delay(wait);
                if (!IsVisible || IsActive) return;
                Native.ForceForeground(hwnd);
                Activate();
                Search.Focus();
                Keyboard.Focus(Search);
            }
        });
    }

    /// <summary>The popup always opens on the newest clip: in History that's the first one below the pinned block
    /// (new copies and re-copies land there); in other tabs it's the most recently created one.</summary>
    ClipItem? NewestItem()
    {
        var all = current.Items;
        if (current.IsHistory) return all.FirstOrDefault(i => !i.Pinned) ?? all.FirstOrDefault();
        return all.Where(i => i.Kind != ClipKind.Refund).MaxBy(i => i.Created) ?? all.FirstOrDefault();
    }

    // Closing on an outside click can't rely on Deactivated alone: if Windows never gave us focus, it never fires.
    readonly DispatcherTimer outsideWatch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    bool mouseWasDown;

    static bool AnyMouseDown() => Native.IsDown(Native.VK_LBUTTON) || Native.IsDown(Native.VK_RBUTTON) || Native.IsDown(Native.VK_MBUTTON);

    void WatchOutsideClicks()
    {
        if (!IsVisible) { outsideWatch.Stop(); return; }
        var down = AnyMouseDown();
        if (down && !mouseWasDown && !dragging && !Native.CursorOverOurWindow()) HidePopup();
        mouseWasDown = down;
    }

    /// <summary>Screenshot mode: fill the list without moving/activating the window.</summary>
    public void ShowPopupForTest(bool editFirst)
    {
        RefreshTabs();
        RefreshList();
        if (editFirst && items.Skip(1).FirstOrDefault() is { } vm) vm.BeginEdit();
        if (items.Count > 1) List.SelectedItem = items[1];
    }

    public void HidePopup()
    {
        if (Store.ReadOnly) return;
        if (!IsVisible) return;
        if (Prompt.Visibility == Visibility.Visible) Prompt.Visibility = Visibility.Collapsed;
        // Unsaved edits are kept rather than lost when the popup closes.
        foreach (var vm in items.Where(v => v.IsExpanded).ToList())
        {
            if (vm.IsNew && vm.EditTitle.Trim().Length == 0 && vm.EditText.Trim().Length == 0) { vm.Tab.Items.Remove(vm.Item); continue; }
            if (vm.Dirty || vm.IsNew) vm.Commit(); else vm.IsExpanded = false;
        }
        Store.Data.Settings.LastTab = current.Id;
        Store.Save();
        Hide();
        outsideWatch.Stop();
        lastHidden = DateTime.Now;
    }

    void PlaceNearCaret()
    {
        Native.POINT anchor;
        var caret = prevForeground != IntPtr.Zero ? Native.CaretPosition(prevForeground) : null;
        if (caret != null) anchor = caret.Value; else Native.GetCursorPos(out anchor);
        var (work, scale) = Native.MonitorAt(anchor);
        int w = (int)(Width * scale), h = (int)(Height * scale);
        int x = anchor.X - (int)(34 * scale);
        int y = caret != null ? anchor.Y + (int)(2 * scale) : anchor.Y - (int)(24 * scale);
        if (y + h > work.Bottom && caret != null) y = anchor.Y - h - (int)(22 * scale);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE | NOZORDER | NOACTIVATE
    }

    void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
        try { DragMove(); } catch { }
    }

    void Close_Click(object sender, RoutedEventArgs e) => HidePopup();
    void Refund_Click(object sender, RoutedEventArgs e) { HidePopup(); App.ShowRefund(); }

    // ===================================================================== tabs

    void RefreshTabs()
    {
        TabsPanel.Children.Clear();
        int n = 1;
        foreach (var tab in Store.Data.Tabs)
        {
            var t = tab;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = t.Icon, FontFamily = new FontFamily("Segoe UI Emoji"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = t.Name, VerticalAlignment = VerticalAlignment.Center });
            var count = t.IsHistory ? t.Items.Count : t.Items.Count(i => i.Kind != ClipKind.Refund);
            if (count > 0)
                content.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 10, Opacity = 0.6, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Tab"), Content = content, GroupName = "tabs", AllowDrop = true,
                IsChecked = t == current && !Searching, ToolTip = n <= 9 ? $"Ctrl+{n}" : null,
            };
            rb.Click += (_, _) => SelectTab(t);
            rb.MouseRightButtonUp += (_, e) => { TabMenu(t, rb); e.Handled = true; };
            rb.DragOver += (_, e) => { var vm = Dragged(e); e.Effects = vm != null && vm.Tab != t ? DragDropEffects.Move : DragDropEffects.None; rb.Tag = e.Effects == DragDropEffects.Move ? "drop" : null; e.Handled = true; };
            rb.DragLeave += (_, _) => rb.Tag = null;
            rb.Drop += (_, e) => { rb.Tag = null; if (Dragged(e) is { } vm && vm.Tab != t) { MoveToTab(vm, t); Flash($"Moved to {t.Name}"); } e.Handled = true; };
            TabsPanel.Children.Add(rb);
            n++;
        }
    }

    void SelectTab(ClipTab tab)
    {
        CommitOpenEdits();
        current = tab;
        if (Search.Text.Length > 0) Search.Text = ""; // also refreshes
        RefreshTabs();
        RefreshList();
        ScrollTabIntoView();
    }

    void ScrollTabIntoView()
    {
        var i = Store.Data.Tabs.IndexOf(current);
        if (i >= 0 && i < TabsPanel.Children.Count) ((FrameworkElement)TabsPanel.Children[i]).BringIntoView();
    }

    void TabScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    void AddTab_Click(object sender, RoutedEventArgs e) => NewTabPrompt(null);

    void NewTabPrompt(ClipVM? moveAfter)
    {
        string icon = "📁";
        ShowPrompt("New tab", moveAfter != null ? "The clip will be moved into it." : "Name it and pick an icon.", "",
            [("Cancel", false, _ => { }), ("Create", true, name =>
            {
                name = name.Trim();
                if (name.Length == 0) return;
                var tab = new ClipTab { Name = name, Icon = icon };
                Store.Data.Tabs.Add(tab);
                if (moveAfter != null) MoveToTab(moveAfter, tab);
                Store.Save();
                SelectTab(tab);
            })],
            TabIcons, i => icon = i);
    }

    void TabMenu(ClipTab tab, FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor };
        if (!tab.IsHistory)
        {
            m.Items.Add(Item("Rename…", "", () => ShowPrompt("Rename tab", "", tab.Name,
                [("Cancel", false, _ => { }), ("Rename", true, n => { if (n.Trim().Length > 0) { tab.Name = n.Trim(); Store.Save(); RefreshTabs(); } })])));
            var icons = new MenuItem { Header = "Icon" };
            foreach (var ic in TabIcons)
            {
                var mi = new MenuItem { Header = ic, FontFamily = new FontFamily("Segoe UI Emoji"), IsChecked = tab.Icon == ic };
                mi.Click += (_, _) => { tab.Icon = ic; Store.Save(); RefreshTabs(); };
                icons.Items.Add(mi);
            }
            m.Items.Add(icons);
            var idx = Store.Data.Tabs.IndexOf(tab);
            var left = Item("Move left", "", () => MoveTab(tab, -1)); left.IsEnabled = idx > 1;
            var right = Item("Move right", "", () => MoveTab(tab, 1)); right.IsEnabled = idx < Store.Data.Tabs.Count - 1;
            m.Items.Add(left);
            m.Items.Add(right);
            m.Items.Add(new Separator());
            m.Items.Add(Item("Delete tab…", "", () => DeleteTabPrompt(tab)));
        }
        else
        {
            m.Items.Add(Item("Clear history (keeps pinned)", "", ClearHistoryPrompt));
        }
        m.IsOpen = true;
    }

    void MoveTab(ClipTab tab, int dir)
    {
        var tabs = Store.Data.Tabs;
        var i = tabs.IndexOf(tab);
        var j = Math.Clamp(i + dir, 1, tabs.Count - 1); // History stays first
        if (i == j) return;
        tabs.RemoveAt(i);
        tabs.Insert(j, tab);
        Store.Save();
        RefreshTabs();
    }

    void DeleteTabPrompt(ClipTab tab)
    {
        var n = tab.Items.Count;
        ShowPrompt($"Delete \"{tab.Name}\"?", n == 0 ? "The tab is empty." : $"It holds {n} clip(s). Move them to History or delete them with the tab.", null,
            [("Cancel", false, _ => { }),
             ("Move clips to History", false, _ => DeleteTab(tab, true)),
             ("Delete all", true, _ => DeleteTab(tab, false))]);
    }

    void DeleteTab(ClipTab tab, bool keepClips)
    {
        if (keepClips)
        {
            var h = Store.History;
            foreach (var it in tab.Items.Where(i => i.Kind != ClipKind.Refund).Reverse())
            {
                it.Pinned = true; // they were pinned in their tab; keep them safe from trimming
                h.Items.Insert(0, it);
            }
        }
        Store.Data.Tabs.Remove(tab);
        if (current == tab) current = Store.History;
        Store.Save();
        Store.CleanImages();
        RefreshTabs();
        RefreshList();
    }

    void ClearHistoryPrompt()
    {
        ShowPrompt("Clear history?", "Every unpinned clip in History will be removed. Pinned clips and other tabs stay.", null,
            [("Cancel", false, _ => { }), ("Clear", true, _ =>
            {
                Store.History.Items.RemoveAll(i => !i.Pinned);
                Store.Save();
                Store.CleanImages();
                RefreshTabs();
                RefreshList();
            })]);
    }

    // ===================================================================== list

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
        RefreshTabChecks();
    }

    void RefreshTabChecks()
    {
        for (int i = 0; i < TabsPanel.Children.Count && i < Store.Data.Tabs.Count; i++)
            ((RadioButton)TabsPanel.Children[i]).IsChecked = Store.Data.Tabs[i] == current && !Searching;
    }

    void RefreshList(ClipItem? select = null)
    {
        select ??= (List.SelectedItem as ClipVM)?.Item;
        var q = Search.Text.Trim();
        var list = new List<ClipVM>();
        if (q.Length > 0)
        {
            foreach (var tab in Store.Data.Tabs)
                foreach (var it in tab.Items)
                    if (Matches(it, q)) list.Add(new ClipVM(it, tab) { ShowTabName = true });
        }
        else
        {
            foreach (var it in current.Items) list.Add(new ClipVM(it, current));
        }
        items = new ObservableCollection<ClipVM>(list);
        List.ItemsSource = items;
        var sel = items.FirstOrDefault(v => v.Item == select) ?? items.FirstOrDefault();
        List.SelectedItem = sel;
        if (sel != null) List.ScrollIntoView(sel);

        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0)
        {
            (EmptyTitle.Text, EmptyText.Text) = q.Length > 0 ? ("Nothing found", $"No clip in any tab matches \"{q}\".")
                : current.IsHistory ? ("Nothing copied yet", "Copy anything and it shows up here. Pin the keepers or right-click → Move to a tab.")
                : ("An empty tab", "Press New clip, or right-click a History clip → Move to → " + current.Name + ". Drag clips onto a tab works too.");
        }
    }

    static bool Matches(ClipItem it, string q) =>
        it.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || it.Text.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        (it.Kind == ClipKind.Refund && "refund".Contains(q, StringComparison.OrdinalIgnoreCase));

    ClipVM? Selected => List.SelectedItem as ClipVM;

    void Select(ClipVM vm)
    {
        List.SelectedItem = vm;
        List.ScrollIntoView(vm);
    }

    // ===================================================================== card mouse

    static ClipVM? VmOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipVM;

    void Card_Down(object sender, MouseButtonEventArgs e)
    {
        downVm = null;
        if (VmOf(sender) is not { } vm) return;
        if (e.OriginalSource is DependencyObject src && (FindAncestor<ButtonBase>(src) != null || FindAncestor<TextBox>(src) != null)) return;
        List.SelectedItem = vm;
        if (vm.IsExpanded) return;
        downVm = vm;
        downPoint = e.GetPosition(this);
    }

    void Card_Move(object sender, MouseEventArgs e)
    {
        if (downVm == null || dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - downPoint;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        var vm = downVm;
        dragging = true;
        try
        {
            var data = new DataObject();
            data.SetData(DragFormat, vm);
            if (vm.Item.Kind == ClipKind.Text) data.SetText(vm.Item.Text);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            dragging = false;
            downVm = null;
            ClearDropMarks();
        }
    }

    void Card_Up(object sender, MouseButtonEventArgs e)
    {
        var vm = downVm;
        downVm = null;
        if (vm == null || dragging || VmOf(sender) != vm || vm.IsExpanded) return;
        e.Handled = true;
        Paste(vm);
    }

    void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<TextBox>(src) != null) return;
        Select(vm);
        ClipMenu(vm, (FrameworkElement)sender).IsOpen = true;
        e.Handled = true;
    }

    // ---------- drag & drop reordering ----------

    static ClipVM? Dragged(DragEventArgs e) => e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as ClipVM : null;

    void Card_DragOver(object sender, DragEventArgs e)
    {
        var card = (FrameworkElement)sender;
        var target = VmOf(sender);
        var dragged = Dragged(e);
        if (dragged == null || target == null || dragged == target || Searching) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        bool after = e.GetPosition(card).Y > card.ActualHeight / 2;
        ClearDropMarks();
        var grid = (Grid)card.Parent;
        ((Border)grid.Children[after ? 1 : 0]).Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    void Card_DragLeave(object sender, DragEventArgs e) => ClearDropMarks();

    void Card_Drop(object sender, DragEventArgs e)
    {
        var card = (FrameworkElement)sender;
        ClearDropMarks();
        if (Dragged(e) is not { } dragged || VmOf(sender) is not { } target || dragged == target || Searching) return;
        bool after = e.GetPosition(card).Y > card.ActualHeight / 2;
        MoveRelative(dragged, target, after);
        e.Handled = true;
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = Dragged(e) != null && !Searching ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || Dragged(e) is not { } dragged || Searching) return;
        // dropped below the last card: move to the end of this tab
        var last = items.LastOrDefault();
        if (last != null && last != dragged) MoveRelative(dragged, last, true);
    }

    void ClearDropMarks()
    {
        foreach (var g in FindAll<Border>(List).Where(b => b.Height == 2 && b.Parent is Grid))
            g.Visibility = Visibility.Collapsed;
    }

    void MoveRelative(ClipVM dragged, ClipVM target, bool after)
    {
        var tab = target.Tab;
        dragged.Tab.Items.Remove(dragged.Item);
        var idx = tab.Items.IndexOf(target.Item) + (after ? 1 : 0);
        tab.Items.Insert(Math.Clamp(idx, 0, tab.Items.Count), dragged.Item);
        // In History, the pinned block stays on top: dropping inside it pins, below it unpins.
        if (tab.IsHistory) { dragged.Item.Pinned = target.Item.Pinned; NormalizeHistory(); }
        else dragged.Item.Pinned = true;
        Store.Save();
        RefreshTabs();
        RefreshList(dragged.Item);
    }

    static void NormalizeHistory()
    {
        var h = Store.History.Items;
        var ordered = h.Where(i => i.Pinned).Concat(h.Where(i => !i.Pinned)).ToList();
        h.Clear();
        h.AddRange(ordered);
    }

    // ===================================================================== actions

    async void Paste(ClipVM vm, bool plain = false)
    {
        if (vm.IsRefund) { HidePopup(); App.ShowRefund(); return; }
        if (!App.Clips.Put(vm.Item, plain)) { Flash("Couldn't open the clipboard, try again"); return; }
        vm.Item.Used = DateTime.Now;
        if (vm.Tab.IsHistory && !vm.Item.Pinned)
        {
            var h = Store.History.Items;
            h.Remove(vm.Item);
            h.Insert(h.Count(i => i.Pinned), vm.Item);
        }
        Store.Save();
        var target = prevForeground;
        HidePopup();
        if (target != IntPtr.Zero && Native.IsWindow(target)) Native.SetForegroundWindow(target);
        await Task.Delay(80);
        Native.SendPaste();
    }

    void CopyOnly(ClipVM vm)
    {
        if (App.Clips.Put(vm.Item)) Flash("Copied — paste it wherever you like");
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) TogglePin(vm);
    }

    void TogglePin(ClipVM vm)
    {
        if (vm.Tab.IsHistory)
        {
            var h = Store.History.Items;
            h.Remove(vm.Item);
            vm.Item.Pinned = !vm.Item.Pinned;
            // pinned → end of the pinned block; unpinned → top of the rest
            h.Insert(vm.Item.Pinned ? h.Count(i => i.Pinned) : h.Count(i => i.Pinned), vm.Item);
            Flash(vm.Item.Pinned ? "Pinned — it won't be trimmed and stays on top" : "Unpinned");
        }
        else
        {
            if (vm.IsRefund) { Flash("The refund sheet stays in its tab — move it instead"); return; }
            MoveToTab(vm, Store.History, pinned: false);
            Flash($"Unpinned from {vm.Tab.Name} — back in History");
        }
        Store.Save();
        RefreshTabs();
        RefreshList(vm.Item);
    }

    void MoveToTab(ClipVM vm, ClipTab tab, bool? pinned = null)
    {
        vm.Tab.Items.Remove(vm.Item);
        if (tab.IsHistory)
        {
            vm.Item.Pinned = pinned ?? true;
            var h = tab.Items;
            h.Insert(vm.Item.Pinned ? 0 : h.Count(i => i.Pinned), vm.Item);
        }
        else
        {
            vm.Item.Pinned = true; // clips in tabs are pinned automatically
            tab.Items.Insert(tab.Items.Count(i => i.Kind == ClipKind.Refund), vm.Item);
        }
        Store.Save();
        RefreshTabs();
        RefreshList();
    }

    void Move(ClipVM vm, string where)
    {
        var list = vm.Tab.Items;
        var i = list.IndexOf(vm.Item);
        if (i < 0) return;
        int lo = 0, hi = list.Count - 1;
        if (vm.Tab.IsHistory)
        {
            int pinned = list.Count(x => x.Pinned);
            if (vm.Item.Pinned) hi = pinned - 1; else lo = pinned;
        }
        int j = where switch { "top" => lo, "up" => i - 1, "down" => i + 1, _ => hi };
        j = Math.Clamp(j, lo, hi);
        if (j == i) return;
        list.RemoveAt(i);
        list.Insert(j, vm.Item);
        Store.Save();
        RefreshList(vm.Item);
    }

    void Delete(ClipVM vm)
    {
        var list = vm.Tab.Items;
        var i = list.IndexOf(vm.Item);
        if (i < 0) return;
        var pos = items.IndexOf(vm);
        list.RemoveAt(i);
        lastDeleted = (vm.Item, vm.Tab, i);
        Store.Save();
        RefreshTabs();
        RefreshList();
        if (items.Count > 0) Select(items[Math.Clamp(pos, 0, items.Count - 1)]);
        Flash("Deleted · Ctrl+Z to undo");
    }

    void UndoDelete()
    {
        if (lastDeleted is not { } d) return;
        if (!Store.Data.Tabs.Contains(d.tab)) d = (d.item, Store.History, 0);
        d.tab.Items.Insert(Math.Clamp(d.index, 0, d.tab.Items.Count), d.item);
        lastDeleted = null;
        Store.Save();
        RefreshTabs();
        RefreshList(d.item);
        Flash("Restored");
    }

    void Duplicate(ClipVM vm)
    {
        var copy = new ClipItem
        {
            Title = vm.Item.Title.Length > 0 ? vm.Item.Title + " (copy)" : "", Text = vm.Item.Text, Kind = vm.Item.Kind,
            Image = vm.Item.Image, Files = vm.Item.Files?.ToList(), Pinned = vm.Item.Pinned, Source = vm.Item.Source,
        };
        if (copy.Kind == ClipKind.Refund) return;
        vm.Tab.Items.Insert(vm.Tab.Items.IndexOf(vm.Item) + 1, copy);
        Store.Save();
        RefreshTabs();
        RefreshList(copy);
    }

    void New_Click(object sender, RoutedEventArgs e) => NewClip();

    void NewClip()
    {
        CommitOpenEdits();
        if (Searching) Search.Text = "";
        var it = new ClipItem { Pinned = true };
        var at = current.IsHistory ? 0 : current.Items.Count(i => i.Kind == ClipKind.Refund);
        current.Items.Insert(at, it);
        RefreshList(it);
        if (Selected is { } vm && vm.Item == it)
        {
            vm.IsNew = true;
            vm.BeginEdit();
            Select(vm);
        }
    }

    void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) BeginEdit(vm);
    }

    void BeginEdit(ClipVM vm)
    {
        if (!vm.CanEdit) return;
        CommitOpenEdits(except: vm);
        vm.BeginEdit();
        Select(vm);
    }

    void CommitOpenEdits(ClipVM? except = null)
    {
        foreach (var o in items.Where(v => v.IsExpanded && v != except).ToList()) SaveEdit(o);
    }

    void Collapse_Click(object sender, RoutedEventArgs e) { if (VmOf(sender) is { } vm) SaveEdit(vm); }
    void Save_Click(object sender, RoutedEventArgs e) { if (VmOf(sender) is { } vm) SaveEdit(vm); }
    void Cancel_Click(object sender, RoutedEventArgs e) { if (VmOf(sender) is { } vm) CancelEdit(vm); }
    void DeleteEdit_Click(object sender, RoutedEventArgs e) { if (VmOf(sender) is { } vm) { vm.IsExpanded = false; Delete(vm); } }

    void SaveEdit(ClipVM vm)
    {
        if (vm.IsNew && vm.EditTitle.Trim().Length == 0 && vm.EditText.Trim().Length == 0) { CancelEdit(vm); return; }
        vm.Commit();
        Store.Save();
        RefreshTabs();
        List.Focus();
        Search.Focus();
    }

    void CancelEdit(ClipVM vm)
    {
        vm.IsExpanded = false;
        if (vm.IsNew)
        {
            vm.Tab.Items.Remove(vm.Item);
            RefreshList();
        }
        Search.Focus();
    }

    void EditTitle_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox tb) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            tb.Focus();
            Keyboard.Focus(tb);
            tb.CaretIndex = tb.Text.Length;
        });
    }

    // ===================================================================== menus

    static MenuItem Item(string header, string? glyph, Action click, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (glyph != null) mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 12 };
        mi.Click += (_, _) => click();
        return mi;
    }

    ContextMenu ClipMenu(ClipVM vm, FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor };
        if (vm.IsRefund)
            m.Items.Add(Item("Open refund sheet", "", () => { HidePopup(); App.ShowRefund(); }, "Enter"));
        else
        {
            m.Items.Add(Item("Paste", "", () => Paste(vm), "Enter"));
            if (vm.Item.Kind == ClipKind.Files) m.Items.Add(Item("Paste paths as text", "", () => Paste(vm, true)));
            m.Items.Add(Item("Copy only", "", () => CopyOnly(vm)));
            m.Items.Add(Item("Edit", "", () => BeginEdit(vm), "Ctrl+E"));
        }
        m.Items.Add(new Separator());
        if (!vm.IsRefund)
            m.Items.Add(Item(vm.IsPinned ? (vm.Tab.IsHistory ? "Unpin" : "Unpin (back to History)") : "Pin", vm.IsPinned ? "" : "", () => TogglePin(vm), "Ctrl+P"));

        var moveTo = new MenuItem { Header = "Move to tab", Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12 } };
        foreach (var tab in Store.Data.Tabs.Where(t => t != vm.Tab && !(t.IsHistory && vm.IsRefund)))
        {
            var t = tab;
            var mi = new MenuItem { Header = $"{t.Icon}  {t.Name}" };
            mi.Click += (_, _) => { MoveToTab(vm, t); Flash($"Moved to {t.Name}"); };
            moveTo.Items.Add(mi);
        }
        if (moveTo.Items.Count > 0) moveTo.Items.Add(new Separator());
        moveTo.Items.Add(Item("New tab…", "", () => NewTabPrompt(vm)));
        m.Items.Add(moveTo);

        if (!Searching)
        {
            var order = new MenuItem { Header = "Organize", Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12 } };
            order.Items.Add(Item("Move to top", "", () => Move(vm, "top")));
            order.Items.Add(Item("Move up", "", () => Move(vm, "up"), "Alt+↑"));
            order.Items.Add(Item("Move down", "", () => Move(vm, "down"), "Alt+↓"));
            order.Items.Add(Item("Move to bottom", "", () => Move(vm, "bottom")));
            m.Items.Add(order);
        }
        if (!vm.IsRefund) m.Items.Add(Item("Duplicate", "", () => Duplicate(vm)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Delete", "", () => Delete(vm), "Del"));
        return m;
    }

    void Gear_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.Data.Settings;
        var m = new ContextMenu { PlacementTarget = GearBtn, Placement = PlacementMode.Bottom };
        var themes = new MenuItem { Header = "Theme", Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12 } };
        foreach (var p in Theme.All)
        {
            var pal = p;
            var mi = new MenuItem { Header = $"{pal.Emoji}  {pal.Name}", IsChecked = Theme.P.Id == pal.Id };
            mi.Click += (_, _) => App.SetTheme(pal.Id);
            themes.Items.Add(mi);
        }
        m.Items.Add(themes);
        var amb = Item("Animated background", null, () => { s.Ambient = !s.Ambient; Store.Save(); ApplyThemeExtras(); App.RefreshRefundTheme(); });
        amb.IsChecked = s.Ambient;
        m.Items.Add(amb);
        var pause = Item("Pause history capture", null, () => { s.Paused = !s.Paused; Store.Save(); ApplyThemeExtras(); });
        pause.IsChecked = s.Paused;
        m.Items.Add(pause);
        var size = new MenuItem { Header = $"History size ({s.HistoryLimit})", Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12 } };
        foreach (var n in new[] { 50, 100, 300, 500, 1000, 3000 })
        {
            var mi = new MenuItem { Header = $"{n} clips", IsChecked = s.HistoryLimit == n };
            mi.Click += (_, _) => { s.HistoryLimit = n; Store.Save(); };
            size.Items.Add(mi);
        }
        m.Items.Add(size);
        m.Items.Add(Item("Clear history (keeps pinned)", "", ClearHistoryPrompt));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Refund info sheet", "", () => { HidePopup(); App.ShowRefund(); }));
        m.Items.Add(Item("Open data folder", "", () => { HidePopup(); Process.Start("explorer.exe", Store.Dir); }));
        m.Items.Add(Item("About xClipboard", "", About));
        m.Items.Add(Item("Quit xClipboard", "", () => App.Quit()));
        m.IsOpen = true;
    }

    void About()
    {
        var hook = App.Hook.Installed ? "Win+V is handled by xClipboard." : "Win+V hook failed — Windows' own panel will open.";
        ShowPrompt("xClipboard", $"Version {App.Version}\n{hook}\nData: {Store.Dir}\n\nClips in tabs are pinned automatically. Right-click a clip to move or organize it; drag clips to reorder or onto a tab.",
            null, [("Close", true, _ => { })]);
    }

    // ===================================================================== prompt overlay

    void ShowPrompt(string title, string text, string? input, (string label, bool accent, Action<string> act)[] buttons,
        string[]? choices = null, Action<string>? choose = null)
    {
        PromptTitle.Text = title;
        PromptText.Text = text;
        PromptText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PromptInput.Visibility = input != null ? Visibility.Visible : Visibility.Collapsed;
        PromptInput.Text = input ?? "";
        PromptChoices.Children.Clear();
        if (choices != null)
        {
            foreach (var c in choices)
            {
                var ch = c;
                var b = new RadioButton { Style = (Style)FindResource("Tab"), GroupName = "choice", Margin = new Thickness(0, 0, 2, 2),
                    Content = new TextBlock { Text = ch, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 14 }, IsChecked = ch == "📁" };
                b.Click += (_, _) => choose?.Invoke(ch);
                PromptChoices.Children.Add(b);
            }
        }
        PromptButtons.Children.Clear();
        Button? def = null;
        foreach (var (label, accent, act) in buttons)
        {
            var a = act;
            var b = new Button { Content = label, Style = (Style)FindResource(accent ? "AccentBtn" : "Btn"), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => { Prompt.Visibility = Visibility.Collapsed; a(PromptInput.Text); Search.Focus(); };
            PromptButtons.Children.Add(b);
            if (accent) def = b;
        }
        promptDefault = def;
        Prompt.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (input != null) { PromptInput.Focus(); PromptInput.SelectAll(); }
            else def?.Focus();
        });
    }

    Button? promptDefault;

    void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && promptDefault != null)
        {
            promptDefault.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            e.Handled = true;
        }
    }

    void Flash(string text)
    {
        Hint.Text = text;
        hintTimer.Stop();
        hintTimer.Start();
    }

    // ===================================================================== keyboard

    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        // Typing anywhere searches (unless an editor has focus).
        if (Keyboard.FocusedElement is TextBox || Prompt.Visibility == Visibility.Visible || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        Search.Focus();
        Search.Text += e.Text;
        Search.CaretIndex = Search.Text.Length;
        e.Handled = true;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var focusedBox = Keyboard.FocusedElement as TextBox;
        bool inEditor = focusedBox != null && focusedBox != Search && focusedBox != PromptInput;
        var editing = items.FirstOrDefault(v => v.IsExpanded);

        if (Prompt.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape) { Prompt.Visibility = Visibility.Collapsed; Search.Focus(); e.Handled = true; }
            return;
        }

        if (key == Key.Escape)
        {
            if (editing != null) CancelEdit(editing);
            else if (Search.Text.Length > 0) Search.Text = "";
            else HidePopup();
            e.Handled = true;
            return;
        }
        if (editing != null && ctrl && (key == Key.S || key == Key.Enter)) { SaveEdit(editing); e.Handled = true; return; }
        if (inEditor) return;

        var sel = Selected;
        switch (key)
        {
            case Key.Enter when sel != null:
                Paste(sel, shift);
                break;
            case Key.Down when alt && sel != null: Move(sel, "down"); break;
            case Key.Up when alt && sel != null: Move(sel, "up"); break;
            case Key.Down:
            case Key.Up:
            case Key.PageDown:
            case Key.PageUp:
                if (items.Count == 0) break;
                int step = key is Key.PageDown or Key.PageUp ? 5 : 1;
                int i = sel == null ? 0 : items.IndexOf(sel) + (key is Key.Down or Key.PageDown ? step : -step);
                Select(items[Math.Clamp(i, 0, items.Count - 1)]);
                break;
            case Key.Delete when sel != null && (focusedBox != Search || Search.Text.Length == 0):
                Delete(sel);
                break;
            case Key.E when ctrl && sel != null:
            case Key.F2 when sel != null:
                BeginEdit(sel);
                break;
            case Key.P when ctrl && sel != null: TogglePin(sel); break;
            case Key.N when ctrl: NewClip(); break;
            case Key.Z when ctrl && (focusedBox != Search || Search.Text.Length == 0): UndoDelete(); break;
            case Key.F when ctrl: Search.Focus(); Search.SelectAll(); break;
            case Key.Tab when ctrl:
                var tabs = Store.Data.Tabs;
                SelectTab(tabs[(tabs.IndexOf(current) + (shift ? tabs.Count - 1 : 1)) % tabs.Count]);
                break;
            case >= Key.D1 and <= Key.D9 when ctrl:
                var n = key - Key.D1;
                if (n < Store.Data.Tabs.Count) SelectTab(Store.Data.Tabs[n]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ===================================================================== visual tree helpers

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindAll<T>(c)) yield return x;
        }
    }
}
