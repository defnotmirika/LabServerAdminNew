using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LabServerAdmin.Services;

namespace LabServerAdmin
{
    public partial class App : Application
    {
        private IHost? _host;
        private DatabaseService? _databaseService;
        private ShowLearniq? _splashWindow; // ✅ ADDED

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _host = CreateHostBuilder().Build();
                _databaseService = _host.Services.GetRequiredService<DatabaseService>();

                _ = _databaseService.InitializeDatabaseAsync(); // ✅ CHANGED: removed await

                // ✅ CHANGED: Shows splash screen instead of login directly
                await Dispatcher.InvokeAsync(() =>
                {
                    _splashWindow = new ShowLearniq();
                    _splashWindow.AnimationCompleted += async (_, _) => await ShowLoginAndMainAsync();
                    _splashWindow.Show();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Failed to initialize application: {ex.Message}\n\n" +
                        "Please check your database connection settings in appsettings.json",
                        "Initialization Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                });
            }
        }

        // ✅ ADDED: New method — handles login + welcome text + main window
        private async Task ShowLoginAndMainAsync()
        {
            _splashWindow?.Close();
            _splashWindow = null;

            var loginWindow = new LoginWindow(_databaseService);
            bool? dialogResult = loginWindow.ShowDialog();

            if (loginWindow != null && loginWindow.IsAuthenticated && dialogResult == true)
            {
                var username = loginWindow.AuthenticatedUsername;
                var role = loginWindow.UserRole;
                var password = loginWindow.AuthenticatedPassword;
                var shouldShowWelcomeAndMicTest = false;

                // ✅ ADDED: Show WelcomeText for first-time INSTRUCTOR login
                if (_databaseService != null &&
                    !string.IsNullOrWhiteSpace(username) &&
                    string.Equals(role, "INSTRUCTOR", StringComparison.OrdinalIgnoreCase))
                {
                    shouldShowWelcomeAndMicTest = await _databaseService.ShouldShowWelcomeTextAsync(username);
                    if (shouldShowWelcomeAndMicTest)
                    {
                        var welcomeWindow = new WelcomeText();
                        welcomeWindow.AnimationCompleted += (_, _) => welcomeWindow.Close();
                        welcomeWindow.ShowDialog();
                        await _databaseService.MarkWelcomeTextShownAsync(username);
                    }
                }

                try
                {
                    var mainWindow = new MainWindow(username, role, password);
                    mainWindow.Show();
                    mainWindow.Activate();
                    mainWindow.Focus();

                    if (shouldShowWelcomeAndMicTest)
                    {
                        var micTestWindow = new MicTest
                        {
                            Owner = mainWindow
                        };
                        micTestWindow.ShowDialog();
                    }

                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error creating main window: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                }
            }
            else
            {
                Shutdown();
            }
        }

        private static IHostBuilder CreateHostBuilder() =>
            Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<DatabaseService>();
                });

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}