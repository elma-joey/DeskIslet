using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using ScreenRecorderLib;

namespace Deskfold
{
    sealed class RecorderPanel
    {
        public Window Window { get; private set; }
        public bool Active { get { return recorder != null; } }
        readonly Action<bool> dockState;
        readonly ComboBox sources;
        readonly Button action, folder;
        readonly TextBlock clock, heading, result;
        readonly DispatcherTimer timer;
        readonly Stopwatch elapsed = new Stopwatch();
        readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "桌屿录屏");
        Recorder recorder;
        string output, pending;
        bool stopRequested, started, closeWhenDone;
        Action exit;

        class Choice
        {
            public string Name { get; set; }
            public RecordingSourceBase Source { get; set; }
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int w, int h, uint flags);

        public RecorderPanel(string device, Action<bool> dockState)
        {
            this.dockState = dockState;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Recorder.xaml"))
                Window = (Window)XamlReader.Load(stream);
            sources = Get<ComboBox>("Sources");
            action = Get<Button>("Action");
            folder = Get<Button>("Folder");
            clock = Get<TextBlock>("Clock");
            heading = Get<TextBlock>("Heading");
            result = Get<TextBlock>("Result");
            var choices = new List<Choice>();
            foreach (var monitor in Recorder.GetDisplays().OrderByDescending(m => m.DeviceName == device))
                choices.Add(new Choice { Name = "屏幕 · " + monitor.FriendlyName + " (" + monitor.DeviceName.Split('\\').Last() + ")", Source = monitor });
            int pid = Process.GetCurrentProcess().Id;
            foreach (var window in Recorder.GetWindows().Where(w => w.Pid != pid && !String.IsNullOrWhiteSpace(w.Title)))
                choices.Add(new Choice { Name = "窗口 · " + window.Title, Source = window });
            sources.ItemsSource = choices;
            sources.SelectedIndex = choices.Count > 0 ? 0 : -1;
            action.IsEnabled = choices.Count > 0;
            if (choices.Count == 0) Report("没有找到可录制的屏幕或窗口。");

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += delegate { clock.Text = elapsed.Elapsed.ToString(@"hh\:mm\:ss"); };
            action.Click += delegate { if (Active) Stop(); else Start(); };
            folder.Click += delegate {
                try { Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
                catch (Exception ex) { Report(ex.Message); }
            };
            Get<DockPanel>("Header").MouseLeftButtonDown += (s, e) => {
                if (!e.Handled && e.OriginalSource != Get<Button>("Dismiss")) Window.DragMove();
            };
            Get<Button>("Dismiss").Click += delegate { Window.Close(); };
            Window.Closing += (s, e) => {
                if (Active) { e.Cancel = true; closeWhenDone = true; Stop(); }
            };
            Window.Loaded += delegate {
                var handle = new WindowInteropHelper(Window).Handle;
                Recorder.SetExcludeFromCapture(handle, true);
                var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(m => m.DeviceName == device);
                if (screen == null) return;
                var area = screen.WorkingArea;
                var scale = PresentationSource.FromVisual(Window).CompositionTarget.TransformToDevice;
                SetWindowPos(handle, IntPtr.Zero, area.Left + (area.Width - (int)(Window.ActualWidth * scale.M11)) / 2,
                    Math.Max(area.Top, area.Bottom - (int)((Window.ActualHeight + 86) * scale.M22)), 0, 0, 0x0015);
            };
        }

        T Get<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        void Report(string message) { result.Text = message; result.Visibility = Visibility.Visible; }
        public void ToggleFromDock()
        {
            if (Active) { Stop(); return; }
            Window.Show();
            Window.Activate();
        }

        void Start()
        {
            var choice = sources.SelectedItem as Choice;
            if (choice == null || Active) return;
            try {
                Directory.CreateDirectory(directory);
                string stem = "录屏 " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + " " + Guid.NewGuid().ToString("N").Substring(0, 8);
                output = Path.Combine(directory, stem + ".mp4");
                pending = Path.Combine(directory, stem + ".partial.mp4");
                var options = RecorderOptions.Default;
                options.SourceOptions.RecordingSources = new List<RecordingSourceBase> { choice.Source };
                options.VideoEncoderOptions.Framerate = 30;
                options.MouseOptions.IsMousePointerEnabled = true;
                options.MouseOptions.IsMouseClicksDetected = Get<CheckBox>("Clicks").IsChecked == true;
                options.MouseOptions.MouseClickDetectionDuration = 300;
                var audio = new List<AudioSourceBase>();
                if (Get<CheckBox>("SystemAudio").IsChecked == true) audio.Add(LoopbackAudioSource.Default);
                if (Get<CheckBox>("Mic").IsChecked == true) audio.Add(CaptureAudioSource.Default);
                options.AudioOptions.AudioSources = audio;
                options.AudioOptions.IsAudioEnabled = audio.Count > 0;
                recorder = Recorder.CreateRecorder(options);
                var session = recorder;
                session.OnStatusChanged += (s, e) => {
                    if (e.Status == RecorderStatus.Recording) Window.Dispatcher.BeginInvoke(new Action(() => {
                        if (recorder != session || started) return;
                        started = true;
                        elapsed.Restart(); timer.Start();
                        if (stopRequested) session.Stop();
                        else heading.Text = "正在录制";
                    }));
                };
                session.OnRecordingComplete += (s, e) => Window.Dispatcher.BeginInvoke(new Action(() => Finish(session, null)));
                session.OnRecordingFailed += (s, e) => Window.Dispatcher.BeginInvoke(new Action(() => Finish(session, e.Error ?? "编码失败")));
                Get<StackPanel>("Options").Visibility = folder.Visibility = result.Visibility = Visibility.Collapsed;
                heading.Text = "正在准备录制"; clock.Text = "00:00:00";
                action.Content = "停止并保存";
                dockState(true);
                session.Record(pending);
            } catch (Exception ex) { Finish(recorder, ex.GetBaseException().Message); }
        }

        void Stop()
        {
            if (!Active || stopRequested) return;
            stopRequested = true;
            heading.Text = "正在保存";
            action.IsEnabled = false;
            if (started) recorder.Stop();
        }

        public void StopBeforeExit(Action onFinished) { exit = onFinished; Stop(); }

        void Finish(Recorder session, string error)
        {
            if (recorder != session) return;
            recorder = null;
            timer.Stop(); elapsed.Reset();
            if (session != null) session.Dispose();
            dockState(false);
            started = stopRequested = false;
            try {
                if (error == null) File.Move(pending, output);
                else if (File.Exists(pending) && new FileInfo(pending).Length == 0) File.Delete(pending);
            } catch (IOException ex) { error = ex.Message; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; }
            heading.Text = "屏幕录制"; clock.Text = "";
            action.IsEnabled = true; action.Content = "开始录制";
            Get<StackPanel>("Options").Visibility = folder.Visibility = Visibility.Visible;
            Report(error == null ? "已保存到「桌屿录屏」\n" + Path.GetFileName(output) : "录制失败 · " + error);
            if (exit != null) exit();
            else if (closeWhenDone && error == null) Window.Close();
            closeWhenDone = false;
        }
    }
}
