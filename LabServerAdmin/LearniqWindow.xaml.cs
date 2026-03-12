using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;

namespace LabServerAdmin
{
    public partial class LearniqWindow : Window
    {
        private readonly string _username;
        private const string Secret = "LSA-Learniq-Secret-2026";
        private const string LearniqBaseUrl = "http://localhost:5219";
        private const string LearniqAppPath = @"C:\Users\63930\source\repos\LearniqLearningToolApp\publish\LearniqLearningToolApp.exe";

        public LearniqWindow(string username)
        {
            _username = username;
            InitializeComponent();
            Loaded += LearniqWindow_Loaded;
        }

        private async void LearniqWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // I-check kung tumatakbo na ang LearnIQ server
            if (!IsLearniqRunning())
            {
                StartLearniqServer();

                // Hintayin na maging ready ang server
                bool isReady = await WaitForServerAsync();

                if (!isReady)
                {
                    MessageBox.Show(
                        "Could not start LearnIQ server.\n\nPlease make sure LearniqLearningToolApp is published.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Close();
                    return;
                }
            }

            await WebView.EnsureCoreWebView2Async(null);

            // Tanggalin ang WebView2 toolbar/controls
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            var url = $"{LearniqBaseUrl}/Account/AutoLogin" +
                      $"?token={Secret}" +
                      $"&username={Uri.EscapeDataString(_username)}";

            WebView.Source = new Uri(url);
        }

        private bool IsLearniqRunning()
        {
            var processes = Process.GetProcessesByName("LearniqLearningToolApp");
            return processes.Length > 0;
        }

        private void StartLearniqServer()
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = LearniqAppPath,
                    UseShellExecute = false,
                    CreateNoWindow = true // Hindi mag-aappear ang console window
                };
                Process.Start(processInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to start LearnIQ:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task<bool> WaitForServerAsync()
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(2);

            for (int i = 0; i < 10; i++) // Try 10 times (10 seconds max)
            {
                try
                {
                    var response = await httpClient.GetAsync(LearniqBaseUrl);

                    // 200 OK or 302 Redirect — server is ready na
                    if (response.IsSuccessStatusCode ||
                        response.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Hindi pa ready, try ulit
                }

                await Task.Delay(1000); // Wait 1 second bago mag-retry
            }

            return false;
        }

        private void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // Navigation finished — add any post-load logic here if needed
        }

        protected override void OnClosed(EventArgs e)
        {
            // OPTIONAL: I-kill ang LearnIQ server pag nagsara ang LearniqWindow
            // I-uncomment kung gusto mong ma-stop ang server pag nagsara:

            // var processes = Process.GetProcessesByName("LearniqLearningToolApp");
            // foreach (var p in processes) p.Kill();

            base.OnClosed(e);
        }
    }
}