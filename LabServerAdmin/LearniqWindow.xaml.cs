using Microsoft.Web.WebView2.Core;
using System;
using System.Windows;

namespace LabServerAdmin
{
    public partial class LearniqWindow : Window
    {
        private readonly string _username;
        private const string Secret = "LSA-Learniq-Secret-2026";
        private const string LearniqBaseUrl = "http://localhost:5219";

        public LearniqWindow(string username)
        {
            _username = username;
            InitializeComponent();
            Loaded += LearniqWindow_Loaded;
        }

        private async void LearniqWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await WebView.EnsureCoreWebView2Async(null);

            // Navigate to AutoLogin endpoint — no need to type password again
            var url = $"{LearniqBaseUrl}/Account/AutoLogin" +
                      $"?token={Secret}" +
                      $"&username={Uri.EscapeDataString(_username)}";

            WebView.Source = new Uri(url);
        }

    }
}