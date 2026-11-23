using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Interop;

namespace Deskfold
{
    public sealed class ZoneView
    {
        public bool InStartMenu { get; set; }
        public string Name { get; set; }
        public List<FileView> Files { get; set; }
    }
    public sealed class FileView
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Folder { get; set; }
        public bool Collected { get; set; }
        public bool OnDesktop { get; set; }
        public string Modified { get; set; }
        public string DropOrigin { get; set; }
    }

    partial class Board
    {
        readonly string dataDirectory;
        readonly int processId = Process.GetCurrentProcess().Id;
        System.Threading.Thread coreThread;
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("Deskfold.Core.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int DeskfoldRunCore(string command);
        bool nativeReady;
        CodexQuota codexQuota;
        RecorderPanel recorderPanel;

        void ShowRecorder(string display)
        {
            try {
                if (recorderPanel == null) {
                    recorderPanel = new RecorderPanel(display, active => {
                        var host = FindWindow("AtriumCoreHost", null);
                        uint pid;
                        GetWindowThreadProcessId(host, out pid);
                        if (pid == processId) PostMessage(host, 0x8000 + 14, new IntPtr(active ? 1 : 0), IntPtr.Zero);
                    });
                    recorderPanel.Window.Closed += delegate { recorderPanel = null; };
                }
                recorderPanel.ToggleFromDock();
            } catch (Exception ex) { MessageBox.Show(ex.GetBaseException().Message, "无法打开屏幕录制"); }
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Packet { public IntPtr Type; public int Size; public IntPtr Text; }
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        void StartAtrium()
        {
            codexQuota = new CodexQuota(dataDirectory);
            win.Closing += (s, e) => {
                if (recorderPanel != null && recorderPanel.Active) {
                    e.Cancel = true;
                    recorderPanel.StopBeforeExit(win.Close);
                }
            };
            var owner = new WindowInteropHelper(win).Handle;
            HwndSource.FromHwnd(owner).AddHook(ReceiveAction);
            Environment.SetEnvironmentVariable("DESKFOLD_OWNER", owner.ToInt64().ToString());
            Environment.SetEnvironmentVariable("DESKFOLD_DATA", dataDirectory);
            PublishView();
            coreThread = new System.Threading.Thread(() => {
                string failure = null;
                try { if (DeskfoldRunCore("--development") != 0) failure = "原生桌面启动失败。"; }
                catch (Exception ex) { failure = ex.Message; }
                if (!win.Dispatcher.HasShutdownStarted) win.Dispatcher.BeginInvoke(new Action(() => {
                    if (closed) return;
                    if (failure == null) win.Close();
                    else { MessageBox.Show(failure, "桌屿启动失败"); win.Close(); }
                }));
            });
            coreThread.SetApartmentState(System.Threading.ApartmentState.STA);
            coreThread.Start();
        }

        void StopAtrium()
        {
            if (codexQuota != null) codexQuota.Stop();
            if (coreThread == null || !coreThread.IsAlive) return;
            // The window can close before the native UI thread has created its host.
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                while (coreThread.IsAlive) {
                    var host = FindWindow("AtriumCoreHost", null);
                    uint pid; GetWindowThreadProcessId(host, out pid);
                    if (host != IntPtr.Zero && pid == processId) { PostMessage(host, 0x10, IntPtr.Zero, IntPtr.Zero); return; }
                    System.Threading.Thread.Sleep(50);
                }
            });
        }
        void PublishView()
        {
            var zones = VisibleZones().Select(z => new ZoneView {
                Name = z, InStartMenu = Catalog.InStartMenu(store.Data, z), Files = entries.Where(f => f.Zone == z).Select(f => new FileView {
                    Name = f.Name, Path = f.Path, Folder = f.Folder, Collected = storage.CanRestore(f.Path),
                    OnDesktop = desktops.Contains(Path.GetDirectoryName(f.Path), StringComparer.OrdinalIgnoreCase),
                    Modified = f.Modified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    DropOrigin = lastMoved != null && f.Key == lastMoved.Key && z == lastTarget ? dropOrigin ?? "" : ""
                }).ToList()
            }).ToList();
            var path = Path.Combine(dataDirectory, "view.json");
            try {
                using (var file = File.Create(path + ".tmp")) new DataContractJsonSerializer(typeof(List<ZoneView>)).WriteObject(file, zones);
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                else File.Move(path + ".tmp", path);
            } catch (IOException) { QueueScan(); viewPending = true; return; }
            viewPending = false;
        }
        bool viewPending;

        IntPtr ReceiveAction(IntPtr h, int m, IntPtr sender, IntPtr l, ref bool handled)
        {
            if (m != 0x4A || l == IntPtr.Zero || closed) return IntPtr.Zero;
            uint pid; GetWindowThreadProcessId(sender, out pid);
            if (pid != processId) return IntPtr.Zero;
            var packet = (Packet)Marshal.PtrToStructure(l, typeof(Packet));
            if (packet.Type.ToInt64() != 0x44534631 || packet.Size <= 0 || packet.Size > 131072) return IntPtr.Zero;
            var fields = Marshal.PtrToStringUni(packet.Text, packet.Size / 2).Split(new[] {'\n'}, 3);
            if (fields.Length != 3) return IntPtr.Zero;
            string action = fields[0], zone = fields[1];
            var item = entries.FirstOrDefault(e => String.Equals(e.Path, fields[2], StringComparison.OrdinalIgnoreCase));
            handled = true;
            // Finish the operation outside Explorer's synchronous drag loop, including any error UI.
            if (action == "move" || action == "canMove") {
                if (!Catalog.Zones(store.Data).Contains(zone)) return IntPtr.Zero;
                var dropped = Catalog.ResolveDrop(fields[2].Split('\n'), roots);
                if (dropped == null) return IntPtr.Zero;
                if (action == "canMove") return new IntPtr(1);
                win.Dispatcher.BeginInvoke(new Action(() => CollectFiles(dropped, zone)));
            } else {
                win.Dispatcher.BeginInvoke(new Action(() => {
                    if (action == "ready" || action == "overflow") {
                        if (!nativeReady && action == "ready") win.Hide();
                        nativeReady = true;
                        if (action == "overflow") Message("网格空间不足。可缩小现有组件，再点刷新分区。");
                    } else if (action == "quotaRefresh") codexQuota.Refresh();
                    else if (action == "weatherCity") ChooseWeatherCity();
                    else if (action == "record") ShowRecorder(zone);
                    else if (action == "manage") ManageRules();
                    else if (action == "advanced") Reveal();
                    else if (action == "new") NewZone();
                    else if (action == "dissolve") DissolveZone(zone);
                    else if ((action == "desktopZone" || action == "menuZone") && VisibleZones().Contains(zone)) {
                        if (Save(s => s.MenuZones[zone] = action == "menuZone")) Render();
                    }
                    else if (item != null && item.Zone == zone) {
                        if (action == "open") Open(item.Path);
                        else if (action == "rename") RenameItem(item);
                        else if (action == "collect") CollectFiles(new List<Entry> { item }, zone);
                        else if (action == "restore") RestoreFile(item);
                        else if (action == "learn") LearnRule(item, zone);
                    }
                }));
            }
            return new IntPtr(1);
        }
    }
}
