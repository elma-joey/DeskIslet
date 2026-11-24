using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
[assembly: AssemblyTitle("桌屿 · DeskIslet")]
internal static class Launcher
{
    [STAThread]
    static int Main(string[] args)
    {
        string command = Environment.CommandLine.TrimStart();
        int end = command[0] == '"' ? command.IndexOf('"', 1) + 1 : command.IndexOf(' ');
        var start = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeskIslet.exe"),
            end < 0 ? "" : command.Substring(end).TrimStart()) { UseShellExecute = false };
        using (var child = Process.Start(start)) {
            if (Array.IndexOf(args, "--exit") >= 0) { child.WaitForExit(); return child.ExitCode; }
        }
        return 0;
    }
}
