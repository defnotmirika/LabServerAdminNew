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
        private ObservableCollection<AttendanceLog> _attendanceLogs = new();
        
        private bool _isServerRunning = false;
        private bool _isVoiceEnabled = false;
        private double? _currentUsageLimitHours = null;
        private readonly Dictionary<string, RemoteControlWindow> _remoteWindows = new();

        private const string UsageLimitHoursKey = "client_usage_hours";
        private const string UsageLimitExpiryKey = "client_usage_expiry_utc";
        private const string UsageLimitSessionStartKey = "client_usage_session_start_utc";

        private DateTime? _usageLimitSessionStartUtc = null;
        private DateTime? _currentUsageLimitExpiryUtc = null;

        public MainWindow()
        {
            InitializeComponent();
            
            // Setup dependency injection
            _host = CreateHostBuilder().Build();
            _databaseService = _host.Services.GetRequiredService<DatabaseService>();
            _tcpServerService = _host.Services.GetRequiredService<TcpServerService>();
            _voiceRecognitionService = _host.Services.GetRequiredService<VoiceRecognitionService>();
            
            // Setup data binding
            ClientsDataGrid.ItemsSource = _connectedClients;
            SystemLogsDataGrid.ItemsSource = _systemLogs;
            AttendanceLogsDataGrid.ItemsSource = _attendanceLogs;
            
            // Setup event handlers
            SetupEventHandlers();
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
                await LoadUsageLimitAsync();
                // Load attendance logs when server starts
                await RefreshAttendanceLogs();
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
                    ServerToggleButton.Content = "⏹️ Stop Server";
                    ServerToggleButton.Style = (Style)FindResource("DangerButton");
                    ServerStatusText.Text = "Server: Running on Port 9000";
                    UpdateStatus("Server started successfully");
                    
                    await HandleUsageLimitOnServerStartAsync();
                    
                    // Refresh attendance logs when server starts
                    await RefreshAttendanceLogs();
                }
                else
                {
                    if (IsUsageLimitSessionActive)
                    {
                        await _tcpServerService.SendCommandToAllAsync("set_usage_limit", null);
                    }

                    await ClearUsageLimitSessionAsync();

                    await _tcpServerService.StopServerAsync();
                    _isServerRunning = false;
                    ServerToggleButton.Content = "▶️ Start Server";
                    ServerToggleButton.Style = (Style)FindResource("SuccessButton");
                    ServerStatusText.Text = "Server: Stopped";
                    UpdateStatus("Server stopped");
                    CloseAllRemoteWindows();
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
        }

        private async void UnlockAllButton_Click(object sender, RoutedEventArgs e)
        {
            await _tcpServerService.SendCommandToAllAsync("unlock");
            UpdateStatus("Unlock command sent to all clients");
        }

        private async void ShutdownAllButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to shutdown all connected PCs?", 
                "Confirm Shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                await _tcpServerService.SendCommandToAllAsync("shutdown");
                UpdateStatus("Shutdown command sent to all clients");
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
            }
        }

        private async void SleepAllButton_Click(object sender, RoutedEventArgs e)
        {
            await _tcpServerService.SendCommandToAllAsync("sleep");
            UpdateStatus("Sleep command sent to all clients");
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
            }
        }

        private async void UnlockClientButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string clientName)
            {
                await _tcpServerService.SendCommandAsync(clientName, "unlock");
                UpdateStatus($"Unlock command sent to {clientName}");
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
                }
            }
        }

        private async void SleepClientButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string clientName)
            {
                await _tcpServerService.SendCommandAsync(clientName, "sleep");
                UpdateStatus($"Sleep command sent to {clientName}");
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

        private async void RefreshAttendanceButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAttendanceLogs();
            UpdateStatus("Attendance logs refreshed");
        }

        private async void ExportAttendanceButton_Click(object sender, RoutedEventArgs e)
        {
            // Show date selection window
            var exportWindow = new ExportAttendanceWindow
            {
                Owner = this
            };

            if (exportWindow.ShowDialog() == true && exportWindow.IsExportConfirmed)
            {
                try
                {
                    // Get attendance logs for selected date range
                    var logs = await _databaseService.GetAttendanceLogsAsync(
                        exportWindow.SelectedStartDate, 
                        exportWindow.SelectedEndDate);

                    if (logs.Count == 0)
                    {
                        MessageBox.Show(
                            "No attendance records found for the selected date range.",
                            "No Data",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    // Export to file
                    await ExportAttendanceLogs(exportWindow.SelectedFilePath!, logs);
                    UpdateStatus($"Attendance logs exported to {Path.GetFileName(exportWindow.SelectedFilePath)} ({logs.Count} records)");
                    
                    MessageBox.Show(
                        $"Successfully exported {logs.Count} attendance record(s) to:\n{exportWindow.SelectedFilePath}",
                        "Export Successful",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error exporting attendance logs: {ex.Message}",
                        "Export Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
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

                if (IsUsageLimitSessionActive)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(500);
                            await SendUsageLimitToClientAsync(e.ClientName);
                        }
                        catch
                        {
                            // Ignored; logging is handled within TcpServerService
                        }
                    });
                }
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
                var logs = await _databaseService.GetSystemLogsAsync();
                _systemLogs.Clear();
                
                foreach (var log in logs)
                {
                    _systemLogs.Add(log);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error refreshing system logs: {ex.Message}");
            }
        }

        private DateTime? _attendanceStartDate = null;
        private DateTime? _attendanceEndDate = null;

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

        private void AttendanceLogsDataGrid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
        {
            // Allow default sorting behavior
            e.Handled = false;
        }

        private async Task ExportSystemLogs(string filePath)
        {
            try
            {
                var logs = await _databaseService.GetSystemLogsAsync(1000); // Export last 1000 logs
                
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
                        l.StudentName,
                        l.PcName,
                        l.TimeIn,
                        l.TimeOut,
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

        private void UpdateStatus(string message)
        {
            StatusText.Text = $"{DateTime.Now:HH:mm:ss} - {message}";
        }

        private void UpdateConnectedClientsCount()
        {
            var connectedCount = _connectedClients.Count(c => c.IsConnected);
            ConnectedClientsText.Text = $"Connected Clients: {connectedCount}";
        }

        private async Task LoadUsageLimitAsync()
        {
            try
            {
                var storedHours = await _databaseService.GetSettingAsync(UsageLimitHoursKey);
                var storedSessionStart = await _databaseService.GetSettingAsync(UsageLimitSessionStartKey);
                var storedExpiry = await _databaseService.GetSettingAsync(UsageLimitExpiryKey);

                if (!string.IsNullOrWhiteSpace(storedHours) &&
                    double.TryParse(storedHours, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) &&
                    hours > 0)
                {
                    _currentUsageLimitHours = hours;
                }
                else
                {
                    _currentUsageLimitHours = null;
                }

                _usageLimitSessionStartUtc = TryParseUtcDateTime(storedSessionStart);
                _currentUsageLimitExpiryUtc = TryParseUtcDateTime(storedExpiry);

                if (_currentUsageLimitExpiryUtc.HasValue && _currentUsageLimitExpiryUtc.Value <= DateTime.UtcNow)
                {
                    _currentUsageLimitExpiryUtc = null;
                    _usageLimitSessionStartUtc = null;
                }

                    Dispatcher.Invoke(() =>
                    {
                    UsageLimitHoursTextBox.Text = _currentUsageLimitHours?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
                        UpdateUsageLimitStatus();
                    });
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading settings: {ex.Message}");
            }
        }

        private void UpdateUsageLimitStatus()
        {
            if (_currentUsageLimitHours.HasValue && _currentUsageLimitHours.Value > 0)
            {
                if (IsUsageLimitSessionActive)
                {
                    var remaining = _currentUsageLimitExpiryUtc!.Value - DateTime.UtcNow;
                    if (remaining < TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }

                    UsageLimitStatusText.Text =
                        $"Usage limit active ({_currentUsageLimitHours.Value.ToString("0.##", CultureInfo.CurrentCulture)}h). Remaining: {remaining:hh\\:mm\\:ss}.";
                UsageLimitStatusText.FontStyle = FontStyles.Normal;
                UsageLimitStatusText.Foreground = Brushes.DarkGreen;
                }
                else
                {
                    UsageLimitStatusText.Text = "Usage limit configured. Session will begin when the server starts.";
                    UsageLimitStatusText.FontStyle = FontStyles.Italic;
                    UsageLimitStatusText.Foreground = Brushes.SteelBlue;
                }
            }
            else
            {
                UsageLimitStatusText.Text = "No usage limit set.";
                UsageLimitStatusText.FontStyle = FontStyles.Italic;
                UsageLimitStatusText.Foreground = Brushes.Gray;
            }
        }

        private bool IsUsageLimitSessionActive =>
            _currentUsageLimitHours.HasValue &&
            _currentUsageLimitHours.Value > 0 &&
            _usageLimitSessionStartUtc.HasValue &&
            _currentUsageLimitExpiryUtc.HasValue &&
            _currentUsageLimitExpiryUtc.Value > DateTime.UtcNow;

        private static DateTime? TryParseUtcDateTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return null;
        }

        private async Task HandleUsageLimitOnServerStartAsync()
        {
            if (!_currentUsageLimitHours.HasValue || _currentUsageLimitHours.Value <= 0)
            {
                await ClearUsageLimitSessionAsync();
                return;
            }

            await ActivateUsageLimitSessionAsync(forceRestart: true);
        }

        private async Task ActivateUsageLimitSessionAsync(bool forceRestart)
        {
            if (!_currentUsageLimitHours.HasValue || _currentUsageLimitHours.Value <= 0)
            {
                return;
            }

            if (IsUsageLimitSessionActive && !forceRestart)
            {
                return;
            }

            _usageLimitSessionStartUtc = DateTime.UtcNow;
            _currentUsageLimitExpiryUtc = _usageLimitSessionStartUtc.Value.AddHours(_currentUsageLimitHours.Value);

            await _databaseService.SetSettingAsync(UsageLimitSessionStartKey, _usageLimitSessionStartUtc.Value.ToString("o", CultureInfo.InvariantCulture));
            await _databaseService.SetSettingAsync(UsageLimitExpiryKey, _currentUsageLimitExpiryUtc.Value.ToString("o", CultureInfo.InvariantCulture));

            Dispatcher.Invoke(UpdateUsageLimitStatus);

            await BroadcastUsageLimitAsync();
        }

        private async Task BroadcastUsageLimitAsync()
        {
            if (!IsUsageLimitSessionActive)
            {
                return;
            }

            var payloadJson = JsonSerializer.Serialize(CreateUsageLimitPayload());
            await _tcpServerService.SendCommandToAllAsync("set_usage_limit", payloadJson);
        }

        private async Task SendUsageLimitToClientAsync(string clientName)
        {
            if (!IsUsageLimitSessionActive)
            {
                return;
            }

            var payloadJson = JsonSerializer.Serialize(CreateUsageLimitPayload());
            await _tcpServerService.SendCommandAsync(clientName, "set_usage_limit", payloadJson);
        }

        private UsageLimitPayload CreateUsageLimitPayload()
        {
            return new UsageLimitPayload
            {
                Hours = _currentUsageLimitHours ?? 0,
                SessionStartUtc = (_usageLimitSessionStartUtc ?? DateTime.UtcNow),
                ExpiresUtc = (_currentUsageLimitExpiryUtc ?? DateTime.UtcNow)
            };
        }

        private async Task ClearUsageLimitSessionAsync()
        {
            _usageLimitSessionStartUtc = null;
            _currentUsageLimitExpiryUtc = null;
            await _databaseService.DeleteSettingAsync(UsageLimitSessionStartKey);
            await _databaseService.DeleteSettingAsync(UsageLimitExpiryKey);
            Dispatcher.Invoke(UpdateUsageLimitStatus);
        }

        private class UsageLimitPayload
        {
            public double Hours { get; set; }
            public DateTime SessionStartUtc { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }

        private void UsageLimitHoursTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            // Allow decimal point and digits
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c) && c != '.' && c != ',')
                {
                    e.Handled = true;
                    return;
                }
            }

            // Prevent multiple decimal points
            var currentText = textBox.Text;
            var selectionStart = textBox.SelectionStart;
            var newText = currentText.Insert(selectionStart, e.Text);
            
            if ((newText.Count(c => c == '.') > 1 && newText.Count(c => c == ',') > 1) ||
                (newText.Count(c => c == '.') > 1) ||
                (newText.Count(c => c == ',') > 1))
            {
                e.Handled = true;
            }
        }

        private void UsageLimitHoursTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Allow navigation, editing, and control keys
            if (e.Key == Key.Back || e.Key == Key.Delete || 
                e.Key == Key.Tab || e.Key == Key.Enter ||
                e.Key == Key.Left || e.Key == Key.Right ||
                e.Key == Key.Up || e.Key == Key.Down || 
                e.Key == Key.Home || e.Key == Key.End ||
                e.Key == Key.Escape)
            {
                return;
            }

            // Allow Ctrl+A, Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+Z
            if (Keyboard.Modifiers == ModifierKeys.Control &&
                (e.Key == Key.A || e.Key == Key.C || e.Key == Key.V || 
                 e.Key == Key.X || e.Key == Key.Z))
            {
                return;
            }

            // Allow Shift for selection
            if (Keyboard.Modifiers == ModifierKeys.Shift &&
                (e.Key == Key.Left || e.Key == Key.Right || 
                 e.Key == Key.Up || e.Key == Key.Down ||
                 e.Key == Key.Home || e.Key == Key.End))
            {
                return;
            }

            // Allow digits and decimal separators - actual validation in PreviewTextInput
            if ((e.Key >= Key.D0 && e.Key <= Key.D9) ||
                (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) ||
                e.Key == Key.OemPeriod || e.Key == Key.OemComma || 
                e.Key == Key.Decimal)
            {
                return;
            }

            // Block other keys
            e.Handled = true;
        }

        private void UsageLimitHoursTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                var text = (string)e.DataObject.GetData(typeof(string));
                
                // Validate pasted text is numeric
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out _) &&
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private async void ApplyUsageLimitButton_Click(object sender, RoutedEventArgs e)
        {
            var input = UsageLimitHoursTextBox.Text.Trim();
            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours) &&
                !double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out hours))
            {
                MessageBox.Show("Please enter a valid number of hours.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (hours <= 0)
            {
                MessageBox.Show("Usage limit must be greater than zero. Use 'Clear Limit' to remove the restriction.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _currentUsageLimitHours = hours;
                await _databaseService.SetSettingAsync(UsageLimitHoursKey, hours.ToString(CultureInfo.InvariantCulture));

                if (_isServerRunning)
                {
                    await ActivateUsageLimitSessionAsync(forceRestart: true);
                UpdateStatus($"Usage limit of {hours:0.##} hours applied to all clients");
                MessageBox.Show($"Usage limit of {hours:0.##} hours applied to all connected clients.", "Usage Limit Applied", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    await ClearUsageLimitSessionAsync();
                    UpdateStatus($"Usage limit of {hours:0.##} hours saved. Session will start when the server runs.");
                    MessageBox.Show("Usage limit saved. It will activate automatically when the server starts.", "Usage Limit Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to apply usage limit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus($"Usage limit error: {ex.Message}");
            }
        }

        private async void ClearUsageLimitButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _currentUsageLimitHours = null;
                UsageLimitHoursTextBox.Text = string.Empty;
                await _databaseService.DeleteSettingAsync(UsageLimitHoursKey);
                await ClearUsageLimitSessionAsync();

                if (_isServerRunning)
                {
                    await _tcpServerService.SendCommandToAllAsync("set_usage_limit", null);
                }

                UpdateStatus("Usage limit cleared");
                MessageBox.Show("Usage limit cleared for all clients.", "Usage Limit Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear usage limit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus($"Usage limit clear error: {ex.Message}");
            }
        }

        #endregion

        #region Session Management

        private async Task HandleLogoutAsync()
        {
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

                await LoadUsageLimitAsync();
                await RefreshSystemLogs();
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
