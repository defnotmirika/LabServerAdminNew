using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LabServerAdmin.Models;
using LabServerAdmin.Services;
using Microsoft.Win32;

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
        private bool _isVoiceEnabled = false;
        private readonly Dictionary<string, RemoteControlWindow> _remoteWindows = new();
        private readonly string? _currentAdminUsername;
        private readonly string? _currentAdminRole;

        private DateTime? _attendanceStartDate = null;
        private DateTime? _attendanceEndDate = null;

        private DateTime? _activityStartDate = null;
        private DateTime? _activityEndDate = null;

        private DateTime? _systemLogsStartDate = null;
        private DateTime? _systemLogsEndDate = null;

        private DateTime? _classListDate = null;

        public MainWindow(string? adminUsername = null, string? adminRole = null)
        {
            _currentAdminUsername = adminUsername;
            _currentAdminRole = adminRole;
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
                }
            };
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            
            // Set window to fullscreen on startup (but still resizable)
            WindowState = WindowState.Maximized;
            
            // Hide class list tab unless role is instructor
            var isInstructor = string.Equals(_currentAdminRole, "INSTRUCTOR", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(_currentAdminRole, "Instructor", StringComparison.OrdinalIgnoreCase);
            ClassListTab.Visibility = isInstructor ? Visibility.Visible : Visibility.Collapsed;

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
                // Load class list when application starts (only for instructors)
                if (ClassListTab.Visibility == Visibility.Visible)
                {
                    await RefreshClassList();
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
                    services.AddSingleton<VoiceRecognitionService>();
                });

        private void SetupEventHandlers()
        {
            _tcpServerService.ClientConnected += OnClientConnected;
            _tcpServerService.ClientDisconnected += OnClientDisconnected;
            _tcpServerService.CommandReceived += OnCommandReceived;
            
            _voiceRecognitionService.VoiceCommandRecognized += OnVoiceCommandRecognized;
            _voiceRecognitionService.RecognitionError += OnRecognitionError;
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
                    _clientsRefreshTimer.Start();
                    ServerToggleButton.Content = "⏹️ Stop Server";
                    ServerToggleButton.Style = (Style)FindResource("DangerButton");
                    ServerStatusText.Text = "Server: Running on Port 9000";
                    UpdateStatus("Server started successfully");
                    
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
                    // Record server stop time
                    await _databaseService.RecordServerStopAsync();

                    await _tcpServerService.StopServerAsync();
                    _isServerRunning = false;
                    _clientsRefreshTimer.Stop();
                    ServerToggleButton.Content = "▶️ Start Server";
                    ServerToggleButton.Style = (Style)FindResource("SuccessButton");
                    ServerStatusText.Text = "Server: Stopped";
                    UpdateStatus("Server stopped");
                    
                    // Log admin action
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Stop Server", "Server stopped");
                    }
                    
                    CloseAllRemoteWindows();
                    
                    // Refresh system logs when server stops
                    await RefreshSystemLogs();
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
            await _tcpServerService.SendCommandToAllAsync("lock");
            UpdateStatus("Lock command sent to all clients");
            
            // Log admin action
            if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
            {
                await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Lock All Clients", "Lock command sent to all connected clients");
            }
            
            // Refresh system logs after action
            await RefreshSystemLogs();
        }

        private async void UnlockAllButton_Click(object sender, RoutedEventArgs e)
        {
            await _tcpServerService.SendCommandToAllAsync("unlock");
            UpdateStatus("Unlock command sent to all clients");
            
            // Log admin action
            if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
            {
                await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Unlock All Clients", "Unlock command sent to all connected clients");
            }
            
            // Refresh system logs after action
            await RefreshSystemLogs();
        }

        private async void ShutdownAllButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to shutdown all connected PCs?", 
                "Confirm Shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                await _tcpServerService.SendCommandToAllAsync("shutdown");
                UpdateStatus("Shutdown command sent to all clients");
                
                // Log admin action
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Shutdown All Clients", "Shutdown command sent to all connected clients");
                }
                
                // Refresh system logs after action
                await RefreshSystemLogs();
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
                
                // Log admin action
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Restart All Clients", "Restart command sent to all connected clients");
                }
                
                // Refresh system logs after action
                await RefreshSystemLogs();
            }
        }

        private async void SleepAllButton_Click(object sender, RoutedEventArgs e)
        {
            await _tcpServerService.SendCommandToAllAsync("sleep");
            UpdateStatus("Sleep command sent to all clients");
            
            // Log admin action
            if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
            {
                await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Sleep All Clients", "Sleep command sent to all connected clients");
            }
            
            // Refresh system logs after action
            await RefreshSystemLogs();
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
                
                // Log admin action
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Lock Client", $"Lock command sent to {clientName}", clientName);
                }
                
                // Refresh system logs after action
                await RefreshSystemLogs();
            }
        }

        private async void UnlockClientButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string clientName)
            {
                await _tcpServerService.SendCommandAsync(clientName, "unlock");
                UpdateStatus($"Unlock command sent to {clientName}");
                
                // Log admin action
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Unlock Client", $"Unlock command sent to {clientName}", clientName);
                }
                
                // Refresh system logs after action
                await RefreshSystemLogs();
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
                    
                    // Log admin action
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Shutdown Client", $"Shutdown command sent to {clientName}", clientName);
                    }
                    
                    // Refresh system logs after action
                    await RefreshSystemLogs();
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
                    
                    // Log admin action
                    if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                    {
                        await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Restart Client", $"Restart command sent to {clientName}", clientName);
                    }
                    
                    // Refresh system logs after action
                    await RefreshSystemLogs();
                }
            }
        }

        private async void SleepClientButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string clientName)
            {
                await _tcpServerService.SendCommandAsync(clientName, "sleep");
                UpdateStatus($"Sleep command sent to {clientName}");
                
                // Log admin action
                if (!string.IsNullOrWhiteSpace(_currentAdminUsername))
                {
                    await _databaseService.LogAdminActionAsync(_currentAdminUsername, "Sleep Client", $"Sleep command sent to {clientName}", clientName);
                }
                
                // Refresh system logs after action
                await RefreshSystemLogs();
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

        private async void RefreshAttendanceButton_Click(object sender, RoutedEventArgs e)
        {
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
            Dispatcher.Invoke(() =>
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
                
                // Refresh system logs when client connects
                _ = RefreshSystemLogs();
            });
        }

        private void OnClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var client = _connectedClients.FirstOrDefault(c => c.Name == e.ClientName);
                if (client != null)
                {
                    client.IsConnected = false;
                    client.Status = "Disconnected";
                }
                UpdateConnectedClientsCount();
                UpdateStatus($"Client {e.ClientName} disconnected");
                
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

        #endregion

        #region Helper Methods

        private Task RefreshConnectedClients()
        {
            try
            {
                var clients = _tcpServerService.GetConnectedClients();
                _connectedClients.Clear();
                
                foreach (var client in clients.Values)
                {
                    _connectedClients.Add(client);
                }
                
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
                var logs = await _databaseService.GetSystemLogsAsync(_systemLogsStartDate, _systemLogsEndDate);
                _systemLogs.Clear();
                
                foreach (var log in logs)
                {
                    _systemLogs.Add(log);
                }
                
                UpdateStatus($"Loaded {logs.Count} system log(s)");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error refreshing system logs: {ex.Message}");
            }
        }

        private async Task RefreshComputers()
        {
            try
            {
                var computers = await _databaseService.GetComputersAsync();
                _computers.Clear();
                
                foreach (var computer in computers)
                {
                    _computers.Add(computer);
                }
                
                if (computers.Count == 0)
                {
                    UpdateStatus("No computers found in database. Please run database_setup.sql to add sample data.");
                }
                else
                {
                    UpdateStatus($"Loaded {computers.Count} computer(s)");
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
                var logs = await _databaseService.GetAttendanceLogsAsync(_attendanceStartDate, _attendanceEndDate);
                _attendanceLogs.Clear();
                
                foreach (var log in logs)
                {
                    _attendanceLogs.Add(log);
                }
                
                UpdateStatus($"Loaded {logs.Count} attendance record(s)");
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
                if (ClassListTab.Visibility != Visibility.Visible)
                {
                    return;
                }

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
            _ = RefreshSystemLogs();
        }

        /// <summary>
        /// Apply system logs date filter
        /// </summary>
        private void ApplySystemLogsDateFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _systemLogsStartDate = SystemLogsStartDatePicker.SelectedDate;
            _systemLogsEndDate = SystemLogsEndDatePicker.SelectedDate;
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
            if (ClassListTab.Visibility != Visibility.Visible)
            {
                return;
            }
            var today = DateTime.Today;
            ClassListDatePicker.SelectedDate = today;
            _classListDate = today;
            _ = RefreshClassList();
        }
 
         private void ClassListAllButton_Click(object sender, RoutedEventArgs e)
         {
             if (ClassListTab.Visibility != Visibility.Visible)
             {
                 return;
             }
             ClassListDatePicker.SelectedDate = null;
             _classListDate = null;
             _ = RefreshClassList();
         }
 
         private void ClassListApplyButton_Click(object sender, RoutedEventArgs e)
         {
             if (ClassListTab.Visibility != Visibility.Visible)
             {
                 return;
             }
             _classListDate = ClassListDatePicker.SelectedDate;
             _ = RefreshClassList();
         }
 
         private void ClassListDatePicker_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
         {
             if (ClassListTab.Visibility != Visibility.Visible)
             {
                 return;
             }

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
            try
            {
                if (_isServerRunning)
                {
                    await _tcpServerService.StopServerAsync();
                    _isServerRunning = false;
                    ServerToggleButton.Content = "▶️ Start Server";
                    ServerToggleButton.Style = (Style)FindResource("SuccessButton");
                    ServerStatusText.Text = "Server: Stopped";
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
    }
}
