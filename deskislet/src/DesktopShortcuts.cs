using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

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
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            dynamic link = null;
            try {
                link = shell.CreateShortcut(path);
                string target = link.TargetPath;
                if (String.IsNullOrEmpty(target) || !Path.IsPathRooted(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
                return new string[] { target.ToUpperInvariant(), link.Arguments, ((string)link.WorkingDirectory).ToUpperInvariant(),
                    link.IconLocation, link.Hotkey, link.Description, link.WindowStyle.ToString(), flags.ToString() };
            } finally {
                if (link != null) Marshal.ReleaseComObject((object)link);
                Marshal.ReleaseComObject((object)shell);
            }
        }
    }
}
