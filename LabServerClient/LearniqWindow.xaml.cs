using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Windows;

namespace LabServerClient  // <- LabServerClient, hindi LabServerAdmin
{
    public partial class LearniqWindow : Window
    {
        private readonly string _url;

        public LearniqWindow(string url)
        {
            _url = url;
            InitializeComponent();
            Loaded += LearniqWindow_Loaded;
        }

        private async void LearniqWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LabServer", "WebView2Cache"
            );

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder
            );

            await WebView.EnsureCoreWebView2Async(env);

            WebView.CoreWebView2.Settings.IsScriptEnabled = true;

            WebView.Source = new Uri(_url);
        }
    }
}