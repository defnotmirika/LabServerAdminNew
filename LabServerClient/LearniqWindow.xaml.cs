using Microsoft.Web.WebView2.Core;
using System;
using System.Windows;

namespace LabServerClient
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
            await WebView.EnsureCoreWebView2Async(null);
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.Source = new Uri(_url);
        }
    }
}