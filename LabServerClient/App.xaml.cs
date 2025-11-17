using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Configuration;
using LabServerClient.Services;

namespace LabServerClient
{
    public partial class App : Application
    {
        private DatabaseService? _databaseService;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Prevent application from shutting down automatically
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            
            // Run async initialization
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                // Setup configuration and database service
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                _databaseService = new DatabaseService(configuration);

                // Show login window first on UI thread
                LoginWindow? loginWindow = null;
                bool? dialogResult = null;
                
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    loginWindow = new LoginWindow(_databaseService);
                    dialogResult = loginWindow.ShowDialog();
                });

                // Only show main window if login was successful
                if (loginWindow != null && loginWindow.IsAuthenticated && dialogResult == true)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var clientWindow = new ClientWindow(_databaseService);
                            clientWindow.Show();
                            clientWindow.Activate();
                            clientWindow.Focus();
                            
                            // Change shutdown mode to normal now that we have a main window
                            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                $"Error creating client window: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
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
                        $"Failed to initialize application: {ex.Message}",
                        "Initialization Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                });
            }
        }
    }
}
