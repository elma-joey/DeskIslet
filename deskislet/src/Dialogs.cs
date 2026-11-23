using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deskfold
{
    partial class Board
    {
        Window Dialog(string title, out StackPanel body)
        {
            var dialog = new Window { Title = title, Width = 400, SizeToContent = SizeToContent.Height,
                MaxHeight = SystemParameters.WorkArea.Height, Owner = win.IsVisible ? win : null, WindowStartupLocation = win.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = Brush("#29292D"), Foreground = Brush("#F2F2F7"),
                FontFamily = win.FontFamily, FontSize = 13 };
            dialog.Resources.MergedDictionaries.Add(win.Resources);
            body = new StackPanel { Margin = new Thickness(18) };
            dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            body.Children.Add(Label(title, 19, "#F5F5F7"));
            dialog.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) dialog.Close(); };
            return dialog;
        }

        static TextBox Field(string text)
        {
            return new TextBox { Text = text, Margin = new Thickness(0, 10, 0, 8), Padding = new Thickness(8),
                Background = Brush("#1C1C1E"), Foreground = Brushes.White, BorderBrush = Brush("#787880"),
                CaretBrush = Brushes.White, MaxLength = 80 };
        }

        static Button ActionButton(string text, Action action)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 8, 6, 0), HorizontalAlignment = HorizontalAlignment.Left };
            b.Click += delegate { action(); };
            return b;
        }

        void RenameItem(Entry item)
        {
            StackPanel panel;
            var dialog = Dialog("重命名", out panel);
            var input = Field("");
            input.MaxLength = 255;
            input.Text = item.Name;
            panel.Children.Add(input);
            var hint = Label("", 12, "#D1D1D6");
            panel.Children.Add(hint);
            var save = ActionButton("保存名称", () => {
                var current = Catalog.ResolveDrop(new[] { item.Path }, roots);
                if (current == null || current[0].Key != item.Key) { hint.Text = "项目已变化，请重新选择。"; return; }
                try { DesktopStorage.Rename(item, input.Text); }
                catch (System.IO.IOException ex) { hint.Text = ex.Message; return; }
                QueueScan(); dialog.Close();
            });
            save.IsDefault = true;
            panel.Children.Add(save);
            dialog.ContentRendered += delegate {
                input.Focus();
                input.Select(0, item.Folder ? item.Name.Length : System.IO.Path.GetFileNameWithoutExtension(item.Name).Length);
            };
            dialog.ShowDialog();
        }

        void NewZone()
        {
            StackPanel p;
            var d = Dialog("新建分区", out p);
            var input = Field("");
            input.MaxLength = 32;
            p.Children.Add(input);
            var hint = Label("给项目或常用的一类文件起个名字。", 12, "#C7C7CC");
            p.Children.Add(hint);
            var ok = ActionButton("创建分区", () => {
                var name = input.Text.Trim();
                if (name.Length == 0 || Catalog.Zones(store.Data).Contains(name, StringComparer.OrdinalIgnoreCase))
                { hint.Text = "请输入一个尚未使用的分区名称。"; return; }
                if (Save(s => { if (!s.DisabledZones.Remove(name)) s.Projects.Add(name); })) { d.Close(); Render(); }
            });
            ok.IsDefault = true;
            p.Children.Add(ok);
            d.ContentRendered += delegate { input.Focus(); };
            d.ShowDialog();
        }

        void DissolveZone(string zone)
        {
            if (!VisibleZones().Contains(zone)) return;
            try {
                Func<List<Entry>> collected = () => storage.Roots.Where(System.IO.Directory.Exists).SelectMany(Catalog.Scan)
                    .Where(e => { ClassifyForView(e); return e.Zone == zone; }).OrderBy(e => e.Path).ToList();
                var files = collected();
                int count = files.Count + entries.Count(e => e.Zone == zone && !storage.CanRestore(e.Path));
                string text = "解散「" + zone + "」？\n\n分区中有 " + count + " 项，其中 " + files.Count +
                    " 项会放回原桌面。其余项目保留在原位置。此分区的分类规则和手动归属会一并移除。";
                if (MessageBox.Show(text, "解散分区", MessageBoxButton.OKCancel, MessageBoxImage.Question,
                    MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                var current = collected();
                if (!current.Select(e => e.Key + e.Path).SequenceEqual(files.Select(e => e.Key + e.Path))) {
                    MessageBox.Show("收纳内容已有变化，请重新打开分区菜单确认。", "解散分区"); return;
                }
                foreach (var item in current) RestoreToDesktop(item);
                if (Save(s => Catalog.RemoveZone(s, zone))) {
                    if (lastTarget == zone) { lastMoved = null; lastTarget = null; }
                    Render();
                }
            } catch (Exception ex) { MessageBox.Show("分区已保留。已放回的文件可在原桌面找到。\n" + ex.Message, "解散未完成"); }
            finally { viewPending = true; QueueScan(); }
        }

        void LearnRule(Entry item, string zone)
        {
            StackPanel p;
            var d = Dialog("记住分类规则", out p);
            p.Children.Add(Label("文件名包含以下文字时，归入「" + zone + "」。", 13, "#D1D1D6"));
            var field = Field(Catalog.Suggest(item, zone));
            p.Children.Add(field);
            var summary = Label("", 12, "#96C7FF");
            p.Children.Add(summary);
            var list = new ListBox { Height = 144, Margin = new Thickness(0, 6, 0, 0), Background = Brush("#202023"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            p.Children.Add(list);
            var ok = ActionButton("保存规则", () => {
                string key = field.Text.Trim();
                if (key.Length == 0) return;
                if (Save(s => s.Rules.Add(new Rule { Keyword = key, Zone = zone })))
                { d.Close(); Message("已记住 · 名称包含「" + key + "」时归入「" + zone + "」。"); Render(); }
            });
            ok.IsDefault = true;
            p.Children.Add(ok);
            Action preview = () => {
                string key = field.Text.Trim();
                bool exists = store.Data.Rules.Any(r => String.Equals(r.Keyword, key, StringComparison.OrdinalIgnoreCase) && r.Zone == zone);
                ok.IsEnabled = key.Length > 0 && !exists;
                var trial = new Settings { Pins = store.Data.Pins, DisabledZones = store.Data.DisabledZones, SourceZones = store.Data.SourceZones, Rules = store.Data.Rules.Concat(new[] { new Rule { Keyword = key, Zone = zone } }).ToList() };
                var hits = entries.Where(e => Catalog.Matches(e.Name, key)).ToList();
                var lines = new List<string>();
                foreach (var e in hits)
                {
                    var copy = new Entry { Key = e.Key, Name = e.Name, Path = e.Path, Folder = e.Folder };
                    Catalog.Classify(copy, trial);
                    lines.Add(e.Name + "  →  " + copy.Zone + (store.Data.Pins.ContainsKey(e.Key) ? "（手动）" : copy.Reason.StartsWith("规则冲突") ? "（冲突）" : ""));
                }
                summary.Text = exists ? "这条规则已经保存。" : "当前匹配 " + hits.Count + " 项 · 保存后对新文件也生效";
                list.ItemsSource = lines;
            };
            field.TextChanged += delegate { preview(); };
            preview();
            d.ContentRendered += delegate { field.Focus(); field.SelectAll(); };
            d.ShowDialog();
        }

        void ManageRules()
        {
            StackPanel p;
            var d = Dialog("分类规则", out p);
            if (!store.Data.Rules.Any()) p.Children.Add(Label("拖动文件到分区后，点「记住规则」即可添加。", 13, "#C7C7CC"));
            foreach (var r in store.Data.Rules.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var del = ActionButton("移除", () => { if (Save(s => s.Rules.Remove(r))) { p.Children.Remove(row); Render(); } });
                DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);
                row.Children.Add(Label(r.Keyword + "  →  " + r.Zone, 13, "#D1D1D6"));
                p.Children.Add(row);
            }
            p.Children.Add(ActionButton("完成", d.Close));
            d.ShowDialog();
        }
    }
}
