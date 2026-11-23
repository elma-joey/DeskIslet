using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deskfold
{
    public sealed class WeatherCity
    {
        public string City { get; set; }
        public string Region { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public override string ToString() { return String.IsNullOrEmpty(Region) ? City : City + " · " + Region; }
    }

    static class CitySearch
    {
        sealed class Response { public Place[] results { get; set; } }
        sealed class Place {
            public string name { get; set; }
            public string admin1 { get; set; }
            public string admin2 { get; set; }
            public string country { get; set; }
            public double latitude { get; set; }
            public double longitude { get; set; }
        }
        static readonly HttpClient client = new HttpClient(new HttpClientHandler {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
        }) { Timeout = TimeSpan.FromSeconds(12) };

        public static async Task<WeatherCity[]> Search(string query, CancellationToken cancel)
        {
            var queries = new[] { query };
            if (query.Any(c => c >= '\u3400' && c <= '\u9fff')) {
                var stem = query.TrimEnd('市', '县', '区');
                queries = new[] { query, stem + "市", stem }.Where(s => s.Length > 0).Distinct().ToArray();
            }
            foreach (var name in queries) {
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=zh&format=json&name=" + Uri.EscapeDataString(name);
                using (var response = await client.GetAsync(url, cancel)) {
                    response.EnsureSuccessStatusCode();
                    var matches = new JavaScriptSerializer().Deserialize<Response>(await response.Content.ReadAsStringAsync()).results;
                    if (matches == null || matches.Length == 0) continue;
                    return matches.Select(p => new WeatherCity {
                        City = p.name, Latitude = p.latitude, Longitude = p.longitude,
                        Region = String.Join(" · ", new[] { p.admin2, p.admin1, p.country }.Where(s => !String.IsNullOrEmpty(s) && s != p.name).Distinct())
                    }).ToArray();
                }
            }
            return new WeatherCity[0];
        }
    }

    partial class Board
    {
        Window weatherDialog;
        void ChooseWeatherCity()
        {
            if (weatherDialog != null) { weatherDialog.Activate(); return; }
            StackPanel panel;
            var dialog = Dialog("天气城市", out panel);
            var path = Path.Combine(dataDirectory, "weather-city.json");
            var json = new JavaScriptSerializer();
            var current = "自动定位";
            try {
                if (File.Exists(path)) {
                    var saved = json.Deserialize<WeatherCity>(File.ReadAllText(path));
                    if (saved != null && !String.IsNullOrWhiteSpace(saved.City)) current = saved.ToString();
                }
            } catch (IOException) { current = "暂时无法读取"; }
              catch (ArgumentException) { current = "暂时无法读取"; }
            panel.Children.Add(Label("当前 · " + current, 13, "#D1D1D6"));
            var input = Field("");
            input.ToolTip = "输入城市名称或拼音";
            panel.Children.Add(input);
            var status = Label("输入城市名称或拼音，再点搜索。", 12, "#B8BEC8");
            var list = new ListBox { Height = 150, Margin = new Thickness(0, 8, 0, 0),
                Background = Brush("#202023"), Foreground = Brushes.White };
            CancellationTokenSource pending = null;
            var search = new Button { Content = "搜索", HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true, IsEnabled = false };
            search.Click += async delegate {
                if (pending != null) pending.Cancel();
                var request = pending = new CancellationTokenSource();
                string query = input.Text.Trim();
                list.ItemsSource = null;
                status.Text = "正在查找…";
                try {
                    var places = await CitySearch.Search(query, request.Token);
                    if (pending != request) return;
                    list.ItemsSource = places;
                    if (places.Length == 1) list.SelectedIndex = 0;
                    status.Text = places.Length == 0 ? "没有找到，试试完整市县名或拼音。" : "请核对省份和国家，选择要显示的地点。";
                } catch (OperationCanceledException) {
                    if (!request.IsCancellationRequested) status.Text = "搜索超时，请重试。";
                } catch (HttpRequestException) { if (pending == request) status.Text = "城市服务暂时无法连接，请重试。"; }
                  catch (ArgumentException) { if (pending == request) status.Text = "未能读取城市列表，请重试。"; }
                finally { if (pending == request) pending = null; request.Dispose(); }
            };
            input.TextChanged += delegate {
                if (pending != null) { pending.Cancel(); pending = null; }
                list.ItemsSource = null;
                search.IsEnabled = input.Text.Trim().Length > 0;
                status.Text = "输入城市名称或拼音，再点搜索。";
            };
            panel.Children.Add(search);
            panel.Children.Add(list);
            panel.Children.Add(status);
            Action<WeatherCity> save = city => {
                try {
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, json.Serialize(city), new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    dialog.Close();
                } catch (IOException) { status.Text = "写入设置失败，请重试。"; }
                  catch (UnauthorizedAccessException) { status.Text = "无法写入当前设置目录。"; }
            };
            var actions = new WrapPanel();
            var use = ActionButton("使用此城市", () => save((WeatherCity)list.SelectedItem));
            use.IsEnabled = false;
            list.SelectionChanged += delegate { use.IsEnabled = list.SelectedItem != null; };
            actions.Children.Add(use);
            actions.Children.Add(ActionButton("恢复自动定位", () => save(new WeatherCity { City = "" })));
            panel.Children.Add(actions);
            weatherDialog = dialog;
            dialog.ContentRendered += delegate { input.Focus(); };
            dialog.Closed += delegate { weatherDialog = null; if (pending != null) { pending.Cancel(); pending = null; } };
            dialog.ShowDialog();
        }
    }
}
