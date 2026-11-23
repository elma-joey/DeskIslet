using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Deskfold
{
    partial class Board
    {
        readonly string folder;
        readonly Store store;
        readonly Window win;
        readonly StackPanel sections;
        readonly ScrollViewer scroller;
        readonly TextBlock status;
        readonly DispatcherTimer debounce, reconcile;
        readonly HashSet<string> expanded = new HashSet<string>();
        List<Entry> entries = new List<Entry>();
        readonly string[] roots;
        readonly DesktopStorage storage;
        readonly DesktopShortcuts shortcuts;
        string dropOrigin;
        readonly Dictionary<string, FileSystemWatcher> watchers = new Dictionary<string, FileSystemWatcher>();
        readonly string[] desktops;
        Entry lastMoved;
        string lastTarget;
        bool scanning, dirty, dragging, closed;

        public Board(string folder, Store store, string data)
        {
            this.folder = folder; this.store = store; dataDirectory = data;
            var personal = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            desktops = String.Equals(folder, personal, StringComparison.OrdinalIgnoreCase) && Directory.Exists(shared)
                ? new[] { folder, shared } : new[] { folder };
            storage = new DesktopStorage(data, desktops);
            shortcuts = new DesktopShortcuts(store, storage);
            roots = desktops.Concat(store.Data.SourceZones.Keys).Concat(storage.Roots).Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shell.xaml"))
                win = (Window)XamlReader.Load(stream);
            sections = Get<StackPanel>("Sections");
            scroller = Get<ScrollViewer>("Scroller");
            status = Get<TextBlock>("Status");
            status.ToolTip = folder;
            var work = SystemParameters.WorkArea;
            win.Width = Math.Min(win.Width, work.Width);
            win.SizeToContent = SizeToContent.Height;
            win.MaxHeight = Math.Min(720, work.Height - 14);
            win.Left = Math.Max(work.Left, Math.Min(store.Data.Left ?? work.Left + 96, work.Right - win.Width));
            win.Top = Math.Max(work.Top, Math.Min(store.Data.Top ?? work.Top + 18, work.Bottom - win.MinHeight));
            win.SizeChanged += delegate {
                if (win.Top + win.ActualHeight > work.Bottom) win.Top = Math.Max(work.Top, work.Bottom - win.ActualHeight);
            };
            Get<Grid>("Header").MouseLeftButtonDown += (s, e) => {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                if (Mouse.Captured != null) return;
                win.DragMove();
                Save(cfg => { cfg.Left = win.Left; cfg.Top = win.Top; });
            };
            Get<Button>("Add").Click += delegate { NewZone(); };
            Get<Button>("More").Click += delegate {
                var menu = new ContextMenu();
                AddMenu(menu, "收起管理窗口", win.Hide);
                AddMenu(menu, "管理分类规则", ManageRules);
                AddMenu(menu, "一键整理桌面", OrganizeDesktop);
                AddMenu(menu, "天气城市…", ChooseWeatherCity);
                AddMenu(menu, "打开桌面文件夹", () => Open(folder));
                AddMenu(menu, "刷新分区", () => { viewPending = true; QueueScan(); });
                AddMenu(menu, "退出桌屿", win.Close);
                menu.PlacementTarget = Get<Button>("More"); menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
            };
            Get<Button>("Learn").Click += delegate { if (lastMoved != null) LearnRule(lastMoved, lastTarget); };
            Get<Button>("Dismiss").Click += delegate { Get<Border>("Notice").Visibility = Visibility.Collapsed; };
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            debounce.Tick += delegate { debounce.Stop(); Scan(); };
            reconcile = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            reconcile.Tick += delegate { QueueScan(); };
            win.Closed += delegate { closed = true; StopAtrium(); debounce.Stop(); reconcile.Stop(); foreach (var w in watchers.Values) w.Dispose(); };
        }

        public void Run()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose, MainWindow = win };
            app.Startup += delegate {
                new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
                Watch(); StartAtrium(); reconcile.Start(); Scan();
            };
            app.Run();
        }
        public void ShowRules() { ManageRules(); }
        public void Reveal() { win.Show(); win.WindowState = WindowState.Normal; win.Activate(); }
        T Get<T>(string name) where T : class { return (T)win.FindName(name); }
        static SolidColorBrush Brush(string color) { return (SolidColorBrush)new BrushConverter().ConvertFromString(color); }
        static TextBlock Label(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        }

        bool Save(Action<Settings> change)
        {
            try { store.Change(change); return true; }
            catch (Exception ex) { MessageBox.Show(win, ex.Message, "分区设置保存失败"); return false; }
        }

        public void OrganizeDesktop()
        {
            if (closed) return;
            var failures = new List<string>();
            int count = 0;
            foreach (var desktop in desktops) {
                try {
                    foreach (var item in Catalog.Scan(desktop)) {
                        try {
                            Catalog.Classify(item, store.Data);
                            if (item.Zone == null) continue;
                            var path = storage.Collect(item);
                            var result = Catalog.ResolveDrop(new[] { path }, roots);
                            if (result != null) {
                                var moved = result[0];
                                Catalog.Classify(moved, store.Data);
                                if (moved.Zone != item.Zone || (moved.Key != item.Key && store.Data.Pins.ContainsKey(item.Key)))
                                    store.Change(s => { s.Pins.Remove(item.Key); s.Pins[moved.Key] = item.Zone; });
                            }
                            count++;
                        } catch (Exception ex) { failures.Add(item.Name + " · " + ex.Message); }
                    }
                } catch (Exception ex) { failures.Add(desktop + " · " + ex.Message); }
            }
            viewPending = true; QueueScan();
            if (failures.Count > 0) MessageBox.Show("已收纳 " + count + " 项。未完成：" + String.Join("；", failures), "一键整理桌面");
        }

        void Watch()
        {
            foreach (var root in roots.Where(r => !watchers.ContainsKey(r) && Directory.Exists(r))) WatchRoot(root);
        }

        void WatchRoot(string root)
        {
            FileSystemWatcher watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes | NotifyFilters.LastWrite };
                FileSystemEventHandler changed = delegate { win.Dispatcher.BeginInvoke(new Action(QueueScan)); };
                watcher.Created += changed; watcher.Deleted += changed; watcher.Changed += changed;
                watcher.Renamed += delegate { win.Dispatcher.BeginInvoke(new Action(QueueScan)); };
                watcher.Error += delegate { win.Dispatcher.BeginInvoke(new Action(() => {
                    FileSystemWatcher active;
                    if (!closed && watchers.TryGetValue(root, out active) && active == watcher) {
                        watchers.Remove(root); watcher.Dispose(); QueueScan();
                    }
                })); };
                watcher.EnableRaisingEvents = true;
                watchers.Add(root, watcher);
            }
            catch (IOException) { if (watcher != null) watcher.Dispose(); watcher = null; }
            catch (ArgumentException) { if (watcher != null) watcher.Dispose(); }
            catch (UnauthorizedAccessException) { if (watcher != null) watcher.Dispose(); watcher = null; }
        }

        void QueueScan()
        {
            if (closed) return;
            dirty = true;
            if (!debounce.IsEnabled) debounce.Start();
        }

        async void Scan()
        {
            if (closed || scanning || dragging) { dirty = true; return; }
            scanning = true; dirty = false;
            try
            {
                Watch();
                var files = await Task.Run(() => roots.Where(Directory.Exists).SelectMany(Catalog.Scan)
                    .OrderByDescending(f => f.Modified).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList());
                if (closed || dragging) { dirty = true; return; }
                if (shortcuts.CollectNext(files)) { dirty = viewPending = true; return; }
                bool different = entries.Count != files.Count || entries.Where((e, n) => e.Path != files[n].Path || e.Key != files[n].Key || e.Modified != files[n].Modified).Any();
                if (different || viewPending || sections.Children.Count == 0) { entries = files; Render(); }
                status.Text = (roots.Any(r => Directory.Exists(r) && !watchers.ContainsKey(r)) ? "定时刷新" : "跟随桌面") + "  ·  " + entries.Count + " 项";
            }
            catch (Exception ex) { status.Text = "读取失败 · " + ex.Message; }
            finally { scanning = false; if (dirty && !closed && !dragging) QueueScan(); }
        }

        void ClassifyForView(Entry entry)
        {
            Catalog.Classify(entry, store.Data);
            // A later rule edit can orphan a file already on a shelf. Keep its restore action reachable.
            if (entry.Zone == null && storage.CanRestore(entry.Path)) entry.Zone = Catalog.Inbox;
        }

        IEnumerable<string> VisibleZones()
        {
            var zones = Catalog.Zones(store.Data);
            if (store.Data.DisabledZones.Contains(Catalog.Inbox) && entries.Any(e => e.Zone == Catalog.Inbox && storage.CanRestore(e.Path)))
                return new[] { Catalog.Inbox }.Concat(zones);
            return zones;
        }

        void Render()
        {
            double offset = scroller.VerticalOffset;
            foreach (var e in entries) ClassifyForView(e);
            PublishView();
            sections.Children.Clear();
            foreach (string z in VisibleZones()) sections.Children.Add(Section(z, entries.Where(e => e.Zone == z).ToList()));
            scroller.ScrollToVerticalOffset(offset);
        }

        Border Section(string zone, List<Entry> files)
        {
            var edge = Brush("#55555A");
            var card = new Border { Background = Brush("#243F3F43"), BorderBrush = edge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 7), Margin = new Thickness(0, 0, 0, 7), AllowDrop = true };
            var stack = new StackPanel(); card.Child = stack;
            var zoneMenu = new ContextMenu();
            AddMenu(zoneMenu, "解散分区…", () => DissolveZone(zone));
            card.ContextMenu = zoneMenu;
            var title = new DockPanel { Margin = new Thickness(2, 0, 2, 5) };
            var count = Label(files.Count.ToString("00"), 12, "#BEBEC5"); count.FontFamily = new FontFamily("Consolas");
            DockPanel.SetDock(count, Dock.Right); title.Children.Add(count);
            string tint = zone == Catalog.Inbox ? "#FF9F0A" : zone == Catalog.Shots ? "#BF5AF2" : zone == Catalog.Docs ? "#64D2FF" : "#89AFFF";
            string glyph = zone == Catalog.Inbox ? "\uE896" : zone == Catalog.Shots ? "\uEB9F" : zone == Catalog.Docs ? "\uE8A5" : zone == Catalog.Links ? "\uE71B" : "\uE8B7";
            var icon = Label(glyph, 16, tint); icon.FontFamily = new FontFamily("Segoe MDL2 Assets"); icon.Margin = new Thickness(0, 0, 8, 0);
            title.Children.Add(icon);
            var name = Label(zone, 15, "#F5F5F7"); name.FontWeight = FontWeights.SemiBold; title.Children.Add(name); stack.Children.Add(title);
            if (files.Count == 0) { var empty = Label("拖到这里", 11, "#ABABB3"); empty.Margin = new Thickness(26, 3, 0, 1); stack.Children.Add(empty); }
            int visible = expanded.Contains(zone) ? files.Count : 3;
            foreach (var f in files.Take(visible)) stack.Children.Add(FileRow(f));
            if (files.Count > 3)
            {
                var more = ActionButton(expanded.Contains(zone) ? "收起" : "展开其余 " + (files.Count - 3) + " 项", () => {
                    if (!expanded.Remove(zone)) expanded.Add(zone); Render();
                });
                more.Padding = new Thickness(6, 1, 6, 1); more.FontSize = 11; stack.Children.Add(more);
            }
            card.PreviewDragOver += (s, e) => {
                bool accepted = DropEntries(e.Data, false) != null;
                e.Effects = accepted ? ReferenceEffect(e.AllowedEffects) : DragDropEffects.None;
                card.BorderBrush = accepted ? Brush("#69AEFF") : edge;
                double y = e.GetPosition(scroller).Y;
                if (y < 24) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 14);
                if (y > scroller.ActualHeight - 24) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 14);
                e.Handled = true;
            };
            card.DragLeave += delegate { card.BorderBrush = edge; };
            card.Drop += (s, e) => {
                card.BorderBrush = edge;
                var filesDropped = DropEntries(e.Data, true);
                e.Effects = ReferenceEffect(e.AllowedEffects);
                if (filesDropped == null || e.Effects == DragDropEffects.None || !CollectFiles(filesDropped, zone)) e.Effects = DragDropEffects.None;
                e.Handled = true;
            };
            return card;
        }

        static DragDropEffects ReferenceEffect(DragDropEffects allowed)
        {
            if ((allowed & DragDropEffects.Link) != 0) return DragDropEffects.Link;
            return allowed & DragDropEffects.Copy;
        }

        List<Entry> DropEntries(IDataObject data, bool commit)
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (!commit && paths != null && paths.Length > 0) {
                var known = paths.Select(p => entries.FirstOrDefault(f => String.Equals(p, f.Path, StringComparison.OrdinalIgnoreCase))).ToList();
                if (known.All(f => f != null)) return known;
            }
            return Catalog.ResolveDrop(paths, roots);
        }

        FrameworkElement FileRow(Entry item)
        {
            var row = new Border { Padding = new Thickness(3, 4, 3, 4), Background = Brushes.Transparent, CornerRadius = new CornerRadius(4),
                Focusable = true, ToolTip = item.Name + "\n" + item.Reason };
            System.Windows.Automation.AutomationProperties.SetName(row, item.Name);
            row.MouseEnter += delegate { row.Background = Brush("#3A73737B"); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = Label(item.Folder ? "\uE8B7" : "\uE8A5", 13, "#AAAAB3"); icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            grid.Children.Add(icon);
            var name = Label(item.Name, 13, "#E5E5EA"); Grid.SetColumn(name, 1); grid.Children.Add(name);
            row.Child = grid;
            Point origin = new Point();
            row.MouseLeftButtonDown += (s, e) => {
                origin = e.GetPosition(win); row.Focus();
                if (e.ClickCount == 2) { Open(item.Path); e.Handled = true; }
                else row.CaptureMouse();
            };
            row.MouseLeftButtonUp += delegate { if (row.IsMouseCaptured) row.ReleaseMouseCapture(); };
            row.MouseMove += (s, e) => {
                if (!row.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
                Point p = e.GetPosition(win);
                if (Math.Abs(p.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                row.ReleaseMouseCapture();
                var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
                dragging = true;
                try { DragDrop.DoDragDrop(row, data, DragDropEffects.Copy); }
                finally { dragging = false; if (dirty) QueueScan(); }
            };
            row.KeyDown += (s, e) => { if (e.Key == Key.Enter) Open(item.Path); };
            var menu = new ContextMenu();
            AddMenu(menu, "打开", () => Open(item.Path));
            if (storage.CanRestore(item.Path)) AddMenu(menu, "放回原桌面", () => RestoreFile(item));
            var move = new MenuItem { Header = "移到分区" };
            foreach (string z in Catalog.Zones(store.Data)) { string target = z; AddMenu(move, z, () => Correct(item, target)); }
            menu.Items.Add(move);
            if (store.Data.Pins.ContainsKey(item.Key))
                AddMenu(menu, "恢复自动分类", () => { if (Save(s => s.Pins.Remove(item.Key))) Render(); });
            menu.Items.Add(new MenuItem { Header = item.Reason, IsEnabled = false });
            row.ContextMenu = menu;
            return row;
        }

        static void AddMenu(ItemsControl menu, string label, Action action)
        {
            var choice = new MenuItem { Header = label }; choice.Click += delegate { action(); }; menu.Items.Add(choice);
        }
        void Correct(Entry e, string zone)
        {
            CorrectFiles(new List<Entry> { e }, zone);
        }
        bool CollectFiles(List<Entry> files, string zone)
        {
            var currentFiles = Catalog.ResolveDrop(files.Select(f => f.Path).ToArray(), roots);
            if (currentFiles == null || !files.Select(f => f.Key).SequenceEqual(currentFiles.Select(f => f.Key))) {
                QueueScan(); MessageBox.Show("桌面项目已变化，请重新选择。", "未能收纳"); return false;
            }
            // Save the intended classification before moving anything. A same-volume move retains the file ID.
            var previous = store.Data.Pins;
            if (!Save(s => { foreach (var f in files) s.Pins[f.Key] = zone; })) return false;
            var failed = new List<Entry>();
            var errors = new List<string>();
            var moved = new List<Entry>();
            var newKeys = new Dictionary<string, string>();
            dropOrigin = null;
            foreach (var f in files) {
                try {
                    var path = storage.Collect(f);
                    var current = Catalog.ResolveDrop(new[] { path }, roots);
                    var entry = current == null ? f : current[0];
                    if (entry.Key != f.Key) newKeys[f.Key] = entry.Key;
                    if (moved.Count == 0) dropOrigin = f.Path;
                    moved.Add(entry);
                } catch (Exception ex) {
                    failed.Add(f);
                    errors.Add(f.Name + " · " + ex.Message);
                }
            }
            if (failed.Count > 0 || newKeys.Count > 0) Save(s => {
                foreach (var f in failed) { string z; if (previous.TryGetValue(f.Key, out z)) s.Pins[f.Key] = z; else s.Pins.Remove(f.Key); }
                foreach (var key in newKeys) { s.Pins.Remove(key.Key); s.Pins[key.Value] = zone; }
            });
            if (moved.Count > 0) {
                lastMoved = moved[0]; lastTarget = zone;
                Message((moved.Count == 1 ? lastMoved.Name : moved.Count + " 项") + "已归入「" + zone + "」。", moved.Count == 1);
            }
            viewPending = true; QueueScan();
            if (failed.Count > 0) MessageBox.Show(String.Join("；", errors), "未能收纳");
            return moved.Count > 0;
        }

        void RestoreToDesktop(Entry item)
        {
            shortcuts.RememberRestored(item);
            var path = storage.Restore(item);
            var result = Catalog.ResolveDrop(new[] { path }, desktops);
            if (result != null && result[0].Key != item.Key) shortcuts.RememberRestored(result[0]);
        }

        void RestoreFile(Entry item)
        {
            if (!storage.CanRestore(item.Path)) return;
            try {
                RestoreToDesktop(item);
                Save(s => s.Pins.Remove(item.Key));
                Message(item.Name + "已放回原桌面。");
            } catch (IOException ex) { MessageBox.Show(ex.Message, "放回桌面失败"); }
            QueueScan();
        }
        bool CorrectFiles(List<Entry> files, string zone)
        {
            if (!Save(s => { foreach (var e in files) s.Pins[e.Key] = zone; })) return false;
            lastMoved = files[0]; lastTarget = zone;
            Render(); QueueScan();
            Message((files.Count == 1 ? "「" + lastMoved.Name + "」" : files.Count + " 项") + "已归入「" + zone + "」。", files.Count == 1);
            return true;
        }

        void Message(string text, bool learn = false)
        {
            Get<TextBlock>("NoticeText").Text = text;
            Get<Button>("Learn").Visibility = learn ? Visibility.Visible : Visibility.Collapsed;
            Get<Border>("Notice").Visibility = Visibility.Visible;
        }

        void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Message("无法打开 · " + ex.Message); QueueScan(); }
        }
    }
}
