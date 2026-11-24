using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Deskfold
{
    public sealed class DesktopShortcuts
    {
        readonly Store store;
        readonly DesktopStorage storage;
        public DesktopShortcuts(Store store, DesktopStorage storage) { this.store = store; this.storage = storage; }

        static bool IsLink(Entry e) { return !e.Folder && e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase); }

        public void RememberRestored(Entry e)
        {
            if (IsLink(e) && store.Data.DesktopShortcutExceptions != null && !store.Data.DesktopShortcutExceptions.Contains(e.Key))
                store.Change(s => s.DesktopShortcutExceptions.Add(e.Key));
        }

        // Return after a successful move: the next scan then includes its destination, even in a batch of installs.
        public bool CollectNext(List<Entry> files)
        {
            var desktop = files.Where(e => IsLink(e) && storage.OnDesktop(e.Path)).ToList();
            if (store.Data.DesktopShortcutExceptions == null) {
                store.Change(s => s.DesktopShortcutExceptions = desktop.Select(e => e.Key).ToList());
                return false;
            }
            foreach (var item in desktop.Where(e => !store.Data.DesktopShortcutExceptions.Contains(e.Key))) {
                try {
                    if (Collect(item, files)) return true;
                } catch (IOException) { /* Installer may still be writing. Retry on the next scan. */ }
                catch (UnauthorizedAccessException) { /* Leave inaccessible shortcuts in place. */ }
                catch (COMException) { /* An incomplete link can become readable on the next scan. */ }
            }
            return false;
        }

        bool Collect(Entry item, List<Entry> files)
        {
            Catalog.Classify(item, store.Data);
            if (item.Zone == null) return false;
            // An exclusive read also avoids collecting a shortcut while the installer has it open.
            using (var input = File.Open(item.Path, FileMode.Open, FileAccess.Read, FileShare.None))
                if (input.Length < 76) return false;
            var launch = ReadLaunch(item.Path);
            if (launch == null) return false;
            var current = Catalog.ResolveDrop(new[] { item.Path }, new[] { Path.GetDirectoryName(item.Path) });
            if (current == null || current[0].Key != item.Key || current[0].Modified != item.Modified) return false;
            foreach (var other in files.Where(e => IsLink(e) && !storage.OnDesktop(e.Path) &&
                String.Equals(e.Name, item.Name, StringComparison.OrdinalIgnoreCase))) {
                Catalog.Classify(other, store.Data);
                if (other.Zone != item.Zone) continue;
                string[] match;
                try { match = ReadLaunch(other.Path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                catch (COMException) { continue; }
                if (match != null && launch.SequenceEqual(match, StringComparer.Ordinal)) {
                    storage.StoreDuplicateShortcut(item);
                    return true;
                }
            }
            var path = storage.Collect(item);
            var moved = Catalog.ResolveDrop(new[] { path }, storage.Roots.ToArray());
            if (moved != null) {
                Catalog.Classify(moved[0], store.Data);
                if (moved[0].Zone != item.Zone) store.Change(s => s.Pins[moved[0].Key] = item.Zone);
            }
            return true;
        }

        static string[] ReadLaunch(string path)
        {
            uint flags;
            using (var reader = new BinaryReader(File.OpenRead(path))) {
                if (reader.BaseStream.Length < 76 || reader.ReadUInt32() != 76) return null;
                reader.BaseStream.Position = 20;
                flags = reader.ReadUInt32(); // includes RunAsUser and link resolution options
            }
            var link = (IShellLinkW)new ShellLink();
            try {
                ((IPersistFile)link).Load(path, 0);
                var text = new StringBuilder(32768);
                link.GetPath(text, text.Capacity, IntPtr.Zero, 0);
                string target = text.ToString();
                if (String.IsNullOrEmpty(target) || !Path.IsPathRooted(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
                link.GetArguments(text.Clear(), text.Capacity);
                string arguments = text.ToString();
                link.GetWorkingDirectory(text.Clear(), text.Capacity);
                string workingDirectory = text.ToString();
                int iconIndex, showCommand;
                short hotkey;
                link.GetIconLocation(text.Clear(), text.Capacity, out iconIndex);
                string icon = text.ToString();
                link.GetHotkey(out hotkey);
                link.GetShowCmd(out showCommand);
                link.GetDescription(text.Clear(), text.Capacity);
                return new[] { target.ToUpperInvariant(), arguments, workingDirectory.ToUpperInvariant(),
                    icon, iconIndex.ToString(), hotkey.ToString(), text.ToString(), showCommand.ToString(), flags.ToString() };
            } finally {
                Marshal.ReleaseComObject(link);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        // IShellLinkW methods must follow the Windows SDK vtable order, including unused setters.
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int capacity);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int capacity);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int command);
            void SetShowCmd(int command);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        }
    }
}
