using System.Windows;

namespace LabServerClient
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Show login window first
            var loginWindow = new LoginWindow();
            loginWindow.ShowDialog();

            // Only show main window if login was successful
            if (loginWindow.IsAuthenticated)
            {
                var clientWindow = new ClientWindow();
                clientWindow.Show();
            }
            else
            {
                // Exit application if login failed
                Shutdown();
            }
        }
    }
}
