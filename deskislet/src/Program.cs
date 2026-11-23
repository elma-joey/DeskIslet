using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

[assembly: AssemblyTitle("桌屿")]
[assembly: AssemblyProduct("桌屿 · DeskIslet")]
[assembly: AssemblyDescription("桌面分区与 Dock")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace Deskfold
{
    class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                // Keep the previous workspace shortcut working. Installed copies start the host directly.
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                var host = Path.Combine(appDir, "Atrium", "Atrium.exe");
                if (!File.Exists(Path.Combine(appDir, "Deskfold.Core.dll")) && File.Exists(host)) {
                    string line = Environment.CommandLine.TrimStart();
                    int end = line[0] == '"' ? line.IndexOf('"', 1) + 1 : line.IndexOf(' ');
                    using (var child = Process.Start(new ProcessStartInfo(host, end < 0 ? "" : line.Substring(end).TrimStart()) { UseShellExecute = false })) {
                        if (Array.IndexOf(args, "--exit") >= 0) { child.WaitForExit(); return child.ExitCode; }
                    }
                    return 0;
                }
                uint guardPid;
                if (args.Length == 2 && args[0] == "--guard" && UInt32.TryParse(args[1], out guardPid))
                    return Board.DeskfoldRunCore("--guard " + guardPid);
                if (args.Length == 1 && args[0] == "--exit") return Board.DeskfoldRunCore("--exit");
                string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskfold");
                bool organize = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--organize") { organize = true; continue; }
                    if (args[i] == "--development") continue;
                    if (i + 1 >= args.Length) throw new ArgumentException("缺少目录参数。");
                    if (args[i] == "--folder") folder = Path.GetFullPath(args[++i]);
                    else if (args[i] == "--data") data = Path.GetFullPath(args[++i]);
                    else if (args[i] == "--ready-event") i++; // also parsed by the in-process native host
                    else throw new ArgumentException("未知参数 " + args[i]);
                }
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
                string id;
                using (var hash = SHA256.Create())
                    id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(data.TrimEnd('\\').ToUpperInvariant()))).Replace("-", "");
                bool first;
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Deskfold.Show." + id))
                using (var collect = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Deskfold.Organize." + id))
                using (var mutex = new Mutex(true, @"Local\Deskfold." + id, out first))
                {
                    if (!first) { (organize ? collect : signal).Set(); return 0; }
                    var board = new Board(folder, new Store(data), data);
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    var listener = ThreadPool.RegisterWaitForSingleObject(signal, (s, t) => dispatcher.BeginInvoke(new Action(board.ShowRules)), null, -1, false);
                    var organizer = ThreadPool.RegisterWaitForSingleObject(collect, (s, t) => dispatcher.BeginInvoke(new Action(board.OrganizeDesktop)), null, -1, false);
                    if (organize) collect.Set();
                    try { board.Run(); } finally { listener.Unregister(null); organizer.Unregister(null); }
                }
                return 0;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "桌屿启动失败"); return 1; }
        }
    }
}
