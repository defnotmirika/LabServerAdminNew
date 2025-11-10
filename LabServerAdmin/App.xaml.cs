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

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                // Setup dependency injection
                _host = CreateHostBuilder().Build();
                _databaseService = _host.Services.GetRequiredService<DatabaseService>();

                // Initialize database before showing login
                await _databaseService.InitializeDatabaseAsync();

                // Show login window first with database service
                var loginWindow = new LoginWindow(_databaseService);
                var dialogResult = loginWindow.ShowDialog();

                // Only show main window if login was successful
                if (loginWindow.IsAuthenticated && dialogResult == true)
                {
                    var mainWindow = new MainWindow();
                    mainWindow.Show();
                }
                else
                {
                    // Exit application if login failed
                    Shutdown();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to initialize application: {ex.Message}\n\n" +
                    "Please check your database connection settings in appsettings.json",
                    "Initialization Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
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
