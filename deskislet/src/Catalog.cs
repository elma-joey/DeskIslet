using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Deskfold
{
    public class Rule
    {
        public string Keyword { get; set; }
        public string Zone { get; set; }
    }

    public class Settings
    {
        public List<string> Projects { get; set; }
        public List<Rule> Rules { get; set; }
        public Dictionary<string, string> Pins { get; set; }
        public Dictionary<string, string> SourceZones { get; set; }
        public List<string> DisabledZones { get; set; }
        public Dictionary<string, bool> MenuZones { get; set; }
        // Null until the first desktop scan on this version.
        public List<string> DesktopShortcutExceptions { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public Settings()
        {
            Projects = new List<string>();
            Rules = new List<Rule>();
            Pins = new Dictionary<string, string>();
            SourceZones = new Dictionary<string, string>();
            DisabledZones = new List<string>();
            MenuZones = new Dictionary<string, bool>();
        }
    }

    public class Store
    {
        readonly string path;
        public Settings Data { get; private set; }

        public Store(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "layout.json");
            Data = File.Exists(path) ? Read(path) : new Settings();
            if (Data.SourceZones == null) Data.SourceZones = new Dictionary<string, string>();
            if (Data.DisabledZones == null) Data.DisabledZones = new List<string>();
            if (Data.MenuZones == null) Data.MenuZones = new Dictionary<string, bool>();
        }

        static Settings Read(string file)
        {
            using (var stream = File.OpenRead(file))
                return (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
        }

        public void Change(Action<Settings> change)
        {
            var next = new Settings {
                Projects = new List<string>(Data.Projects), Rules = new List<Rule>(Data.Rules),
                DisabledZones = new List<string>(Data.DisabledZones), MenuZones = new Dictionary<string, bool>(Data.MenuZones),
                DesktopShortcutExceptions = Data.DesktopShortcutExceptions == null ? null : new List<string>(Data.DesktopShortcutExceptions),
                Pins = new Dictionary<string, string>(Data.Pins), SourceZones = new Dictionary<string, string>(Data.SourceZones), Left = Data.Left, Top = Data.Top
            };
            change(next);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, next);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            Data = next;
        }
    }

    public class Entry
    {
        public string Path, Name, Key, Zone, Reason;
        public bool Folder;
        public DateTime Modified;
    }

    public static class Catalog
    {
        public const string Inbox = "待整理", Shots = "截图", Docs = "文档", Links = "常用入口";
        static readonly string[] Documents = { ".pdf", ".txt", ".md", ".rtf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".odt", ".ods", ".odp" };
        static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
        static readonly Regex ShotName = new Regex(@"^(屏幕截图|屏幕快照|截图|微信截图|企业微信截图|qq截图|screenshot|screen[ _-]?shot|snipaste)[\s_（(\-\d.]", RegexOptions.IgnoreCase);

        public static IEnumerable<string> Zones(Settings s)
        {
            return new[] { Inbox }.Concat(s.Projects).Concat(new[] { Shots, Docs, Links }).Distinct().Except(s.DisabledZones);
        }

        public static bool InStartMenu(Settings s, string zone)
        {
            bool placed;
            return s.MenuZones.TryGetValue(zone, out placed) ? placed : zone == Docs || zone == Links || zone == "文件夹";
        }

        public static void RemoveZone(Settings s, string zone)
        {
            s.Projects.Remove(zone);
            if ((zone == Inbox || zone == Docs || zone == Links || zone == Shots) && !s.DisabledZones.Contains(zone)) s.DisabledZones.Add(zone);
            s.Rules.RemoveAll(r => r.Zone == zone);
            foreach (var key in s.Pins.Where(p => p.Value == zone).Select(p => p.Key).ToList()) s.Pins.Remove(key);
            foreach (var key in s.SourceZones.Where(p => p.Value == zone).Select(p => p.Key).ToList()) s.SourceZones[key] = "";
            s.MenuZones.Remove(zone);
        }

        public static bool Matches(string name, string keyword)
        {
            return !String.IsNullOrWhiteSpace(keyword) && name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void Classify(Entry item, Settings s)
        {
            ClassifyByRule(item, s);
            if (s.DisabledZones.Contains(item.Zone)) {
                item.Zone = s.DisabledZones.Contains(Inbox) ? null : Inbox;
                item.Reason = "原分区已解散";
            }
        }

        static void ClassifyByRule(Entry item, Settings s)
        {
            string zone;
            if (s.Pins.TryGetValue(item.Key, out zone)) { item.Zone = zone; item.Reason = "你手动指定的分区"; return; }
            var hits = s.Rules.Where(r => Matches(item.Name, r.Keyword)).ToList();
            if (hits.Count > 0)
            {
                bool conflict = hits.Any(r => r.Zone != hits[0].Zone);
                item.Zone = conflict ? Inbox : hits[0].Zone;
                item.Reason = (conflict ? "规则冲突 · " : "名称包含 · ") + String.Join("、", hits.Select(r => r.Keyword));
                return;
            }
            if (item.Path != null && s.SourceZones != null && s.SourceZones.TryGetValue(Path.GetDirectoryName(item.Path), out zone) && !String.IsNullOrEmpty(zone)) {
                item.Zone = zone; item.Reason = "按已接管的收纳目录分类"; return;
            }
            string ext = Path.GetExtension(item.Name).ToLowerInvariant();
            if (item.Folder && s.SourceZones != null && s.SourceZones.ContainsValue("文件夹")) { item.Zone = "文件夹"; item.Reason = "按文件夹分类"; }
            else if (item.Folder || ext == ".lnk" || ext == ".url") { item.Zone = Links; item.Reason = "文件夹或快捷方式"; }
            else if (Images.Contains(ext) && ShotName.IsMatch(item.Name + " ")) { item.Zone = Shots; item.Reason = "截图命名与图片格式匹配"; }
            else if (Images.Contains(ext) && s.SourceZones != null && s.SourceZones.ContainsValue("图片")) { item.Zone = "图片"; item.Reason = "按图片格式分类"; }
            else if (Documents.Contains(ext)) { item.Zone = Docs; item.Reason = "按文档格式分类"; }
            else { item.Zone = Inbox; item.Reason = "尚无匹配规则"; }
        }

        public static List<Entry> Scan(string directory)
        {
            var items = new List<Entry>();
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var entry = FromInfo(info);
                if (entry != null) items.Add(entry);
            }
            return items.OrderByDescending(i => i.Modified).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static Entry FromInfo(FileSystemInfo info)
        {
            var attrs = info.Attributes;
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return null;
            string ext = info.Extension.ToLowerInvariant();
            if (ext == ".crdownload" || ext == ".part" || ext == ".tmp" || info.Name.StartsWith("~$")) return null;
            return new Entry { Name = info.Name, Path = info.FullName, Folder = (attrs & FileAttributes.Directory) != 0,
                Key = Identity(info), Modified = info.LastWriteTimeUtc };
        }

        // Explorer can start dragging a newly created shortcut before the watcher publishes a snapshot.
        public static List<Entry> ResolveDrop(string[] paths, string[] roots)
        {
            if (paths == null || paths.Length == 0) return null;
            var result = new List<Entry>();
            try {
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) {
                    if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return null;
                    var info = new FileInfo(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
                    if (!roots.Contains(info.DirectoryName, StringComparer.OrdinalIgnoreCase)) {
                        // Explorer also exposes the user's entire profile as a desktop shell icon.
                        if (info.FullName.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)) continue;
                        return null;
                    }
                    var item = FromInfo(info);
                    if (item == null) return null;
                    result.Add(item);
                }
            } catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            return result.Count == 0 ? null : result;
        }

        // File identity keeps manual corrections across a rename on a local Windows volume.
        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoNative
        {
            public uint Attr;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Access, Write;
            public uint Volume, SizeHi, SizeLo, Links, IndexHi, IndexLo;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sd, uint mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll")]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
        static string Identity(FileSystemInfo item)
        {
            using (var h = CreateFile(item.FullName, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                FileInfoNative f;
                if (!h.IsInvalid && GetFileInformationByHandle(h, out f) && (f.IndexHi != 0 || f.IndexLo != 0))
                    return String.Format("{0:x8}/{1:x8}{2:x8}/{3:x8}{4:x8}", f.Volume, f.IndexHi, f.IndexLo, f.Created.dwHighDateTime, f.Created.dwLowDateTime);
            }
            return item.FullName.ToUpperInvariant() + "|" + item.CreationTimeUtc.Ticks;
        }

        public static string Suggest(Entry e, string zone)
        {
            if (Matches(e.Name, zone)) return zone;
            string stem = Path.GetFileNameWithoutExtension(e.Name);
            string prefix = Regex.Split(stem, @"[\s_\-—（(\d]+")[0].Trim();
            return prefix.Length >= 2 ? prefix : stem;
        }
    }
}
