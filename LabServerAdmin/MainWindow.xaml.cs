    using LabServerAdmin.Models;
    using LabServerAdmin.Services;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Win32;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media;
using System.Windows.Media.Imaging;
    using System.Windows.Threading;

    namespace LabServerAdmin
    {
        /// <summary>
        /// Interaction logic for MainWindow.xaml
        /// </summary>
        public partial class MainWindow : Window
        {
            private readonly IHost _host;
            private readonly DatabaseService _databaseService;
            private readonly TcpServerService _tcpServerService;
            private readonly VoiceRecognitionService _voiceRecognitionService;
        
            private ObservableCollection<ClientInfo> _connectedClients = new();
            private ObservableCollection<SystemLog> _systemLogs = new();
            private ObservableCollection<Computer> _computers = new();
            private ObservableCollection<AttendanceLog> _attendanceLogs = new();
            private ObservableCollection<ActivityLog> _activityLogs = new();
            private ObservableCollection<LoginRequest> _loginRequests = new();
            private ObservableCollection<InstructorClassListItem> _classList = new();

            private bool _isServerRunning = false;
            private readonly DispatcherTimer _clientsRefreshTimer;
            private readonly DispatcherTimer _uptimeTimer;
            private readonly DispatcherTimer _scheduleEndTimer;
            private readonly DispatcherTimer _inactivityTimer;
            private readonly DispatcherTimer _loginRequestsRefreshTimer;
            private DateTime _lastActivityTime;
            private int _sessionTimeoutMinutes = 15;
            private int _warningBeforeMinutes = 1;
            private bool _timeoutWarningShown = false;
            private DateTime? _serverStartTime = null;
            private TimeSpan? _scheduleEndTime = null;
            private bool _isVoiceEnabled = false;
            private readonly Dictionary<string, RemoteControlWindow> _remoteWindows = new();
            private readonly string? _currentAdminUsername;
            private readonly string? _currentAdminRole;
            private readonly string? _adminPassword; // Store password for lock screen
            private readonly Dictionary<string, System.Windows.Controls.Image> _screenThumbnails = new();
            private readonly Dictionary<string, DispatcherTimer> _screenTimers = new();


        private DateTime? _attendanceStartDate = null;
            private DateTime? _attendanceEndDate = null;
            private bool _showAbsentStudents = false;

            private DateTime? _activityStartDate = null;
            private DateTime? _activityEndDate = null;

            private DateTime? _systemLogsStartDate = null;
            private DateTime? _systemLogsEndDate = null;

            private DateTime? _classListDate = null;

            // Pagination for System Logs
            private int _systemLogsCurrentPage = 1;
            private int _systemLogsPageSize = 10;
            private int _systemLogsTotalPages = 1;
            private List<SystemLog> _allSystemLogs = new();

            // Pagination for Clients
            private int _clientsCurrentPage = 1;
            private int _clientsPageSize = 10;
            private int _clientsTotalPages = 1;
            private List<ClientInfo> _allClients = new();

            // Pagination for Computers
            private int _computersCurrentPage = 1;
            private int _computersPageSize = 10;
            private int _computersTotalPages = 1;
            private List<Computer> _allComputers = new();

            public MainWindow(string? adminUsername = null, string? adminRole = null, string? password = null)
            {
                _currentAdminUsername = adminUsername;
                _currentAdminRole = adminRole;
                _adminPassword = password; // Store password for lock screen authentication
                InitializeComponent();
            
                // Setup dependency injection
                _host = CreateHostBuilder().Build();
                _databaseService = _host.Services.GetRequiredService<DatabaseService>();
                _tcpServerService = _host.Services.GetRequiredService<TcpServerService>();
                _voiceRecognitionService = _host.Services.GetRequiredService<VoiceRecognitionService>();
            
                // Setup data binding
                ClientsDataGrid.ItemsSource = _connectedClients;
                SystemLogsDataGrid.ItemsSource = _systemLogs;
                ComputersDataGrid.ItemsSource = _computers;
                AttendanceLogsDataGrid.ItemsSource = _attendanceLogs;
                ActivityLogsDataGrid.ItemsSource = _activityLogs;
                LoginRequestsDataGrid.ItemsSource = _loginRequests;
                ClassListDataGrid.ItemsSource = _classList;

                // Setup event handlers
                SetupEventHandlers();
                // Auto-refresh connected clients list while server is running
                _clientsRefreshTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _clientsRefreshTimer.Tick += (s, e) =>
                {
                    if (_isServerRunning)
                    {
                        _ = RefreshConnectedClients();
                        _ = RefreshComputers(); // Also refresh computers to update online/offline status
                    }
                };
            
                // Setup uptime timer
                _uptimeTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _uptimeTimer.Tick += UptimeTimer_Tick;
            
                // Setup schedule end timer (checks every minute)
                _scheduleEndTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(1)
                };
                _scheduleEndTimer.Tick += ScheduleEndTimer_Tick;
            
                // Setup inactivity/session timeout timer
                _inactivityTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(30) // Check every 30 seconds
                };
                _inactivityTimer.Tick += InactivityTimer_Tick;
                _lastActivityTime = DateTime.Now;
            
                // Load timeout configuration from appsettings.json
                LoadSecuritySettings();
            
                // Capture all user activity to reset inactivity timer
                this.PreviewMouseMove += (s, e) => ResetInactivityTimer();
                this.PreviewKeyDown += (s, e) => ResetInactivityTimer();
                this.PreviewMouseDown += (s, e) => ResetInactivityTimer();
                this.PreviewMouseWheel += (s, e) => ResetInactivityTimer();
            
                // Start inactivity monitoring
                _inactivityTimer.Start();
            
                Loaded += MainWindow_Loaded;
                Closing += MainWindow_Closing;
            
                // Set window to fullscreen on startup (but still resizable)
                WindowState = WindowState.Maximized;

                UpdateStatus("Ready - Click 'Start Server' to begin");
            }

            private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
            {
                try
                {
                    await _databaseService.InitializeDatabaseAsync();
                    // Load system logs when application starts
                    await RefreshSystemLogs();
                    // Load computers when application starts
                    await RefreshComputers();
                    // Load attendance logs when application starts
                    await RefreshAttendanceLogs();
                    // Load activity logs when application starts
                    await RefreshActivityLogs();
                    // Load login requests when application starts
                    await RefreshLoginRequests();
                    // Load class list when application starts
                    await RefreshClassList();

                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    _voiceRecognitionService.SetCurrentUser(_currentAdminUsername);
                }
            }
                catch (Exception ex)
                {
                    MessageBox.Show($"Initialization error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"Initialization error: {ex.Message}");
                }
            }

            private void MainWindow_Closing(object? sender, CancelEventArgs e)
            {
                if (_isServerRunning)
                {
                    e.Cancel = true;
                    MessageBox.Show(
                        "Stop the server before exiting the admin application.",
                        "Server Still Running",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Activate();
                }
            }

            private static IHostBuilder CreateHostBuilder() =>
                Host.CreateDefaultBuilder()
                    .ConfigureServices((context, services) =>
                    {
                        services.AddSingleton<DatabaseService>();
                        services.AddSingleton<TcpServerService>();
                        services.AddSingleton<WakeOnLanService>();
                        services.AddSingleton<VoiceRecognitionService>();
                        services.AddSingleton<VoiceSpeakerService>();
                    });

            private void SetupEventHandlers()
            {
                _tcpServerService.ClientConnected += OnClientConnected;
                _tcpServerService.ClientDisconnected += OnClientDisconnected;
                _tcpServerService.CommandReceived += OnCommandReceived;
            
                _voiceRecognitionService.VoiceCommandRecognized += OnVoiceCommandRecognized;
                _voiceRecognitionService.RecognitionError += OnRecognitionError;
                _voiceRecognitionService.VoiceRejected += OnVoiceRejected;

        }

        #region Server Control Events

        private async void ServerToggleButton_Click(object sender, RoutedEventArgs e)
            {
                try
                {
                    if (!_isServerRunning)
                    {
                        await _tcpServerService.StartServerAsync();
                        _isServerRunning = true;
                        _serverStartTime = DateTime.Now;
                        _clientsRefreshTimer.Start();
                        _uptimeTimer.Start();
                        ServerToggleButton.Content = "⏹️ Stop Server";
                        ServerToggleButton.Style = (Style)FindResource("DangerButton");
                        ServerStatusIndicator.Fill = Brushes.Green;
                        ServerStatusTooltip.Content = "Server: Running on Port 9000";
                        UpdateStatus("Server started successfully");
                    
                        // Get schedule end time
                        if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                        {
                            _scheduleEndTime = await _databaseService.GetTodayScheduleEndTimeAsync(_currentAdminUsername);
                            if (_scheduleEndTime.HasValue)
                            {
                                _scheduleEndTimer.Start();
                                var endTimeStr = _scheduleEndTime.Value.ToString(@"hh\:mm");
                                MessageBox.Show(
                                    $"Server started successfully!\n\nPort: 9000\nStatus: Running\n\nSchedule End Time: {endTimeStr}\nServer will auto-stop at schedule end time.",
                                    "Server Started",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            }
                            else
                            {
                                MessageBox.Show(
                                    "Server started successfully!\n\nPort: 9000\nStatus: Running\n\nNote: No schedule found for today. Server will not auto-stop.",
                                    "Server Started",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            }
                        }
                        else
                        {
                            MessageBox.Show(
                                "Server started successfully!\n\nPort: 9000\nStatus: Running",
                                "Server Started",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    
                        // Record server start time for attendance tracking
                        if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                        {
                            await _databaseService.RecordServerStartAsync(_currentAdminUsername);
                        }
                    
                        // Log admin action
                        if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                        {
                            await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Start Server", "Server started on port 9000");
                        }
                    
                        // Refresh system logs and attendance logs when server starts
                        await RefreshSystemLogs();
                        await RefreshAttendanceLogs();
                    }
                    else
                    {
                        // Stop server manually
                        await StopServerAsync(isAutoStop: false);
                    
                        MessageBox.Show(
                            "Server stopped successfully!\n\nStatus: Stopped",
                            "Server Stopped",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Server error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"Server error: {ex.Message}");
                }
            }

            private async void VoiceToggleButton_Click(object sender, RoutedEventArgs e)
            {
                try
                {
                    if (!_isVoiceEnabled)
                    {
                        await _voiceRecognitionService.StartListeningAsync();
                        _isVoiceEnabled = true;
                        VoiceToggleButton.Content = "🎤 Voice Commands: ON";
                        VoiceToggleButton.Style = (Style)FindResource("SuccessButton");
                        UpdateStatus("Voice recognition enabled");
                    }
                    else
                    {
                        _voiceRecognitionService.StopListening();
                        _isVoiceEnabled = false;
                        VoiceToggleButton.Content = "🎤 Voice Commands: OFF";
                        VoiceToggleButton.Style = (Style)FindResource("ModernButton");
                        UpdateStatus("Voice recognition disabled");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Voice recognition error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"Voice recognition error: {ex.Message}");
                }
            }

        private void VoiceEnrollButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VoiceEnrollmentDialog(
                _host.Services.GetRequiredService<VoiceSpeakerService>(),
                _currentAdminUsername ?? "professor")
            {
                Owner = this
            };

            var result = dialog.ShowDialog();

            if (result == true && dialog.WasEnrolled)
            {
                // Activate speaker verification with the newly saved profile
                _voiceRecognitionService.SetCurrentUser(_currentAdminUsername);
                UpdateStatus($"✅ Voice profile enrolled — verification active for {_currentAdminUsername}");

                MessageBox.Show(
                    "Voice profile saved!\n\n" +
                    "✅ Speaker verification is now ACTIVE.\n\n" +
                    "Only your voice will be accepted when Voice Commands are ON.\n" +
                    "If commands are rejected, re-enroll from the Enroll Voice button.",
                    "Enrollment Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
            {
                var settingsDialog = new SessionSettingsDialog(_sessionTimeoutMinutes, _warningBeforeMinutes)
                {
                    Owner = this
                };

                var result = settingsDialog.ShowDialog();

                if (result == true && settingsDialog.WasSaved)
                {
                    // Update timeout settings
                    var previousTimeout = _sessionTimeoutMinutes;
                    var previousWarning = _warningBeforeMinutes;

                    _sessionTimeoutMinutes = settingsDialog.TimeoutMinutes;
                    _warningBeforeMinutes = settingsDialog.WarningMinutes;

                    // Save to appsettings.json
                    SaveTimeoutSettings();

                    // Log the change
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                    {
                        _ = _databaseService.LogAdminActionAsync(
                            _currentAdminUsername,
                            "Session Settings Changed",
                            $"Timeout changed from {previousTimeout} to {_sessionTimeoutMinutes} minutes, " +
                            $"warning changed from {previousWarning} to {_warningBeforeMinutes} minutes");
                    }

                    UpdateStatus($"Session timeout updated: {_sessionTimeoutMinutes} min (warning at {_sessionTimeoutMinutes - _warningBeforeMinutes} min)");

                    MessageBox.Show(
                        $"Session settings updated successfully!\n\n" +
                        $"Timeout: {_sessionTimeoutMinutes} minutes\n" +
                        $"Warning: {_warningBeforeMinutes} minute(s) before logout\n\n" +
                        $"These settings are now active.",
                        "Settings Updated",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }

        private void LearniqButton_Click(object sender, RoutedEventArgs e)
        {
            const string Secret = "LSA-Learniq-Secret-2026";
            // FIX 3: Huwag gamitin ang localhost — kumuha ng server IP sa registry
            string serverIp = "192.168.1.11"; // default fallback
            try
            {
                var regKey = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\LabServerClient");
                if (regKey != null)
                {
                    string? savedIp = regKey.GetValue("ServerIP")?.ToString();
                    if (!string.IsNullOrWhiteSpace(savedIp))
                        serverIp = savedIp;
                    regKey.Close();
                }
            }
            catch { }

            var url = $"http://{serverIp}:5219/Account/AutoLogin" +
                      $"?token={Secret}" +
                      $"&username={Uri.EscapeDataString(_currentAdminUsername ?? "")}";

            var win = new LearniqWindow(url);
            win.Show();
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
            {
                var confirm = MessageBox.Show(
                    "Are you sure you want to log out?",
                    "Confirm Logout",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                await HandleLogoutAsync();
            }

            #endregion

            #region Client Control Events

            private async void LockAllButton_Click(object sender, RoutedEventArgs e)
            {
                var result = MessageBox.Show("Are you sure you want to lock all connected PCs?\n\nThis will prevent students from using their computers.", 
                    "Confirm Lock All", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
                if (result != MessageBoxResult.Yes)
                    return;

                await _tcpServerService.SendCommandToAllAsync("lock");
                UpdateStatus("Lock command sent to all clients");
            
                // Log admin action (skip if database unavailable)
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Lock All Clients", "Lock command sent to all connected clients");
                }
            
                // Refresh system logs after action (skip if database unavailable)
                if (_databaseService != null)
                {
                    await RefreshSystemLogs();
                }
            }

            private async void UnlockAllButton_Click(object sender, RoutedEventArgs e)
            {
                var result = MessageBox.Show("Are you sure you want to unlock all connected PCs?", 
                    "Confirm Unlock All", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
                if (result != MessageBoxResult.Yes)
                    return;

                await _tcpServerService.SendCommandToAllAsync("unlock");
                UpdateStatus("Unlock command sent to all clients");
            
                // Log admin action (skip if database unavailable)
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Unlock All Clients", "Unlock command sent to all connected clients");
                }
            
                // Refresh system logs after action (skip if database unavailable)
                if (_databaseService != null)
                {
                    await RefreshSystemLogs();
                }
            }

            private async void ShutdownAllButton_Click(object sender, RoutedEventArgs e)
            {
                var result = MessageBox.Show("Are you sure you want to shutdown all connected PCs?", 
                    "Confirm Shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
                if (result == MessageBoxResult.Yes)
                {
                    await _tcpServerService.SendCommandToAllAsync("shutdown");
                    UpdateStatus("Shutdown command sent to all clients");
                
                    // Log admin action (skip if database unavailable)
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Shutdown All Clients", "Shutdown command sent to all connected clients");
                    }
                
                    // Refresh system logs after action (skip if database unavailable)
                    if (_databaseService != null)
                    {
                        await RefreshSystemLogs();
                    }
                }
            }

            private async void RestartAllButton_Click(object sender, RoutedEventArgs e)
            {
                var result = MessageBox.Show("Are you sure you want to restart all connected PCs?", 
                    "Confirm Restart", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
                if (result == MessageBoxResult.Yes)
                {
                    await _tcpServerService.SendCommandToAllAsync("restart");
                    UpdateStatus("Restart command sent to all clients");
                
                    // Log admin action (skip if database unavailable)
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Restart All Clients", "Restart command sent to all connected clients");
                    }
                
                    // Refresh system logs after action (skip if database unavailable)
                    if (_databaseService != null)
                    {
                        await RefreshSystemLogs();
                    }
                }
            }

            private async void SleepAllButton_Click(object sender, RoutedEventArgs e)
            {
                var result = MessageBox.Show("Are you sure you want to put all connected PCs to sleep?", 
                    "Confirm Sleep All", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
                if (result != MessageBoxResult.Yes)
                    return;

                await _tcpServerService.SendCommandToAllAsync("sleep");
                UpdateStatus("Sleep command sent to all clients");
            
                // Log admin action (skip if database unavailable)
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Sleep All Clients", "Sleep command sent to all connected clients");
                }
            
                // Refresh system logs after action (skip if database unavailable)
                if (_databaseService != null)
                {
                    await RefreshSystemLogs();
                }
            }

            private async void RefreshClientsButton_Click(object sender, RoutedEventArgs e)
            {
                await RefreshConnectedClients();
                UpdateStatus("Client list refreshed");
            }

            #endregion

            #region Individual Client Control Events

            private async void LockClientButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is Button button && button.Tag is string clientName)
                {
                    await _tcpServerService.SendCommandAsync(clientName, "lock");
                    UpdateStatus($"Lock command sent to {clientName}");
                
                    // Log admin action (skip if database unavailable)
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Lock Client", $"Lock command sent to {clientName}", clientName);
                    }
                
                    // Refresh system logs after action (skip if database unavailable)
                    if (_databaseService != null)
                    {
                        await RefreshSystemLogs();
                    }
                }
            }

            private async void UnlockClientButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is Button button && button.Tag is string clientName)
                {
                    await _tcpServerService.SendCommandAsync(clientName, "unlock");
                    UpdateStatus($"Unlock command sent to {clientName}");
                
                    // Log admin action (skip if database unavailable)
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Unlock Client", $"Unlock command sent to {clientName}", clientName);
                    }
                
                    // Refresh system logs after action (skip if database unavailable)
                    if (_databaseService != null)
                    {
                        await RefreshSystemLogs();
                    }
                }
            }

            private async void ShutdownClientButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is Button button && button.Tag is string clientName)
                {
                    var result = MessageBox.Show($"Are you sure you want to shutdown {clientName}?", 
                        "Confirm Shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                
                    if (result == MessageBoxResult.Yes)
                    {
                        await _tcpServerService.SendCommandAsync(clientName, "shutdown");
                        UpdateStatus($"Shutdown command sent to {clientName}");
                    
                        // Log admin action (skip if database unavailable)
                        if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                        {
                            await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Shutdown Client", $"Shutdown command sent to {clientName}", clientName);
                        }
                    
                        // Refresh system logs after action (skip if database unavailable)
                        if (_databaseService != null)
                        {
                            await RefreshSystemLogs();
                        }
                    }
                }
            }

            private async void RestartClientButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is Button button && button.Tag is string clientName)
                {
                    var result = MessageBox.Show($"Are you sure you want to restart {clientName}?", 
                        "Confirm Restart", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                
                    if (result == MessageBoxResult.Yes)
                    {
                        await _tcpServerService.SendCommandAsync(clientName, "restart");
                        UpdateStatus($"Restart command sent to {clientName}");
                    
                        // Log admin action (skip if database unavailable)
                        if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                        {
                            await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Restart Client", $"Restart command sent to {clientName}", clientName);
                        }
                    
                        // Refresh system logs after action (skip if database unavailable)
                        if (_databaseService != null)
                        {
                            await RefreshSystemLogs();
                        }
                    }
                }
            }

            private async void SleepClientButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is Button button && button.Tag is string clientName)
                {
                    await _tcpServerService.SendCommandAsync(clientName, "sleep");
                    UpdateStatus($"Sleep command sent to {clientName}");
                
                    // Log admin action (skip if database unavailable)
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername) && _databaseService != null)
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Sleep Client", $"Sleep command sent to {clientName}", clientName);
                    }
                
                    // Refresh system logs after action (skip if database unavailable)
                    if (_databaseService != null)
                    {
                        await RefreshSystemLogs();
                    }
                }
            }

            private void ViewLogsButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is Button button && button.Tag is string clientName)
                {
                    // Switch to System Logs tab and filter by client
                    MainTabControl.SelectedIndex = 1; // System Logs tab
                    UpdateStatus($"Viewing logs for {clientName}");
                }
            }

            private void ViewScreenButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is not Button button || button.Tag is not string clientName)
                {
                    return;
                }

                if (!_tcpServerService.IsClientConnected(clientName))
                {
                    MessageBox.Show($"{clientName} is not currently connected.", "Client Offline", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (_remoteWindows.TryGetValue(clientName, out var existingWindow))
                {
                    existingWindow.Activate();
                    return;
                }

                var remoteWindow = new RemoteControlWindow(_tcpServerService, clientName)
                {
                    Owner = this
                };

                remoteWindow.Closed += (_, _) =>
                {
                    _remoteWindows.Remove(clientName);
                };

                _remoteWindows[clientName] = remoteWindow;
                remoteWindow.Show();
                UpdateStatus($"Opening remote view for {clientName}");
            }

            private void CloseAllRemoteWindows()
            {
                if (_remoteWindows.Count == 0)
                {
                    return;
                }

                foreach (var window in _remoteWindows.Values.ToList())
                {
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                        // Ignore errors during window cleanup
                    }
                }

                _remoteWindows.Clear();
            }

            #endregion

            #region Log Management Events

            private async void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
            {
                // Clear date filters
                SystemLogsStartDatePicker.SelectedDate = null;
                SystemLogsEndDatePicker.SelectedDate = null;
                _systemLogsStartDate = null;
                _systemLogsEndDate = null;
            
                await RefreshSystemLogs();
                UpdateStatus("System logs refreshed");
            }

            private async void ExportLogsButton_Click(object sender, RoutedEventArgs e)
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                    DefaultExt = "csv",
                    FileName = $"system_logs_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    await ExportSystemLogs(saveDialog.FileName);
                    UpdateStatus($"System logs exported to {Path.GetFileName(saveDialog.FileName)}");
                }
            }

            private async void RefreshComputersButton_Click(object sender, RoutedEventArgs e)
            {
                await RefreshComputers();
                UpdateStatus("Computers list refreshed");
            }

            private async void EditComputerButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is not Button button || button.Tag is not Computer computer)
                {
                    return;
                }

                var dialog = new EditComputerDialog(computer, _databaseService, _tcpServerService)
                {
                    Owner = this
                };

                var result = dialog.ShowDialog();

                if (result == true && dialog.WasSaved)
                {
                    // Refresh computers list
                    await RefreshComputers();
                    UpdateStatus($"Computer '{computer.ClientName}' updated successfully");

                    // Log admin action
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                    {
                        await _databaseService.LogAdminActionAsync(
                            _currentAdminUsername,
                            "Edit Computer",
                            $"Updated computer configuration for '{computer.ClientName}'",
                            computer.ClientName);
                    }
                }
            }

            private async void MaintenanceModeButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is not Button button || button.Tag is not Computer computer)
                {
                    return;
                }

                // Check if computer is currently in maintenance status
                bool isCurrentlyInMaintenance = computer.Status == "Maintenance";

                if (isCurrentlyInMaintenance)
                {
                    // Disable maintenance mode
                    var confirm = MessageBox.Show(
                        $"Disable maintenance mode for '{computer.ClientName}'?\n\nThe computer will be unlocked and available for use.",
                        "Disable Maintenance Mode",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (confirm != MessageBoxResult.Yes)
                    {
                        return;
                    }

                    try
                    {
                        // Update database status to Online
                        await _databaseService.UpdateComputerStatusAsync(computer.Id, "Online");

                        // If client is connected, send unlock command
                        if (_tcpServerService.IsClientConnected(computer.ClientName))
                        {
                            await _tcpServerService.SendCommandAsync(computer.ClientName, "unlock");
                            UpdateStatus($"Maintenance mode disabled for {computer.ClientName} - Unlock command sent");
                        }
                        else
                        {
                            UpdateStatus($"Maintenance mode disabled for {computer.ClientName}");
                        }

                        // Log admin action
                        if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                        {
                            await _databaseService.LogAdminActionAsync(
                                _currentAdminUsername,
                                "Disable Maintenance Mode",
                                $"Maintenance mode disabled for '{computer.ClientName}'",
                                computer.ClientName);
                        }

                        // Refresh computers list
                        await RefreshComputers();

                        MessageBox.Show(
                            $"Maintenance mode disabled for '{computer.ClientName}'.",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Error disabling maintenance mode: {ex.Message}",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        UpdateStatus($"Error: {ex.Message}");
                    }
                }
                else
                {
                    // Enable maintenance mode
                    var maintenanceDialog = new MaintenanceModeDialog()
                    {
                        Owner = this
                    };

                    var result = maintenanceDialog.ShowDialog();

                    if (result == true && !string.IsNullOrWhiteSpace(maintenanceDialog.MaintenanceMessage))
                    {
                        try
                        {
                            // Update database status to Maintenance
                            await _databaseService.UpdateComputerStatusAsync(computer.Id, "Maintenance");

                            // If client is connected, send maintenance lock command
                            if (_tcpServerService.IsClientConnected(computer.ClientName))
                            {
                                var parameters = JsonSerializer.Serialize(new { message = maintenanceDialog.MaintenanceMessage });
                                await _tcpServerService.SendCommandAsync(computer.ClientName, "maintenance_lock", parameters);
                                UpdateStatus($"Maintenance mode enabled for {computer.ClientName} - Lock command sent");
                            }
                            else
                            {
                                UpdateStatus($"Maintenance mode enabled for {computer.ClientName} (will apply when client connects)");
                            }

                            // Log admin action
                            if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                            {
                                await _databaseService.LogAdminActionAsync(
                                    _currentAdminUsername,
                                    "Enable Maintenance Mode",
                                    $"Maintenance mode enabled for '{computer.ClientName}': {maintenanceDialog.MaintenanceMessage}",
                                    computer.ClientName);
                            }

                            // Refresh computers list
                            await RefreshComputers();

                            MessageBox.Show(
                                $"Maintenance mode enabled for '{computer.ClientName}'.\n\nMessage: {maintenanceDialog.MaintenanceMessage}",
                                "Success",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                $"Error enabling maintenance mode: {ex.Message}",
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            UpdateStatus($"Error: {ex.Message}");
                        }
                    }
                }
            }

            private async void RefreshAttendanceButton_Click(object sender, RoutedEventArgs e)
            {
                // Clear date filters
                StartDatePicker.SelectedDate = null;
                EndDatePicker.SelectedDate = null;
                _attendanceStartDate = null;
                _attendanceEndDate = null;
            
                await RefreshAttendanceLogs();
                UpdateStatus("Attendance logs refreshed");
            }

            private async void ExportAttendanceButton_Click(object sender, RoutedEventArgs e)
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                    DefaultExt = "csv",
                    FileName = $"attendance_logs_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    await ExportAttendanceLogs(saveDialog.FileName);
                    UpdateStatus($"Attendance logs exported to {Path.GetFileName(saveDialog.FileName)}");
                }
            }

            private async void RefreshClassListButton_Click(object sender, RoutedEventArgs e)
            {
                // Clear date filter
                ClassListDatePicker.SelectedDate = null;
                _classListDate = null;
            
                await RefreshClassList();
                UpdateStatus("Class list refreshed");
            }

            private async void ExportClassListButton_Click(object sender, RoutedEventArgs e)
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                    DefaultExt = "csv",
                    FileName = $"class_list_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    await ExportClassList(saveDialog.FileName);
                    UpdateStatus($"Class list exported to {Path.GetFileName(saveDialog.FileName)}");
                }
            }

            #endregion

            #region Event Handlers

            private void OnClientConnected(object? sender, ClientConnectedEventArgs e)
            {
                Dispatcher.Invoke(async () =>
                {
                    var clientInfo = new ClientInfo
                    {
                        Name = e.ClientName,
                        IpAddress = e.IpAddress,
                        LastResponse = DateTime.UtcNow,
                        IsConnected = true,
                        Status = "Online"
                    };
                
                    _connectedClients.Add(clientInfo);
                    UpdateConnectedClientsCount();
                    UpdateStatus($"Client {e.ClientName} connected from {e.IpAddress}");
                
                    // Ensure computer is registered in database
                    if (_databaseService != null)
                    {
                        var computerId = await _databaseService.EnsureComputerExistsAsync(e.ClientName, e.IpAddress);
                        if (computerId.HasValue)
                        {
                            UpdateStatus($"Client {e.ClientName} registered in database (ID: {computerId.Value})");
                        }
                    }
                
                    // Refresh computers tab to update online status and show new computers
                    _ = RefreshComputers();
                
                    // Refresh system logs when client connects
                    _ = RefreshSystemLogs();
                });
            }

            private void OnClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
            {
                Dispatcher.Invoke(async () =>
                {
                    var client = _connectedClients.FirstOrDefault(c => c.Name == e.ClientName);
                    if (client != null)
                    {
                        client.IsConnected = false;
                        client.Status = "Disconnected";
                    }
                    UpdateConnectedClientsCount();
                    UpdateStatus($"Client {e.ClientName} disconnected");
                
                    // Mark computer as offline in database
                    if (_databaseService != null)
                    {
                        var success = await _databaseService.MarkComputerOfflineAsync(e.ClientName);
                        if (success)
                        {
                            UpdateStatus($"Client {e.ClientName} marked as offline in database");
                        }
                    }
                
                    // Refresh computers tab to update offline status
                    _ = RefreshComputers();
                
                    // Refresh system logs when client disconnects
                    _ = RefreshSystemLogs();
                });
            }

            private void OnCommandReceived(object? sender, CommandReceivedEventArgs e)
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateStatus($"Response from {e.ClientName}: {e.Response}");
                });
            }

            private void OnVoiceCommandRecognized(object? sender, VoiceCommandEventArgs e)
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateStatus($"Voice command: {e.Command} {e.Target}");
                });
            }

            private void OnRecognitionError(object? sender, string error)
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateStatus($"Voice recognition error: {error}");
                });
            }


        private void OnVoiceRejected(object? sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                VoiceRejectionText.Text = message;
                VoiceRejectionBanner.Visibility = Visibility.Visible;
                UpdateStatus(message);

                // Auto-hide banner after 4 seconds
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(4)
                };
                timer.Tick += (s, e) =>
                {
                    VoiceRejectionBanner.Visibility = Visibility.Collapsed;
                    timer.Stop();
                };
                timer.Start();
            });
        }

        #endregion

        #region Helper Methods

        private Task RefreshConnectedClients()
            {
                try
                {
                    var clients = _tcpServerService.GetConnectedClients();
                    _allClients = clients.Values.ToList();
                
                    // Calculate total pages
                    _clientsTotalPages = (int)Math.Ceiling((double)_allClients.Count / _clientsPageSize);
                    if (_clientsTotalPages == 0) _clientsTotalPages = 1;
                
                    // Ensure current page is within bounds
                    if (_clientsCurrentPage > _clientsTotalPages)
                    {
                        _clientsCurrentPage = _clientsTotalPages;
                    }
                
                    // Get clients for current page
                    var pagedClients = _allClients
                        .Skip((_clientsCurrentPage - 1) * _clientsPageSize)
                        .Take(_clientsPageSize)
                        .ToList();
                
                    _connectedClients.Clear();
                    foreach (var client in pagedClients)
                    {
                        _connectedClients.Add(client);
                    }
                
                    UpdateClientsPagination();
                    UpdateConnectedClientsCount();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error refreshing clients: {ex.Message}");
                }
                return Task.CompletedTask;
            }

            private async Task RefreshSystemLogs()
            {
                try
                {
                    // Fetch all logs matching the filter
                    var logs = await _databaseService.GetSystemLogsAsync(_systemLogsStartDate, _systemLogsEndDate);
                    _allSystemLogs = logs;
                
                    // Calculate total pages
                    _systemLogsTotalPages = (int)Math.Ceiling((double)logs.Count / _systemLogsPageSize);
                    if (_systemLogsTotalPages == 0) _systemLogsTotalPages = 1;
                
                    // Ensure current page is within bounds
                    if (_systemLogsCurrentPage > _systemLogsTotalPages)
                    {
                        _systemLogsCurrentPage = _systemLogsTotalPages;
                    }
                
                    // Get logs for current page
                    var pagedLogs = logs
                        .Skip((_systemLogsCurrentPage - 1) * _systemLogsPageSize)
                        .Take(_systemLogsPageSize)
                        .ToList();
                
                    _systemLogs.Clear();
                    foreach (var log in pagedLogs)
                    {
                        _systemLogs.Add(log);
                    }
                
                    UpdateSystemLogsPagination();
                    UpdateStatus($"Loaded {pagedLogs.Count} of {logs.Count} system log(s) - Page {_systemLogsCurrentPage} of {_systemLogsTotalPages}");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error refreshing system logs: {ex.Message}");
                }
            }

            private void UpdateSystemLogsPagination()
            {
                if (SystemLogsPaginationPanel == null) return;
            
                Dispatcher.Invoke(() =>
                {
                    SystemLogsPageInfoText.Text = $"Page {_systemLogsCurrentPage} of {_systemLogsTotalPages} ({_allSystemLogs.Count} total records)";
                    SystemLogsPrevButton.IsEnabled = _systemLogsCurrentPage > 1;
                    SystemLogsNextButton.IsEnabled = _systemLogsCurrentPage < _systemLogsTotalPages;
                    SystemLogsFirstButton.IsEnabled = _systemLogsCurrentPage > 1;
                    SystemLogsLastButton.IsEnabled = _systemLogsCurrentPage < _systemLogsTotalPages;
                });
            }

            private void SystemLogsFirstButton_Click(object sender, RoutedEventArgs e)
            {
                _systemLogsCurrentPage = 1;
                _ = RefreshSystemLogsPage();
            }

            private void SystemLogsPrevButton_Click(object sender, RoutedEventArgs e)
            {
                if (_systemLogsCurrentPage > 1)
                {
                    _systemLogsCurrentPage--;
                    _ = RefreshSystemLogsPage();
                }
            }

            private void SystemLogsNextButton_Click(object sender, RoutedEventArgs e)
            {
                if (_systemLogsCurrentPage < _systemLogsTotalPages)
                {
                    _systemLogsCurrentPage++;
                    _ = RefreshSystemLogsPage();
                }
            }

            private void SystemLogsLastButton_Click(object sender, RoutedEventArgs e)
            {
                _systemLogsCurrentPage = _systemLogsTotalPages;
                _ = RefreshSystemLogsPage();
            }

            private Task RefreshSystemLogsPage()
            {
                try
                {
                    var pagedLogs = _allSystemLogs
                        .Skip((_systemLogsCurrentPage - 1) * _systemLogsPageSize)
                        .Take(_systemLogsPageSize)
                        .ToList();
                
                    _systemLogs.Clear();
                    foreach (var log in pagedLogs)
                    {
                        _systemLogs.Add(log);
                    }
                
                    UpdateSystemLogsPagination();
                    UpdateStatus($"Page {_systemLogsCurrentPage} of {_systemLogsTotalPages}");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error loading page: {ex.Message}");
                }
                return Task.CompletedTask;
            }

            // Clients Pagination Methods
            private void UpdateClientsPagination()
            {
                if (ClientsPaginationPanel == null) return;
            
                Dispatcher.Invoke(() =>
                {
                    ClientsPageInfoText.Text = $"Page {_clientsCurrentPage} of {_clientsTotalPages} ({_allClients.Count} clients)";
                    ClientsPrevButton.IsEnabled = _clientsCurrentPage > 1;
                    ClientsNextButton.IsEnabled = _clientsCurrentPage < _clientsTotalPages;
                    ClientsFirstButton.IsEnabled = _clientsCurrentPage > 1;
                    ClientsLastButton.IsEnabled = _clientsCurrentPage < _clientsTotalPages;
                });
            }

            private void ClientsFirstButton_Click(object sender, RoutedEventArgs e)
            {
                _clientsCurrentPage = 1;
                _ = RefreshClientsPage();
            }

            private void ClientsPrevButton_Click(object sender, RoutedEventArgs e)
            {
                if (_clientsCurrentPage > 1)
                {
                    _clientsCurrentPage--;
                    _ = RefreshClientsPage();
                }
            }

            private void ClientsNextButton_Click(object sender, RoutedEventArgs e)
            {
                if (_clientsCurrentPage < _clientsTotalPages)
                {
                    _clientsCurrentPage++;
                    _ = RefreshClientsPage();
                }
            }

            private void ClientsLastButton_Click(object sender, RoutedEventArgs e)
            {
                _clientsCurrentPage = _clientsTotalPages;
                _ = RefreshClientsPage();
            }

            private Task RefreshClientsPage()
            {
                try
                {
                    var pagedClients = _allClients
                        .Skip((_clientsCurrentPage - 1) * _clientsPageSize)
                        .Take(_clientsPageSize)
                        .ToList();
                
                    _connectedClients.Clear();
                    foreach (var client in pagedClients)
                    {
                        _connectedClients.Add(client);
                    }
                
                    UpdateClientsPagination();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error loading clients page: {ex.Message}");
                }
                return Task.CompletedTask;
            }

            // Computers Pagination Methods
            private void UpdateComputersPagination()
            {
                if (ComputersPaginationPanel == null) return;
            
                Dispatcher.Invoke(() =>
                {
                    ComputersPageInfoText.Text = $"Page {_computersCurrentPage} of {_computersTotalPages} ({_allComputers.Count} computers)";
                    ComputersPrevButton.IsEnabled = _computersCurrentPage > 1;
                    ComputersNextButton.IsEnabled = _computersCurrentPage < _computersTotalPages;
                    ComputersFirstButton.IsEnabled = _computersCurrentPage > 1;
                    ComputersLastButton.IsEnabled = _computersCurrentPage < _computersTotalPages;
                });
            }

            private void ComputersFirstButton_Click(object sender, RoutedEventArgs e)
            {
                _computersCurrentPage = 1;
                _ = RefreshComputersPage();
            }

            private void ComputersPrevButton_Click(object sender, RoutedEventArgs e)
            {
                if (_computersCurrentPage > 1)
                {
                    _computersCurrentPage--;
                    _ = RefreshComputersPage();
                }
            }

            private void ComputersNextButton_Click(object sender, RoutedEventArgs e)
            {
                if (_computersCurrentPage < _computersTotalPages)
                {
                    _computersCurrentPage++;
                    _ = RefreshComputersPage();
                }
            }

            private void ComputersLastButton_Click(object sender, RoutedEventArgs e)
            {
                _computersCurrentPage = _computersTotalPages;
                _ = RefreshComputersPage();
            }

            private Task RefreshComputersPage()
            {
                try
                {
                    var pagedComputers = _allComputers
                        .Skip((_computersCurrentPage - 1) * _computersPageSize)
                        .Take(_computersPageSize)
                        .ToList();
                
                    _computers.Clear();
                    foreach (var computer in pagedComputers)
                    {
                        _computers.Add(computer);
                    }
                
                    UpdateComputersPagination();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error loading computers page: {ex.Message}");
                }
                return Task.CompletedTask;
            }

            private async Task RefreshComputers()
            {
                try
                {
                    var computers = await _databaseService.GetComputersAsync();
                    _allComputers.Clear();
                
                    foreach (var computer in computers)
                    {
                        // Update online/offline status based on actual TCP connection
                        if (_isServerRunning && _tcpServerService.IsClientConnected(computer.ClientName))
                        {
                            computer.IsOnline = true;
                            computer.Status = "Online";
                        }
                        else
                        {
                            computer.IsOnline = false;
                            // Keep database status (Maintenance, etc.) if server is not running
                            // or set to Offline if not in maintenance mode
                            if (computer.Status != "Maintenance")
                            {
                                computer.Status = "Offline";
                            }
                        }
                    
                        _allComputers.Add(computer);
                    }
                
                    // Calculate total pages
                    _computersTotalPages = (int)Math.Ceiling((double)_allComputers.Count / _computersPageSize);
                    if (_computersTotalPages == 0) _computersTotalPages = 1;
                
                    // Ensure current page is within bounds
                    if (_computersCurrentPage > _computersTotalPages)
                    {
                        _computersCurrentPage = _computersTotalPages;
                    }
                
                    // Get computers for current page
                    var pagedComputers = _allComputers
                        .Skip((_computersCurrentPage - 1) * _computersPageSize)
                        .Take(_computersPageSize)
                        .ToList();
                
                    _computers.Clear();
                    foreach (var computer in pagedComputers)
                    {
                        _computers.Add(computer);
                    }
                
                    UpdateComputersPagination();
                
                    if (_allComputers.Count == 0)
                    {
                        UpdateStatus("No computers found in database. Please run database_setup.sql to add sample data.");
                    }
                    else
                    {
                        var onlineCount = _allComputers.Count(c => c.IsOnline);
                        UpdateStatus($"Loaded {pagedComputers.Count} of {_allComputers.Count} computer(s) - {onlineCount} online - Page {_computersCurrentPage} of {_computersTotalPages}");
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error refreshing computers: {ex.Message}");
                    MessageBox.Show(
                        $"Error loading computers from database:\n{ex.Message}\n\nPlease ensure:\n1. Database is running\n2. database_setup.sql has been executed\n3. Connection string is correct in appsettings.json",
                        "Database Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            private async Task RefreshAttendanceLogs()
            {
                try
                {
                    if (_showAbsentStudents)
                    {
                        // Get combined list of present and absent students
                        var allLogs = await _databaseService.GetAttendanceWithAbsentStudentsAsync(_attendanceStartDate, _attendanceEndDate);
                        _attendanceLogs.Clear();
                    
                        foreach (var log in allLogs)
                        {
                            _attendanceLogs.Add(log);
                        }
                    
                        var presentCount = allLogs.Count(l => l.Status != "Absent");
                        var absentCount = allLogs.Count(l => l.Status == "Absent");
                        UpdateStatus($"Loaded {presentCount} present, {absentCount} absent - Total: {allLogs.Count} record(s)");
                    }
                    else
                    {
                        // Get only present students (logged in)
                        var logs = await _databaseService.GetAttendanceLogsAsync(_attendanceStartDate, _attendanceEndDate);
                        _attendanceLogs.Clear();
                    
                        foreach (var log in logs)
                        {
                            _attendanceLogs.Add(log);
                        }
                    
                        UpdateStatus($"Loaded {logs.Count} attendance record(s)");
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error refreshing attendance logs: {ex.Message}");
                }
            }

            private async Task RefreshActivityLogs()
            {
                try
                {
                    var logs = await _databaseService.GetActivityLogsAsync(_activityStartDate, _activityEndDate);
                    _activityLogs.Clear();
                
                    foreach (var log in logs)
                    {
                        _activityLogs.Add(log);
                    }
                
                    UpdateStatus($"Loaded {logs.Count} activity record(s)");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error refreshing activity logs: {ex.Message}");
                }
            }

            private async Task RefreshLoginRequests()
            {
                try
                {
                    var logs = await _databaseService.GetPendingLoginRequestsAsync();
                    _loginRequests.Clear();
                
                    foreach (var log in logs)
                    {
                        _loginRequests.Add(log);
                    }
                
                    UpdatePendingRequestsCount();
                    UpdateStatus($"Loaded {logs.Count} login request(s)");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error refreshing login requests: {ex.Message}");
                }
            }

            private async Task RefreshClassList()
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_currentAdminUsername))
                    {
                        UpdateStatus("No instructor ID available for class list");
                        return;
                    }

                    var list = await _databaseService.GetInstructorClassListAsync(_currentAdminUsername, _classListDate);
                    _classList.Clear();
                    foreach (var item in list)
                    {
                        _classList.Add(item);
                    }

                    UpdateStatus($"Loaded {list.Count} class record(s)");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error refreshing class list: {ex.Message}");
                }
            }

            private void UpdatePendingRequestsCount()
            {
                var pendingCount = _loginRequests.Count(r => r.Status == "Pending");
                PendingRequestsCountText.Text = $"Pending: {pendingCount}";
            }

            private void FilterTodayButton_Click(object sender, RoutedEventArgs e)
            {
                var today = DateTime.Today;
                StartDatePicker.SelectedDate = today;
                EndDatePicker.SelectedDate = today;
                _attendanceStartDate = today;
                _attendanceEndDate = today;
                _ = RefreshAttendanceLogs();
            }

            private void FilterAllButton_Click(object sender, RoutedEventArgs e)
            {
                StartDatePicker.SelectedDate = null;
                EndDatePicker.SelectedDate = null;
                _attendanceStartDate = null;
                _attendanceEndDate = null;
                _ = RefreshAttendanceLogs();
            }

            private void ShowAbsentButton_Click(object sender, RoutedEventArgs e)
            {
                _showAbsentStudents = !_showAbsentStudents;
            
                if (_showAbsentStudents)
                {
                    ShowAbsentButton.Content = "✅ Hide Absent Students";
                    ShowAbsentButton.Style = (Style)FindResource("SuccessButton");
                }
                else
                {
                    ShowAbsentButton.Content = "❌ Show Absent Students";
                    ShowAbsentButton.Style = (Style)FindResource("DangerButton");
                }
            
                _ = RefreshAttendanceLogs();
            }

            private void ApplyDateFilterButton_Click(object sender, RoutedEventArgs e)
            {
                _attendanceStartDate = StartDatePicker.SelectedDate;
                _attendanceEndDate = EndDatePicker.SelectedDate;
                _ = RefreshAttendanceLogs();
            }

            private void DatePicker_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
            {
                // Auto-apply filter when dates are selected
                if (StartDatePicker.SelectedDate != null || EndDatePicker.SelectedDate != null)
                {
                    _attendanceStartDate = StartDatePicker.SelectedDate;
                    _attendanceEndDate = EndDatePicker.SelectedDate;
                    _ = RefreshAttendanceLogs();
                }
            }

            private AttendanceLog? GetSelectedAttendanceLog()
            {
                return AttendanceLogsDataGrid.SelectedItem as AttendanceLog;
            }

            private void AttendanceLogsDataGrid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
            {
                // Allow default sorting behavior
                e.Handled = false;
            }

            /// <summary>
            /// Refresh activity logs button click handler
            /// </summary>
            private async void RefreshActivityLogsButton_Click(object sender, RoutedEventArgs e)
            {
                // Clear date filters
                ActivityStartDatePicker.SelectedDate = null;
                ActivityEndDatePicker.SelectedDate = null;
                _activityStartDate = null;
                _activityEndDate = null;
            
                await RefreshActivityLogs();
                UpdateStatus("Activity logs refreshed");
            }

            private async void ExportActivityLogsButton_Click(object sender, RoutedEventArgs e)
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                    DefaultExt = "csv",
                    FileName = $"activity_logs_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    await ExportActivityLogs(saveDialog.FileName);
                    UpdateStatus($"Activity logs exported to {Path.GetFileName(saveDialog.FileName)}");
                }
            }

            private void ActivityLogsDataGrid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
            {
                // Allow default sorting behavior
                e.Handled = false;
            }

            private void FilterActivityTodayButton_Click(object sender, RoutedEventArgs e)
            {
                var today = DateTime.Today;
                ActivityStartDatePicker.SelectedDate = today;
                ActivityEndDatePicker.SelectedDate = today;
                _activityStartDate = today;
                _activityEndDate = today;
                _ = RefreshActivityLogs();
            }

            private void FilterActivityAllButton_Click(object sender, RoutedEventArgs e)
            {
                ActivityStartDatePicker.SelectedDate = null;
                ActivityEndDatePicker.SelectedDate = null;
                _activityStartDate = null;
                _activityEndDate = null;
                _ = RefreshActivityLogs();
            }

            private void ApplyActivityDateFilterButton_Click(object sender, RoutedEventArgs e)
            {
                _activityStartDate = ActivityStartDatePicker.SelectedDate;
                _activityEndDate = ActivityEndDatePicker.SelectedDate;
                _ = RefreshActivityLogs();
            }

            private void ActivityDatePicker_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
            {
                if (ActivityStartDatePicker.SelectedDate != null || ActivityEndDatePicker.SelectedDate != null)
                {
                    _activityStartDate = ActivityStartDatePicker.SelectedDate;
                    _activityEndDate = ActivityEndDatePicker.SelectedDate;
                    _ = RefreshActivityLogs();
                }
            }

            private async Task ExportActivityLogs(string filePath)
            {
                try
                {
                    var logs = await _databaseService.GetActivityLogsAsync(_activityStartDate, _activityEndDate, 1000);
                
                    if (Path.GetExtension(filePath).ToLower() == ".csv")
                    {
                        await ExportToCsv(logs.Select(l => new
                        {
                            l.Timestamp,
                            l.StudNo,
                            l.StudentName,
                            l.PcName,
                            l.Action,
                            l.Description
                        }), filePath);
                    }
                    else
                    {
                        await ExportToText(logs, filePath);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            /// <summary>
            /// Filter system logs to today
            /// </summary>
            private void FilterSystemLogsTodayButton_Click(object sender, RoutedEventArgs e)
            {
                var today = DateTime.Today;
                SystemLogsStartDatePicker.SelectedDate = today;
                SystemLogsEndDatePicker.SelectedDate = today;
                _systemLogsStartDate = today;
                _systemLogsEndDate = today;
                _systemLogsCurrentPage = 1; // Reset to first page
                _ = RefreshSystemLogs();
            }

            /// <summary>
            /// Show all system log records
            /// </summary>
            private void FilterSystemLogsAllButton_Click(object sender, RoutedEventArgs e)
            {
                SystemLogsStartDatePicker.SelectedDate = null;
                SystemLogsEndDatePicker.SelectedDate = null;
                _systemLogsStartDate = null;
                _systemLogsEndDate = null;
                _systemLogsCurrentPage = 1; // Reset to first page
                _ = RefreshSystemLogs();
            }

            /// <summary>
            /// Apply system logs date filter
            /// </summary>
            private void ApplySystemLogsDateFilterButton_Click(object sender, RoutedEventArgs e)
            {
                _systemLogsStartDate = SystemLogsStartDatePicker.SelectedDate;
                _systemLogsEndDate = SystemLogsEndDatePicker.SelectedDate;
                _systemLogsCurrentPage = 1; // Reset to first page
                _ = RefreshSystemLogs();
            }

            /// <summary>
            /// Auto-apply filter when system logs dates are selected
            /// </summary>
            private void SystemLogsDatePicker_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
            {
                if (SystemLogsStartDatePicker.SelectedDate != null || SystemLogsEndDatePicker.SelectedDate != null)
                {
                    _systemLogsStartDate = SystemLogsStartDatePicker.SelectedDate;
                    _systemLogsEndDate = SystemLogsEndDatePicker.SelectedDate;
                    _systemLogsCurrentPage = 1; // Reset to first page
                    _ = RefreshSystemLogs();
                }
            }

            private async Task ExportSystemLogs(string filePath)
            {
                try
                {
                    var logs = await _databaseService.GetSystemLogsAsync(_systemLogsStartDate, _systemLogsEndDate, 1000); // Export last 1000 logs with date filter
                
                    if (Path.GetExtension(filePath).ToLower() == ".csv")
                    {
                        await ExportToCsv(logs.Select(l => new
                        {
                            l.Timestamp,
                            l.Action,
                            l.ClientName,
                            l.Status,
                            l.Details
                        }), filePath);
                    }
                    else
                    {
                        await ExportToText(logs, filePath);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            private async Task ExportAttendanceLogs(string filePath, List<AttendanceLog>? logs = null)
            {
                try
                {
                    // Use provided logs or fetch all if not provided
                    if (logs == null)
                    {
                        logs = await _databaseService.GetAttendanceLogsAsync();
                    }
                
                    if (Path.GetExtension(filePath).ToLower() == ".csv")
                    {
                        await ExportToCsv(logs.Select(l => new
                        {
                            l.StudNo,
                            l.StudentName,
                            l.PcName,
                            l.TimeIn,
                            l.TimeOut,
                            l.Duration,
                            l.IsActive
                        }), filePath);
                    }
                    else
                    {
                        await ExportToText(logs, filePath);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            private async Task ExportClassList(string filePath)
            {
                try
                {
                    var data = _classList.ToList();
                    if (data.Count == 0)
                    {
                        MessageBox.Show("No class list data to export.", "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (Path.GetExtension(filePath).ToLower() == ".csv")
                    {
                        await ExportToCsv(data.Select(l => new
                        {
                            l.StudNo,
                            l.LastName,
                            l.FirstName,
                            l.SectionName,
                            l.LoginTime,
                            l.LogoutTime,
                            l.Status,
                            l.ClientName
                        }), filePath);
                    }
                    else
                    {
                        await ExportToText(data, filePath);
                    }

                    MessageBox.Show($"Successfully exported {data.Count} record(s) to:\n{filePath}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            private void ClassListTodayButton_Click(object sender, RoutedEventArgs e)
            {
                var today = DateTime.Today;
                ClassListDatePicker.SelectedDate = today;
                _classListDate = today;
                _ = RefreshClassList();
            }
 
             private void ClassListAllButton_Click(object sender, RoutedEventArgs e)
             {
                 ClassListDatePicker.SelectedDate = null;
                 _classListDate = null;
                 _ = RefreshClassList();
             }
 
             private void ClassListApplyButton_Click(object sender, RoutedEventArgs e)
             {
                 _classListDate = ClassListDatePicker.SelectedDate;
                 _ = RefreshClassList();
             }
 
             private void ClassListDatePicker_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
             {
                 if (ClassListDatePicker.SelectedDate != null)
                 {
                     _classListDate = ClassListDatePicker.SelectedDate;
                     _ = RefreshClassList();
                 }
             }
 
             private async Task ExportToCsv<T>(IEnumerable<T> data, string filePath)
             {
                 var csv = new StringBuilder();
                 var properties = typeof(T).GetProperties();
             
                 // Header
                 csv.AppendLine(string.Join(",", properties.Select(p => p.Name)));
             
                 // Data
                 foreach (var item in data)
                 {
                     var values = properties.Select(p => 
                     {
                         var value = p.GetValue(item);
                         return value?.ToString()?.Replace(",", ";") ?? "";
                     });
                     csv.AppendLine(string.Join(",", values));
                 }
             
                 await File.WriteAllTextAsync(filePath, csv.ToString());
             }

             private async Task ExportToText<T>(IEnumerable<T> data, string filePath)
             {
                 var text = new StringBuilder();
             
                 foreach (var item in data)
                 {
                     text.AppendLine(item?.ToString() ?? "");
                 }
             
                 await File.WriteAllTextAsync(filePath, text.ToString());
             }

            /// <summary>
            /// Refresh the login requests button click handler
            /// </summary>
            private async void RefreshLoginRequestsButton_Click(object sender, RoutedEventArgs e)
            {
                await RefreshLoginRequests();
                UpdateStatus("Login requests refreshed");
            }

            /// <summary>
            /// Approve a login request
            /// </summary>
            private async void ApproveLoginRequestButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is not Button button || button.Tag is not int requestId)
                {
                    return;
                }

                var request = _loginRequests.FirstOrDefault(r => r.Id == requestId);
                if (request == null)
                {
                    MessageBox.Show("Login request not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var result = MessageBox.Show(
                    $"Approve {request.RequestType?.ToLower() ?? "login"} request for student '{request.StudNo}' on PC '{request.PcName}'?",
                    "Confirm Approval",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                try
                {
                    var success = await _databaseService.ApproveLoginRequestAsync(requestId, _currentAdminUsername ?? "admin");

                    if (success)
                    {
                        // If it's a logout request and client is connected, send logout command
                        if (request.RequestType?.Equals("Logout", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            if (_tcpServerService.IsClientConnected(request.PcName))
                            {
                                // Send logout command to client
                                await _tcpServerService.SendCommandAsync(request.PcName, "force_logout");
                                UpdateStatus($"Logout command sent to {request.PcName}");
                            }
                        }

                        await RefreshLoginRequests();
                        UpdateStatus($"{request.RequestType} request from {request.PcName} approved");

                        MessageBox.Show(
                            $"{request.RequestType} request approved for '{request.StudNo}' on '{request.PcName}'.",
                            "Request Approved",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(
                            "Failed to approve request. It may have already been processed.",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error approving request: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    UpdateStatus($"Error approving request: {ex.Message}");
                }
            }

            /// <summary>
            /// Decline a login request
            /// </summary>
            private async void DeclineLoginRequestButton_Click(object sender, RoutedEventArgs e)
            {
                if (sender is not Button button || button.Tag is not int requestId)
                {
                    return;
                }

                var request = _loginRequests.FirstOrDefault(r => r.Id == requestId);
                if (request == null)
                {
                    MessageBox.Show("Login request not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var result = MessageBox.Show(
                    $"Decline login request for student '{request.StudNo}' on PC '{request.PcName}'?",
                    "Confirm Decline",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                try
                {
                    var success = await _databaseService.DeclineLoginRequestAsync(requestId, _currentAdminUsername ?? "admin");

                    if (success)
                    {
                        await RefreshLoginRequests();
                        UpdateStatus($"Login request from {request.PcName} declined");

                        MessageBox.Show(
                            $"Login request declined for '{request.StudNo}' on '{request.PcName}'.",
                            "Request Declined",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(
                            "Failed to decline login request. It may have already been processed.",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error declining login request: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    UpdateStatus($"Error declining login request: {ex.Message}");
                }
            }

            private void UpdateStatus(string message)
            {
                StatusText.Text = $"{DateTime.Now:HH:mm:ss} - {message}";
            }

            private void UpdateConnectedClientsCount()
            {
                var connectedCount = _connectedClients.Count(c => c.IsConnected);
                ConnectedClientsText.Text = $"Connected Clients: {connectedCount}";
            }

            #endregion

            #region Session Management

            private async Task HandleLogoutAsync()
            {
                // Log admin action
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Logout", "Admin logged out");
                }
            
                await CleanupSessionStateAsync();

                var app = Application.Current;
                if (app == null)
                {
                    return;
                }

                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Hide();

                var loginWindow = new LoginWindow(_databaseService, requireAuthenticationToClose: false);
                bool? dialogResult = loginWindow.ShowDialog();

                if (loginWindow.IsAuthenticated && dialogResult == true)
                {
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    Show();
                    WindowState = WindowState.Maximized;
                    Activate();
                    MainTabControl.SelectedIndex = 0;

                    // Reset session timeout after successful re-login
                    ResetInactivityTimer();
                    _inactivityTimer.Start();

                    await RefreshSystemLogs();
                    await RefreshComputers();
                    await RefreshAttendanceLogs();
                    await RefreshConnectedClients();

                    UpdateStatus("Logged in successfully");
                }
                else
                {
                    app.Shutdown();
                }
            }

            private async Task CleanupSessionStateAsync()
            {
                // Stop all timers
                _inactivityTimer?.Stop();
                _clientsRefreshTimer?.Stop();
                _uptimeTimer?.Stop();
                _scheduleEndTimer?.Stop();
            
                try
                {
                    if (_isServerRunning)
                    {
                        await _tcpServerService.StopServerAsync();
                        _isServerRunning = false;
                        ServerToggleButton.Content = "▶️ Start Server";
                        ServerToggleButton.Style = (Style)FindResource("SuccessButton");
                        ServerStatusIndicator.Fill = Brushes.Red;
                        ServerStatusTooltip.Content = "Server: Stopped";
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error stopping server: {ex.Message}");
                }

                try
                {
                    if (_isVoiceEnabled)
                    {
                        _voiceRecognitionService.StopListening();
                        _isVoiceEnabled = false;
                        VoiceToggleButton.Content = "🎤 Voice Commands: OFF";
                        VoiceToggleButton.Style = (Style)FindResource("ModernButton");
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error disabling voice recognition: {ex.Message}");
                }

                CloseAllRemoteWindows();
                _connectedClients.Clear();
                _systemLogs.Clear();
                _attendanceLogs.Clear();
                _loginRequests.Clear();
                _classList.Clear();
                UpdateConnectedClientsCount();
                UpdateStatus("Session cleared");
            }

        #endregion


        #region Screen Monitor
        private void RefreshScreensButton_Click(object sender, RoutedEventArgs e)
        {
            StartScreenMonitor();
        }

        private void StopAllStreamsButton_Click(object sender, RoutedEventArgs e)
        {
            StopScreenMonitor();
        }

        private void StartScreenMonitor()
        {
            if (!_isServerRunning)
            {
                MessageBox.Show("Start the server first.", "Server Not Running",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ScreensWrapPanel.Children.Clear();
            _screenThumbnails.Clear();

            var clients = _tcpServerService.GetConnectedClients();

            if (clients.Count == 0)
            {
                ScreenMonitorStatusText.Text = "No connected clients";
                return;
            }

            foreach (var client in clients.Values)
            {
                AddScreenThumbnail(client.Name);
                _ = _tcpServerService.StartScreenStreamAsync(client.Name, 1000); // 1 FPS for thumbnails
            }

            _tcpServerService.ScreenDataReceived += ScreenMonitor_ScreenDataReceived;
            ScreenMonitorStatusText.Text = $"{clients.Count} screen(s) active";
        }

        private void StopScreenMonitor()
        {
            _tcpServerService.ScreenDataReceived -= ScreenMonitor_ScreenDataReceived;

            var clients = _tcpServerService.GetConnectedClients();
            foreach (var client in clients.Values)
            {
                _ = _tcpServerService.StopScreenStreamAsync(client.Name);
            }

            ScreenMonitorStatusText.Text = "0 screens active";
        }

        private void AddScreenThumbnail(string clientName)
        {
            // Container card
            var card = new Border
            {
                Width = 320,
                Height = 230,
                Margin = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(5, 13, 96)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Black,
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Screen image
            var image = new System.Windows.Controls.Image
            {
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRow(image, 0);
            _screenThumbnails[clientName] = image;

            // Label
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(5, 13, 96)),
                Padding = new Thickness(8, 4, 8, 4)
            };
            var labelText = new TextBlock
            {
                Text = clientName,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            label.Child = labelText;
            Grid.SetRow(label, 1);

            grid.Children.Add(image);
            grid.Children.Add(label);
            card.Child = grid;

            // Click to open full remote window
            card.MouseLeftButtonUp += (s, e) =>
            {
                // Stop thumbnail stream first
                _ = _tcpServerService.StopScreenStreamAsync(clientName);
                _tcpServerService.ScreenDataReceived -= ScreenMonitor_ScreenDataReceived;

                // Open full remote window
                if (_remoteWindows.TryGetValue(clientName, out var existing))
                {
                    existing.Activate();
                }
                else
                {
                    var remoteWindow = new RemoteControlWindow(_tcpServerService, clientName)
                    {
                        Owner = this
                    };
                    remoteWindow.Closed += (_, _) =>
                    {
                        _remoteWindows.Remove(clientName);
                        // Resume thumbnail stream
                        _tcpServerService.ScreenDataReceived += ScreenMonitor_ScreenDataReceived;
                        _ = _tcpServerService.StartScreenStreamAsync(clientName, 1000);
                    };
                    _remoteWindows[clientName] = remoteWindow;
                    remoteWindow.Show();
                }
            };

            ScreensWrapPanel.Children.Add(card);
        }

        private void ScreenMonitor_ScreenDataReceived(object? sender, ScreenDataReceivedEventArgs e)
        {
            if (!_screenThumbnails.ContainsKey(e.ClientName)) return;

            try
            {
                var bitmap = new BitmapImage();
                using (var ms = new System.IO.MemoryStream(e.ImageBytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                Dispatcher.Invoke(() =>
                {
                    if (_screenThumbnails.TryGetValue(e.ClientName, out var img))
                        img.Source = bitmap;
                });
            }
            catch { }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
            {
                if (_isServerRunning)
                {
                    _ = Task.Run(async () => await _tcpServerService.StopServerAsync());
                }
            
                if (_isVoiceEnabled)
                {
                    _voiceRecognitionService.StopListening();
                }
            
                _voiceRecognitionService.Dispose();
                _host.Dispose();
            
                base.OnClosed(e);
            }

            #region Uptime Timer

            private void UptimeTimer_Tick(object? sender, EventArgs e)
            {
                if (_serverStartTime.HasValue)
                {
                    var uptime = DateTime.Now - _serverStartTime.Value;
                    ServerUptimeText.Text = $"Uptime: {uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
                }
            }

            #endregion

            #region Schedule End Timer

            private async void ScheduleEndTimer_Tick(object? sender, EventArgs e)
            {
                if (!_isServerRunning || !_scheduleEndTime.HasValue)
                {
                    return;
                }

                var currentTime = DateTime.Now.TimeOfDay;
            
                // Check if current time has reached or passed the schedule end time
                if (currentTime >= _scheduleEndTime.Value)
                {
                    _scheduleEndTimer.Stop();
                
                    // Show notification
                    MessageBox.Show(
                        $"Schedule end time reached ({_scheduleEndTime.Value:hh\\:mm})!\n\nThe server will now stop automatically.",
                        "Schedule Ended",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                
                    // Stop the server
                    await StopServerAsync(isAutoStop: true);
                }
            }

            private async Task StopServerAsync(bool isAutoStop = false)
            {
                try
                {
                    // Send logout command to all connected clients BEFORE stopping the server
                    var connectedClients = _tcpServerService.GetConnectedClients();
                    if (connectedClients.Count > 0)
                    {
                        UpdateStatus($"Notifying {connectedClients.Count} connected client(s) - Server is shutting down...");
                    
                        // Send server_shutdown command to all clients
                        await _tcpServerService.SendCommandToAllAsync("server_shutdown");
                    
                        // Give clients a moment to process the logout command
                        await Task.Delay(1000);
                    
                        UpdateStatus("All clients notified of server shutdown");
                    }

                    // Record server stop time
                    await _databaseService.RecordServerStopAsync();

                    await _tcpServerService.StopServerAsync();
                    _isServerRunning = false;
                    _serverStartTime = null;
                    _scheduleEndTime = null;
                    _clientsRefreshTimer.Stop();
                    _uptimeTimer.Stop();
                    _scheduleEndTimer.Stop();
                    ServerUptimeText.Text = "Uptime: --:--:--";
                    ServerToggleButton.Content = "▶️ Start Server";
                    ServerToggleButton.Style = (Style)FindResource("SuccessButton");
                    ServerStatusIndicator.Fill = Brushes.Red;
                    ServerStatusTooltip.Content = "Server: Stopped";
                
                    var statusMessage = isAutoStop 
                        ? "Server stopped automatically (schedule ended)" 
                        : "Server stopped";
                    UpdateStatus(statusMessage);

                    // Log admin action
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                    {
                        var logMessage = isAutoStop 
                            ? "Server stopped automatically (schedule end time reached)" 
                            : "Server stopped";
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Stop Server", logMessage);
                    }

                    CloseAllRemoteWindows();

                    // Refresh system logs when server stops
                    await RefreshSystemLogs();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error stopping server: {ex.Message}");
                }
            }

            #endregion

            #region Session Timeout

            private void LoadSecuritySettings()
            {
                try
                {
                    var config = _host.Services.GetRequiredService<IConfiguration>();
                    _sessionTimeoutMinutes = config.GetValue<int>("Security:SessionTimeoutMinutes", 15);
                    _warningBeforeMinutes = config.GetValue<int>("Security:ShowWarningBeforeMinutes", 1);
                
                    UpdateStatus($"Session timeout: {_sessionTimeoutMinutes} minutes (warning at {_sessionTimeoutMinutes - _warningBeforeMinutes} min)");
                }
                catch
                {
                    // Use defaults if config fails
                    _sessionTimeoutMinutes = 15;
                    _warningBeforeMinutes = 1;
                }
            }

            private void SaveTimeoutSettings()
            {
                try
                {
                    var configPath = "appsettings.json";
                    if (!System.IO.File.Exists(configPath))
                    {
                        UpdateStatus($"Warning: appsettings.json not found at {configPath}");
                        return;
                    }

                    var json = System.IO.File.ReadAllText(configPath);
                    var jsonDoc = System.Text.Json.JsonDocument.Parse(json);
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

                    using var stream = System.IO.File.Create(configPath);
                    using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });

                    // Parse and update
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    writer.WriteStartObject();

                    foreach (var property in root.EnumerateObject())
                    {
                        if (property.Name == "Security")
                        {
                            writer.WriteStartObject("Security");
                            writer.WriteNumber("SessionTimeoutMinutes", _sessionTimeoutMinutes);
                            writer.WriteNumber("ShowWarningBeforeMinutes", _warningBeforeMinutes);
                            writer.WriteEndObject();
                        }
                        else
                        {
                            property.Value.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                    writer.Flush();

                    UpdateStatus($"Session settings saved: {_sessionTimeoutMinutes} min timeout, {_warningBeforeMinutes} min warning");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error saving settings: {ex.Message}");
                    MessageBox.Show(
                        $"Could not save settings to appsettings.json:\n{ex.Message}",
                        "Save Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            private void ResetInactivityTimer()
            {
                _lastActivityTime = DateTime.Now;
                _timeoutWarningShown = false;
            }

            private void InactivityTimer_Tick(object? sender, EventArgs e)
            {
                var inactiveMinutes = (DateTime.Now - _lastActivityTime).TotalMinutes;
            
                // Show warning before lock
                if (inactiveMinutes >= (_sessionTimeoutMinutes - _warningBeforeMinutes) && !_timeoutWarningShown)
                {
                    _timeoutWarningShown = true;
                    var secondsRemaining = (int)((_sessionTimeoutMinutes - inactiveMinutes) * 60);
                
                    var result = MessageBox.Show(
                        $"Your screen will be locked in {secondsRemaining} seconds due to inactivity.\n\nClick OK to continue working, or Cancel to lock now.",
                        "⚠️ Session Lock Warning",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning);
                
                    if (result == MessageBoxResult.OK)
                    {
                        // User clicked OK - reset timer
                        ResetInactivityTimer();
                        UpdateStatus("Session lock timer reset - activity detected");
                    }
                    else
                    {
                        // User clicked Cancel or closed dialog - lock immediately
                        _inactivityTimer.Stop();
                        ShowLockScreen();
                    }
                }
                // Auto-lock if timeout reached
                else if (inactiveMinutes >= _sessionTimeoutMinutes)
                {
                    _inactivityTimer.Stop();
                    ShowLockScreen();
                }
            }

            private void ShowLockScreen()
            {
                try
                {
                    // Ensure we have the necessary credentials
                    if (string.IsNullOrWhiteSpace(_currentAdminUsername) || string.IsNullOrWhiteSpace(_adminPassword))
                    {
                        MessageBox.Show(
                            "Session credentials not available. You will be logged out.",
                            "Security Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);

                        _ = HandleLogoutAsync();
                        return;
                    }

                    // Show lock screen
                    var lockScreen = new LockScreenDialog(_currentAdminUsername, _adminPassword);
                    lockScreen.ShowDialog();

                    if (lockScreen.WasUnlocked)
                    {
                        // Success - resume session
                        ResetInactivityTimer();
                        _inactivityTimer?.Start();

                        // Log unlock event
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _databaseService.LogSystemActionAsync(
                                    "Screen Unlocked",
                                    _currentAdminUsername ?? "Unknown",
                                    "Success",
                                    $"Admin {_currentAdminUsername} successfully unlocked screen after inactivity"
                                );
                            }
                            catch
                            {
                                // Ignore logging errors
                            }
                        });

                        UpdateStatus("Screen unlocked - session resumed");
                    }
                    else if (lockScreen.WasLogoutRequested)
                    {
                        // User chose to logout from lock screen
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _databaseService.LogSystemActionAsync(
                                    "Logout from Lock Screen",
                                    _currentAdminUsername ?? "Unknown",
                                    "Success",
                                    $"Admin {_currentAdminUsername} logged out from lock screen"
                                );
                            }
                            catch
                            {
                                // Ignore logging errors
                            }
                        });

                        _ = HandleLogoutAsync();
                    }
                    else
                    {
                        // Lock screen was closed unexpectedly - restart timer
                        _inactivityTimer?.Start();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error showing lock screen: {ex.Message}\n\nYou will be logged out for security.",
                        "Lock Screen Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    // Failsafe: logout on error
                    _ = HandleLogoutAsync();
                }
            }

            private async Task HandleSessionTimeoutAsync(string reason)
            {
                // Log the timeout event
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    await _databaseService.LogAdminActionAsync(
                        _currentAdminUsername, 
                        "Session Timeout", 
                        $"Auto-logout: {reason}");
                }
            
                MessageBox.Show(
                    $"Your session has ended due to inactivity.\n\nReason: {reason}\n\nYou will be logged out for security.",
                    "Session Timeout",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            
                // Perform logout
                await HandleLogoutAsync();
            }

            #endregion

            #region Search Functionality

            private void ClassListSearchBox_TextChanged(object sender, TextChangedEventArgs e)
            {
                // Guard against initialization - DataGrid may not exist yet
                if (ClassListDataGrid == null || ClassListDataGrid.ItemsSource == null)
                {
                    return;
                }

                if (ClassListSearchBox.Text == "Search..." || ClassListSearchBox.Foreground == Brushes.Gray)
                {
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ClassListDataGrid.ItemsSource);
                    view.Filter = null;
                    return;
                }

                var searchText = ClassListSearchBox.Text.ToLower();
                var view2 = System.Windows.Data.CollectionViewSource.GetDefaultView(ClassListDataGrid.ItemsSource);
            
                if (string.IsNullOrWhiteSpace(searchText))
                {
                    view2.Filter = null;
                }
                else
                {
                    view2.Filter = item =>
                    {
                        if (item is InstructorClassListItem classItem)
                        {
                            return classItem.StudNo?.ToLower().Contains(searchText) == true ||
                                   classItem.FirstName?.ToLower().Contains(searchText) == true ||
                                   classItem.LastName?.ToLower().Contains(searchText) == true ||
                                   classItem.SectionName?.ToLower().Contains(searchText) == true;
                        }
                        return false;
                    };
                }
            }

            private void ComputersSearchBox_TextChanged(object sender, TextChangedEventArgs e)
            {
                // Guard against initialization - DataGrid may not exist yet
                if (ComputersDataGrid == null || ComputersDataGrid.ItemsSource == null)
                {
                    return;
                }

                if (ComputersSearchBox.Text == "Search..." || ComputersSearchBox.Foreground == Brushes.Gray)
                {
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ComputersDataGrid.ItemsSource);
                    view.Filter = null;
                    return;
                }

                var searchText = ComputersSearchBox.Text.ToLower();
                var view2 = System.Windows.Data.CollectionViewSource.GetDefaultView(ComputersDataGrid.ItemsSource);
            
                if (string.IsNullOrWhiteSpace(searchText))
                {
                    view2.Filter = null;
                }
                else
                {
                    view2.Filter = item =>
                    {
                        if (item is Computer computer)
                        {
                            return computer.ClientName?.ToLower().Contains(searchText) == true ||
                                   computer.IpAddress?.ToLower().Contains(searchText) == true;
                        }
                        return false;
                    };
                }
            }

            private void AttendanceSearchBox_TextChanged(object sender, TextChangedEventArgs e)
            {
                // Guard against initialization - DataGrid may not exist yet
                if (AttendanceLogsDataGrid == null || AttendanceLogsDataGrid.ItemsSource == null)
                {
                    return;
                }

                if (AttendanceSearchBox.Text == "Search..." || AttendanceSearchBox.Foreground == Brushes.Gray)
                {
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(AttendanceLogsDataGrid.ItemsSource);
                    view.Filter = null;
                    return;
                }

                var searchText = AttendanceSearchBox.Text.ToLower();
                var view2 = System.Windows.Data.CollectionViewSource.GetDefaultView(AttendanceLogsDataGrid.ItemsSource);
            
                if (string.IsNullOrWhiteSpace(searchText))
                {
                    view2.Filter = null;
                }
                else
                {
                    view2.Filter = item =>
                    {
                        if (item is AttendanceLog log)
                        {
                            return log.StudNo?.ToLower().Contains(searchText) == true ||
                                   log.StudentName?.ToLower().Contains(searchText) == true ||
                                   log.PcName?.ToLower().Contains(searchText) == true;
                        }
                        return false;
                    };
                }
            }

            #endregion

            #region Search Box Placeholder Handlers

            private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
            {
                if (sender is TextBox textBox && textBox.Text == "Search..." && textBox.Foreground == Brushes.Gray)
                {
                    textBox.Text = "";
                    textBox.Foreground = Brushes.Black;
                }
            }

            private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
            {
                if (sender is TextBox textBox && string.IsNullOrWhiteSpace(textBox.Text))
                {
                    textBox.Text = "Search...";
                    textBox.Foreground = Brushes.Gray;
                }
            }

            /// <summary>
            /// Clear search boxes when switching between tabs
            /// </summary>
            private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
            {
                // Clear all search boxes when navigating away from tabs
                ClearSearchBox(ClassListSearchBox);
                ClearSearchBox(ComputersSearchBox);
                ClearSearchBox(AttendanceSearchBox);

            if (MainTabControl.SelectedItem == ScreenMonitorTab)
            {
                StartScreenMonitor();
            }
            else
            {
                StopScreenMonitor();
            }


        }

            /// <summary>
            /// Helper method to reset a search box to its placeholder state
            /// </summary>
            private void ClearSearchBox(TextBox searchBox)
            {
                if (searchBox != null && searchBox.Text != "Search...")
                {
                    searchBox.Text = "Search...";
                    searchBox.Foreground = Brushes.Gray;
                }
            }

            #endregion
        }
    }
