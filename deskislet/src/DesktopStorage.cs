using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Deskfold
{
    // One shelf per source desktop keeps public and personal items independently restorable.
    public sealed class DesktopStorage
    {
        readonly Dictionary<string, string> shelves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly string profile;
        public IEnumerable<string> Roots { get { return shelves.Values; } }

        public DesktopStorage(string profile, string[] desktops)
        {
            this.profile = Path.GetFullPath(profile);
            for (int i = 0; i < desktops.Length; i++) {
                var desktop = Path.GetFullPath(desktops[i]).TrimEnd(Path.DirectorySeparatorChar);
                // Keep moves on one volume, including redirected desktops.
                var shelf = String.Equals(Path.GetPathRoot(desktop), Path.GetPathRoot(this.profile), StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(this.profile, "收纳文件", i == 0 ? "个人桌面" : "公共桌面")
                    : Path.Combine(desktop, ".桌屿收纳");
                shelves[desktop] = shelf;
            }
        }

        public bool CanRestore(string path)
        {
            return shelves.Values.Contains(Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase);
        }

        public bool OnDesktop(string path) { return shelves.ContainsKey(Path.GetDirectoryName(path)); }

        public string StoreDuplicateShortcut(Entry item)
        {
            var shelf = shelves[Path.GetDirectoryName(item.Path)];
            bool redirected = Path.GetFileName(shelf) == ".桌屿收纳";
            var backup = redirected ? Path.Combine(shelf, ".重复快捷方式")
                : Path.Combine(profile, "重复快捷方式", Path.GetFileName(shelf));
            if (redirected) {
                Directory.CreateDirectory(shelf);
                File.SetAttributes(shelf, File.GetAttributes(shelf) | FileAttributes.Hidden);
                if (Directory.Exists(backup) && (File.GetAttributes(backup) & FileAttributes.Hidden) == 0)
                    throw new IOException("重复快捷方式目录已被占用。");
            }
            Directory.CreateDirectory(backup);
            if (redirected) File.SetAttributes(backup, File.GetAttributes(backup) | FileAttributes.Hidden);
            return MoveUnique(item.Path, backup, false);
        }

        public string Collect(Entry item)
        {
            string shelf;
            if (!shelves.TryGetValue(Path.GetDirectoryName(item.Path), out shelf)) return item.Path;
            var prefix = item.Path + Path.DirectorySeparatorChar;
            if (item.Folder && (item.Path.Equals(profile, StringComparison.OrdinalIgnoreCase) || profile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                AppDomain.CurrentDomain.BaseDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                Roots.Contains(item.Path, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("这个文件夹包含正在使用的桌屿程序或收纳目录。");
            Directory.CreateDirectory(shelf);
            if (Path.GetDirectoryName(shelf).Equals(Path.GetDirectoryName(item.Path), StringComparison.OrdinalIgnoreCase))
                File.SetAttributes(shelf, File.GetAttributes(shelf) | FileAttributes.Hidden);
            return MoveUnique(item.Path, shelf, item.Folder);
        }

        public string Restore(Entry item)
        {
            var shelf = Path.GetDirectoryName(item.Path);
            var desktop = shelves.First(pair => pair.Value.Equals(shelf, StringComparison.OrdinalIgnoreCase)).Key;
            return MoveUnique(item.Path, desktop, item.Folder);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string source, string destination, uint flags);

        public static void Rename(Entry item, string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.EndsWith(".") || name.EndsWith(" ")) throw new IOException("请输入有效的文件名。");
            if (item.Name == name) return;
            var destination = Path.Combine(Path.GetDirectoryName(item.Path), name);
            if (!MoveFileEx(item.Path, destination, 0)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        static string MoveUnique(string source, string directory, bool folder)
        {
            var name = Path.GetFileName(source);
            var stem = folder ? name : Path.GetFileNameWithoutExtension(name);
            var ext = folder ? "" : Path.GetExtension(name);
            for (int i = 1; ; i++) {
                var path = Path.Combine(directory, i == 1 ? name : stem + " (" + i + ")" + ext);
                // A same-volume rename, with neither replacement nor copy/delete fallback.
                if (MoveFileEx(source, path, 0)) return path;
                int error = Marshal.GetLastWin32Error();
                if (error == 80 || error == 183 || (error == 5 && (Directory.Exists(path) || File.Exists(path)))) continue;
                throw new IOException(new Win32Exception(error).Message);
            }
        }
    }
}
