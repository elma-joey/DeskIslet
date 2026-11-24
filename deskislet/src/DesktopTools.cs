using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Deskfold
{
    public sealed class ToolPreferences
    {
        public bool Water { get; set; }
        public bool Stretch { get; set; }
        public int WaterMinutes { get; set; } = 45;
        public int StretchMinutes { get; set; } = 60;
        public List<InboxAccount> Accounts { get; set; } = new List<InboxAccount>();
    }

    partial class Board
    {
        ToolPreferences prefs;
        DispatcherTimer toolsTimer;
        readonly Dictionary<string,Window> toolNotices = new Dictionary<string,Window>();
        DateTime waterDue, stretchDue, lastTick, nextMail;
        IntPtr toolsOwner;
        const int SnipKey = 0x5341, PinKey = 0x5342;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
        [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Tick; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput value);

        void StartTools(IntPtr owner)
        {
            string file = Path.Combine(dataDirectory, "desktop-tools.json");
            prefs = File.Exists(file) ? new JavaScriptSerializer().Deserialize<ToolPreferences>(File.ReadAllText(file)) : new ToolPreferences();
            if (prefs == null) prefs = new ToolPreferences();
            if (prefs.Accounts == null) prefs.Accounts = new List<InboxAccount>();
            prefs.WaterMinutes = Math.Max(1,Math.Min(1440,prefs.WaterMinutes));
            prefs.StretchMinutes = Math.Max(1,Math.Min(1440,prefs.StretchMinutes));
            toolsOwner = owner;
            RegisterHotKey(owner, SnipKey, 0x4003, 0x70);  // Ctrl+Alt+F1
            RegisterHotKey(owner, PinKey, 0x4003, 0x72);   // Ctrl+Alt+F3
            ResetReminders(DateTime.UtcNow);
            toolsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            toolsTimer.Tick += (s,e) => TickTools(DateTime.UtcNow);
            toolsTimer.Start();
        }
        void StopTools()
        {
            if (toolsTimer != null) toolsTimer.Stop();
            mailLifetime.Cancel();
            if (toolsOwner != IntPtr.Zero) { UnregisterHotKey(toolsOwner,SnipKey); UnregisterHotKey(toolsOwner,PinKey); }
            foreach (var note in new List<Window>(toolNotices.Values)) note.Close();
        }
        void SavePreferences()
        {
            var file = Path.Combine(dataDirectory,"desktop-tools.json");
            File.WriteAllText(file+".tmp",new JavaScriptSerializer().Serialize(prefs),new UTF8Encoding(false));
            if(File.Exists(file)) File.Replace(file+".tmp",file,null); else File.Move(file+".tmp",file);
        }
        static void CaptureTool(bool pin)
        {
            try { if(pin) SnipPreview.PinClipboard(); else SnipOverlay.Begin(); }
            catch(Exception ex) { MessageBox.Show(ex.Message,"桌屿 · 截图"); }
        }
        void BeginCapture(bool pin)
        {
            var delay = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(240) };
            delay.Tick += delegate { delay.Stop(); if(!closed) CaptureTool(pin); }; delay.Start();
        }
        void ResetReminders(DateTime now)
        {
            lastTick=now;
            waterDue=now.AddMinutes(prefs.WaterMinutes);
            stretchDue=now.AddMinutes(prefs.StretchMinutes);
        }
        void TickTools(DateTime now)
        {
            var input=new LastInput {Size=8};
            uint idle=GetLastInputInfo(ref input) ? unchecked((uint)Environment.TickCount-input.Tick) : 0;
            CheckReminders(now,idle);
            if(now>=nextMail && !mailBusy && prefs.Accounts.Count>0) { nextMail=now.AddMinutes(2); RefreshInbox(); }
        }
        void CheckReminders(DateTime now,uint idle)
        {
            if (now < lastTick || now-lastTick > TimeSpan.FromMinutes(2)) ResetReminders(now);
            lastTick=now;
            if(idle >= 5*60000) stretchDue=now.AddMinutes(prefs.StretchMinutes);
            if(idle < 5*60000 && prefs.Water && now>=waterDue) {
                waterDue=now.AddMinutes(prefs.WaterMinutes);
                Notice("water","该喝水了","给自己倒杯水吧。","喝过了",null,() => waterDue=DateTime.UtcNow.AddMinutes(5));
            }
            if(prefs.Stretch && now>=stretchDue) {
                stretchDue=now.AddMinutes(prefs.StretchMinutes);
                Notice("stretch","起来活动一下","已经连续使用电脑一段时间了。","已活动",null,() => stretchDue=DateTime.UtcNow.AddMinutes(5));
            }
        }
        void Notice(string key,string title,string message,string ok,Action accept,Action snooze)
        {
            if(toolNotices.ContainsKey(key) || closed) return;
            StackPanel content;
            var note=Dialog(title,out content);
            note.Owner=null; note.Topmost=true; note.ShowActivated=false; note.Width=305;
            note.WindowStartupLocation=WindowStartupLocation.Manual;
            content.Children.Add(Label(message,13,"#D1D1D6"));
            var row=new StackPanel {Orientation=Orientation.Horizontal}; content.Children.Add(row);
            row.Children.Add(ActionButton(ok,() => { note.Close(); if(accept!=null) accept(); }));
            if(snooze!=null) row.Children.Add(ActionButton("5 分钟后提醒",() => {note.Close(); snooze();}));
            toolNotices.Add(key,note);
            var work=SystemParameters.WorkArea;
            note.Left=work.Right-note.Width-16; note.Top=work.Bottom-220-155*(toolNotices.Count-1);
            note.Closed += delegate {toolNotices.Remove(key);};
            note.Show();
        }
        Window reminderWindow;
        void ShowReminders()
        {
            if(reminderWindow!=null) {reminderWindow.Activate(); return;}
            StackPanel content;
            var dialog=Dialog("喝水与久坐提醒",out content);
            reminderWindow=dialog; dialog.Closed += delegate {reminderWindow=null;};
            var water=new CheckBox {Content="提醒喝水",IsChecked=prefs.Water,Foreground=dialog.Foreground,Margin=new Thickness(0,12,0,4)};
            var stretch=new CheckBox {Content="提醒起身活动",IsChecked=prefs.Stretch,Foreground=dialog.Foreground,Margin=water.Margin};
            var waterTime=Field(prefs.WaterMinutes.ToString()); var stretchTime=Field(prefs.StretchMinutes.ToString());
            content.Children.Add(water); content.Children.Add(Label("喝水间隔（分钟）",12,"#DDDDE2")); content.Children.Add(waterTime);
            content.Children.Add(stretch); content.Children.Add(Label("连续使用电脑（分钟）",12,"#DDDDE2")); content.Children.Add(stretchTime);
            var info=Label("离开电脑 5 分钟后，久坐计时重新开始。",12,"#CACAD0"); content.Children.Add(info);
            content.Children.Add(ActionButton("保存提醒",() => {
                int a,b;
                if(!int.TryParse(waterTime.Text,out a)||!int.TryParse(stretchTime.Text,out b)||a<1||a>1440||b<1||b>1440) {
                    info.Text="请输入 1 至 1440 之间的整数分钟。";return;
                }
                prefs.Water=water.IsChecked==true; prefs.Stretch=stretch.IsChecked==true;
                prefs.WaterMinutes=a; prefs.StretchMinutes=b;
                try {SavePreferences();} catch(IOException ex){info.Text=ex.Message;return;}
                ResetReminders(DateTime.UtcNow);
                foreach(var key in new[]{"water","stretch"}) if(toolNotices.ContainsKey(key)) toolNotices[key].Close();
                dialog.Close();
            }));
            dialog.Show(); dialog.Activate();
        }
    }
}
