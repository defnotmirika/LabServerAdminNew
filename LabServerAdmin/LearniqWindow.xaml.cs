using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace LabServerAdmin  // o LabServerClient — pareho ang fix
{
    public partial class LearniqWindow : Window
    {
        private readonly string _url;
        private const string Secret = "LSA-Learniq-Secret-2026";

        // FIX 1: Accept full URL na (para magamit ng both admin at student)
        public LearniqWindow(string url)
        {
            _url = url;
            InitializeComponent();
            Loaded += LearniqWindow_Loaded;
        }

        private async void LearniqWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // FIX 2: Explicit UserDataFolder para hindi mag-temp folder
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LabServer", "WebView2Cache"
            );

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder
            );

            await WebView.EnsureCoreWebView2Async(env);

            // Optional: disable unnecessary restrictions
            WebView.CoreWebView2.Settings.IsScriptEnabled = true;
            WebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;

            WebView.Source = new Uri(_url);
        }
    }
}