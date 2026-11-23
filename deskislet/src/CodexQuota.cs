using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Deskfold
{
    public class QuotaWindow
    {
        public string Label { get; set; }
        public double Remaining { get; set; }
        public string Reset { get; set; }
    }
    public class QuotaSnapshot
    {
        public string Status { get; set; }
        public string Updated { get; set; }
        public string AccountTag { get; set; }
        public List<QuotaWindow> Windows { get; set; }
        public QuotaSnapshot() { Windows = new List<QuotaWindow>(); }
    }

    public sealed class CodexQuota
    {
        readonly string path;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        DateTime nextAllowed;
        bool busy;

        public CodexQuota(string directory) { path = Path.Combine(directory, "codex-quota.json"); }
        public void Stop() { lifetime.Cancel(); }

        // Called on the UI dispatcher. Coalesce clicks while a read is outstanding.
        public async void Refresh()
        {
            if (busy || lifetime.IsCancellationRequested || DateTime.UtcNow < nextAllowed) return;
            busy = true;
            nextAllowed = DateTime.UtcNow.AddSeconds(5);
            try { await Task.Run(() => Update()); }
            catch (IOException) { /* A subsequent refresh retries publication. */ }
            catch (UnauthorizedAccessException) { }
            finally { busy = false; }
        }

        static string FindExecutable()
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            foreach (var dir in dirs.Where(Path.IsPathRooted)) {
                var exe = Path.Combine(dir.Trim('"'), "codex.exe");
                if (File.Exists(exe)) return exe;
            }
            // Explorer may have a different PATH from the Codex chat that built the widget.
            using (var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages")) {
                if (packages != null) foreach (var name in packages.GetSubKeyNames().Where(n => n.StartsWith("OpenAI.Codex_") && n.EndsWith("_2p2nqsd0c76g0"))) {
                    using (var app = packages.OpenSubKey(name)) {
                        string root = app == null ? null : app.GetValue("PackageRootFolder") as string;
                        if (String.IsNullOrEmpty(root)) continue;
                        string packaged = Path.Combine(root, "app", "resources", "codex.exe");
                        if (File.Exists(packaged)) return packaged;
                    }
                }
            }
            string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
            var exePath = Path.Combine(npm, @"node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe");
            return File.Exists(exePath) ? exePath : null;
        }

        static Dictionary<string,object> ObjectAt(Dictionary<string,object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string,object> : null;
        }
        static string StringAt(Dictionary<string,object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as string : null;
        }
        static decimal? Number(Dictionary<string,object> obj, string key)
        {
            object v;
            if (!obj.TryGetValue(key, out v) || !(v is int || v is long || v is decimal || v is double)) return null;
            return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        }

        public static List<QuotaWindow> WindowsFrom(Dictionary<string,object> result)
        {
            var limits = ObjectAt(ObjectAt(result, "rateLimitsByLimitId"), "codex") ?? ObjectAt(result, "rateLimits");
            var rows = new List<QuotaWindow>();
            foreach (string key in new[] { "primary", "secondary" }) {
                var w = ObjectAt(limits, key);
                if (w == null) continue;
                var used = Number(w, "usedPercent");
                if (!used.HasValue || used < 0 || used > 100) continue;
                var minutes = Number(w, "windowDurationMins");
                string label = key == "primary" ? "主额度" : "次额度";
                if (minutes > 0) label = minutes % 1440 == 0 ? (minutes / 1440) + " 天" : minutes % 60 == 0 ? (minutes / 60) + " 小时" : minutes + " 分钟";
                var epoch = Number(w, "resetsAt");
                string reset = epoch > 0 && epoch <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime().ToString("MM/dd HH:mm") : "";
                rows.Add(new QuotaWindow { Label = label, Remaining = (double)(100 - used.Value), Reset = reset });
            }
            return rows;
        }

        Dictionary<string,object> Call(Process p, int id, string method, object parameters)
        {
            p.StandardInput.WriteLine(json.Serialize(new { id = id, method = method, @params = parameters }));
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null) {
                var message = json.Deserialize<Dictionary<string,object>>(line);
                object value;
                if (!message.TryGetValue("id", out value) || !Object.Equals(value, id)) continue;
                return ObjectAt(message, "result");
            }
            throw new EndOfStreamException();
        }

        QuotaSnapshot Read(string exe, ref string tag)
        {
            if (exe == null) return new QuotaSnapshot { Status = "未找到本机 Codex" };
            var args = new ProcessStartInfo(exe, "app-server --listen stdio://") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(args))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using (timeout.Token.Register(() => Kill(p))) {
                    try {
                        p.BeginErrorReadLine();
                        if (Call(p, 1, "initialize", new { clientInfo = new { name = "deskfold", version = "1" } }) == null) throw new IOException();
                        p.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                        var accountResult = Call(p, 2, "account/read", new { refreshToken = false });
                        if (accountResult == null) throw new IOException();
                        var account = ObjectAt(accountResult, "account");
                        if (account == null) return new QuotaSnapshot { Status = "请先登录本机 Codex" };
                        var type = StringAt(account, "type");
                        if (type == "apiKey") return new QuotaSnapshot { Status = "API Key 模式无订阅额度" };
                        string email = StringAt(account, "email");
                        if (!String.IsNullOrEmpty(email))
                            using (var hash = SHA256.Create())
                                tag = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(email + "\n" + StringAt(account, "workspaceName") + "\n" + StringAt(account, "planType"))));
                        var result = Call(p, 3, "account/rateLimits/read", new { });
                        if (result == null) throw new IOException();
                        var windows = WindowsFrom(result);
                        if (windows.Count == 0) return new QuotaSnapshot { Status = "账号未返回额度窗口" };
                        return new QuotaSnapshot { Status = "已同步", Updated = DateTime.Now.ToString("MM/dd HH:mm"),
                            AccountTag = tag, Windows = windows };
                    } finally {
                        p.StandardInput.Close();
                        if (!p.WaitForExit(500)) Kill(p);
                    }
                }
            }
        }

        static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
        }

        void Update()
        {
            string tag = null;
            QuotaSnapshot state;
            try { state = Read(FindExecutable(), ref tag); }
            catch (Exception) {
                state = new QuotaSnapshot { Status = "同步失败，点击重试" };
                if (tag != null && File.Exists(path)) {
                    try {
                        var cached = json.Deserialize<QuotaSnapshot>(File.ReadAllText(path));
                        if (cached != null && cached.AccountTag == tag && cached.Windows != null && cached.Windows.Count > 0) {
                            state = cached;
                            state.Status = "更新失败 · 上次";
                        }
                    } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
                }
            }
            if (lifetime.IsCancellationRequested) return;
            File.WriteAllText(path + ".tmp", json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
        }
    }
}
