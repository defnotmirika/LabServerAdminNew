using System;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging; // ← ADDED
using LabServerAdmin.Services;

namespace LabServerAdmin
{
    public partial class App : Application
    {
        private IHost? _host;
        private DatabaseService? _databaseService;
        private ShowLearniq? _splashWindow;

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

                _ = _databaseService.InitializeDatabaseAsync();

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

        private async Task ShowLoginAndMainAsync()
        {
            _splashWindow?.Close();
            _splashWindow = null;

            var voiceSpeakerService = _host?.Services.GetRequiredService<VoiceSpeakerService>();
            var configuration = _host?.Services.GetRequiredService<IConfiguration>();
            while (true)
            {
                var loginWindow = new LoginWindow(
                    _databaseService,
                    voiceSpeakerService,
                    configuration);
                bool? dialogResult = loginWindow.ShowDialog();

                if (loginWindow == null || !loginWindow.IsAuthenticated || dialogResult != true)
                {
                    Shutdown();
                    return;
                }

                var username = loginWindow.AuthenticatedUsername;
                var role = loginWindow.UserRole;
                var password = loginWindow.AuthenticatedPassword;
                var isInstructor = !string.IsNullOrWhiteSpace(username) &&
                    string.Equals(role, "INSTRUCTOR", StringComparison.OrdinalIgnoreCase);

                var identifiers = (Username: (string?)null, EmpId: (string?)null);
                if (isInstructor && _databaseService != null && !string.IsNullOrWhiteSpace(username))
                {
                    identifiers = await _databaseService.GetInstructorIdentifiersAsync(username);
                }

                var hasProfile = HasVoiceAuthProfile(username) ||
                                 HasVoiceAuthProfile(identifiers.Username) ||
                                 HasVoiceAuthProfile(identifiers.EmpId);

                var shouldRunFirstTimeFlow = _databaseService != null &&
                    isInstructor &&
                    await _databaseService.ShouldShowWelcomeTextAsync(username!) &&
                    !hasProfile;

                if (shouldRunFirstTimeFlow)
                {
                    var welcomeWindow = new WelcomeText();
                    welcomeWindow.AnimationCompleted += (_, _) => welcomeWindow.Close();
                    welcomeWindow.ShowDialog();

                    var micTestWindow = new MicTest(voiceSpeakerService, configuration, username)
                    {
                        Owner = null
                    };

                    var micTestResult = micTestWindow.ShowDialog();
                    if (micTestResult != true)
                    {
                        MessageBox.Show(
                            "Voice enrollment is required to complete first-time setup.",
                            "Setup Incomplete",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        Shutdown();
                        return;
                    }

                    await _databaseService.MarkWelcomeTextShownAsync(username);
                }
                else if (_databaseService != null && isInstructor &&
                         await _databaseService.ShouldShowWelcomeTextAsync(username!))
                {
                    await _databaseService.MarkWelcomeTextShownAsync(username);
                }

                try
                {
                    var mainWindow = new MainWindow(username, role, password);
                    mainWindow.Show();
                    mainWindow.Activate();
                    mainWindow.Focus();

                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error creating main window: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }
        }

        private static bool HasVoiceAuthProfile(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return false;
            }

            var safeName = Regex.Replace(username.Trim().ToLowerInvariant(), "[^a-zA-Z0-9_\\-]", "_");
            var profileFile = $"{safeName}.json";
            var directCandidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Voice Auth", "speaker_profiles"),
                Path.Combine(AppContext.BaseDirectory, "Voice Auth", "voice_auth", "speaker_profiles"),
                Path.Combine(Directory.GetCurrentDirectory(), "Voice Auth", "speaker_profiles"),
                Path.Combine(Directory.GetCurrentDirectory(), "Voice Auth", "voice_auth", "speaker_profiles")
            };

            foreach (var candidate in directCandidates)
            {
                if (File.Exists(Path.Combine(candidate, profileFile)))
                {
                    return true;
                }
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            var maxLevels = 5;
            var currentLevel = 0;

            while (directory != null && currentLevel < maxLevels)
            {
                var candidate = Path.Combine(directory.FullName, "Voice Auth", "speaker_profiles", profileFile);
                if (File.Exists(candidate))
                {
                    return true;
                }

                candidate = Path.Combine(directory.FullName, "Voice Auth", "voice_auth", "speaker_profiles", profileFile);
                if (File.Exists(candidate))
                {
                    return true;
                }

                candidate = Path.Combine(directory.FullName, "LabServerAdmin", "Voice Auth", "speaker_profiles", profileFile);
                if (File.Exists(candidate))
                {
                    return true;
                }

                candidate = Path.Combine(directory.FullName, "LabServerAdmin", "Voice Auth", "voice_auth", "speaker_profiles", profileFile);
                if (File.Exists(candidate))
                {
                    return true;
                }

                directory = directory.Parent;
                currentLevel++;
            }

            return false;
        }

        // ← UPDATED: Added ConfigureLogging to remove EventLog
        private static IHostBuilder CreateHostBuilder() =>
            Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<DatabaseService>();
                    services.AddSingleton<VoiceSpeakerService>();
                });

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}