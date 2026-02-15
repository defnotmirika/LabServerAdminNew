using System;
using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using LabServerClient.Services;

namespace LabServerClient
{
    public partial class ClientWindow : Window
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private bool _isConnected = false;
        private readonly DatabaseService? _databaseService;
        private bool _allowClose = false;
        private LockpcWindow? _kioskModeWindow;
        private readonly bool _isAdmin;
        private SessionWindow? _sessionWindow;

        public void SetSessionWindow(SessionWindow? sessionWindow)
        {
            _sessionWindow = sessionWindow;
        }

        public ClientWindow(DatabaseService? databaseService = null, bool isAdmin = false, string? username = null)
        {
            _databaseService = databaseService;
            _isAdmin = isAdmin;

            InitializeComponent();

            // Show server configuration only for admin
            ServerIpTextBox.Visibility = _isAdmin ? Visibility.Visible : Visibility.Collapsed;

            // Load settings
            LoadSettings();

            // Setup startup
            SetupStartup();

            // Auto-connect for client accounts
            if (!_isAdmin)
            {
                Loaded += ClientWindow_Loaded;
            }

            UpdateStatus("Configuration mode");
        }

        private async void ClientWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isAdmin && !_isConnected)
            {
                await ConnectToServer();
            }
        }

        // Connection methods now perform real connect/disconnect
        private async Task ConnectToServer()
        {
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(ServerIpTextBox.Text, 9000);
                _stream = _tcpClient.GetStream();

                _isConnected = true;
                StatusText.Text = "Connected";
                StatusText.Style = (Style)FindResource("StatusConnected");

                await SendRegistrationMessage();

                SaveSettings();
                UpdateStatus($"Connected to {ServerIpTextBox.Text}");
                LogMessage($"Connected to server {ServerIpTextBox.Text}");
            }
            catch (Exception ex)
            {
                _isConnected = false;
                StatusText.Text = "Disconnected";
                StatusText.Style = (Style)FindResource("StatusDisconnected");
                UpdateStatus($"Connection failed: {ex.Message}");
                LogMessage($"Connection error: {ex.Message}");
            }
        }

        public async Task DisconnectFromServer()
        {
            try
            {
                if (_stream != null)
                {
                    await _stream.FlushAsync();
                    _stream.Close();
                    _stream = null;
                }

                if (_tcpClient != null)
                {
                    _tcpClient.Close();
                    _tcpClient = null;
                }

                _isConnected = false;
                StatusText.Text = "Disconnected";
                StatusText.Style = (Style)FindResource("StatusDisconnected");

                ResetScreenShareState();

                // Close kiosk mode window if open
                if (_kioskModeWindow != null)
                {
                    try
                    {
                        Dispatcher.Invoke(() => _kioskModeWindow.Close());
                    }
                    catch { }
                    _kioskModeWindow = null;
                }

                UpdateStatus("Disconnected from server");
                LogMessage("Disconnected from server");
            }
            catch (Exception ex)
            {
                LogMessage($"Disconnect error: {ex.Message}");
            }
        }

        private async Task SendRegistrationMessage()
        {
            if (_stream == null)
            {
                return;
            }

            var registration = new
            {
                type = "register",
                clientName = PcNameTextBox.Text,
                ipAddress = string.Empty,
                timestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(registration);
            var data = Encoding.UTF8.GetBytes(json + "\n");

            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }

        private void LoadSettings()
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\LabServerClient", true);
                if (key != null)
                {
                    ServerIpTextBox.Text = key.GetValue("ServerIP", "192.168.1.100")?.ToString() ?? "192.168.1.100";
                    PcNameTextBox.Text = key.GetValue("PCName", Environment.MachineName)?.ToString() ?? Environment.MachineName;
                    key.Close();
                }
                else
                {
                    // Set defaults if registry key doesn't exist
                    ServerIpTextBox.Text = "192.168.1.100";
                    PcNameTextBox.Text = Environment.MachineName;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error loading settings: {ex.Message}");
                // Set defaults on error
                ServerIpTextBox.Text = "192.168.1.100";
                PcNameTextBox.Text = Environment.MachineName;
            }
        }

        private void SaveSettings()
        {
            try
            {
                var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\LabServerClient", true);
                if (key != null)
                {
                    key.SetValue("ServerIP", ServerIpTextBox.Text ?? "192.168.1.100");
                    key.SetValue("PCName", PcNameTextBox.Text ?? Environment.MachineName);
                    key.Close();
                    LogMessage("Configuration saved successfully");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error saving settings: {ex.Message}");
            }
        }

        private void SetupStartup()
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    var appPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    key.SetValue("LabServerClient", $"\"{appPath}\"");
                    key.Close();
                    LogMessage("Added to Windows startup");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error setting up startup: {ex.Message}");
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            UpdateStatus("Configuration saved");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                if (WindowState != WindowState.Minimized)
                {
                    WindowState = WindowState.Minimized;
                }
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Close kiosk mode window if open
            if (_kioskModeWindow != null)
            {
                try
                {
                    _kioskModeWindow.Close();
                }
                catch { }
                _kioskModeWindow = null;
            }

            _ = Task.Run(async () => await DisconnectFromServer());
            base.OnClosed(e);
        }

        private void LogMessage(string message)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[ClientWindow] {DateTime.Now:HH:mm:ss}: {message}");
            }
            catch
            {
                // Ignore logging failures
            }
        }

        private void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
            });
        }

        private void ServerIpTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        private void PcNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        public bool IsConnected()
        {
            return _isConnected;
        }

        public NetworkStream? GetNetworkStream()
        {
            return _stream;
        }

        public string GetClientName()
        {
            return PcNameTextBox.Text;
        }

        private void ResetScreenShareState()
        {
            if (_sessionWindow != null)
            {
                _ = _sessionWindow.StopScreenShare();
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var app = Application.Current as App;

            // Prevent app shutdown when closing this window during logout
            if (app != null)
            {
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            _allowClose = true;

            if (_sessionWindow != null)
            {
                try
                {
                    _sessionWindow.Close();
                }
                catch { }
                _sessionWindow = null;
            }

            await DisconnectFromServer();

            // Close this window
            try
            {
                Close();
            }
            catch { }

            // Show login again
            if (app != null)
            {
                app.ShowLoginWindow();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
        }
    }
}

