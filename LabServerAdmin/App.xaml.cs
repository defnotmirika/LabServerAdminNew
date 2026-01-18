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

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Prevent application from shutting down automatically
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            
            // Disable all animations globally
            DisableAnimations();
            
            // Run async initialization
            _ = InitializeAsync();
        }

        /// <summary>
        /// Disables all WPF animations to prevent disruptions
        /// </summary>
        private void DisableAnimations()
        {
            // Set animation timeline speeds to 0
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline),
                new FrameworkPropertyMetadata { DefaultValue = 0 });
        }

        private async Task InitializeAsync()
        {
            try
            {
                // Setup dependency injection
                _host = CreateHostBuilder().Build();
                _databaseService = _host.Services.GetRequiredService<DatabaseService>();

                // Initialize database before showing login
                await _databaseService.InitializeDatabaseAsync();

                // Show login window first with database service on UI thread
                LoginWindow? loginWindow = null;
                bool? dialogResult = null;
                
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    loginWindow = new LoginWindow(_databaseService);
                    // Prevent animation effects during login window show
                    loginWindow.ShowDialog();
                    dialogResult = loginWindow.DialogResult;
                });

                // Only show main window if login was successful
                if (loginWindow != null && loginWindow.IsAuthenticated && dialogResult == true)
                {
                    var username = loginWindow.AuthenticatedUsername;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var mainWindow = new MainWindow(username);
                            mainWindow.Show();
                            mainWindow.Activate();
                            mainWindow.Focus();
                            
                            // Change shutdown mode to normal now that we have a main window
                            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
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
                    });
                }
                else
                {
                    // Exit application if login failed
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Shutdown();
                    });
                }
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
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

        private static IHostBuilder CreateHostBuilder() =>
            Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<DatabaseService>();
                    services.AddSingleton<TcpServerService>();
                    services.AddSingleton<VoiceRecognitionService>();
                });

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}
