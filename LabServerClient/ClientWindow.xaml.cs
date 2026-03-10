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

        // P/Invoke for system sleep
        [System.Runtime.InteropServices.DllImport("PowrProf.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, ExactSpelling = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

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

            // Auto-connect for ALL users (both admin and students)
            Loaded += ClientWindow_Loaded;

            UpdateStatus("Configuration mode");
        }

        private async void ClientWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Auto-connect if not already connected
            if (!_isConnected)
            {
                await ConnectToServer();
            }
        }

        // Connection methods now perform real connect/disconnect
        private async Task ConnectToServer()
        {
            try
            {
                // Clean up any existing connection first
                if (_tcpClient != null)
                {
                    try
                    {
                        _stream?.Close();
                        _tcpClient.Close();
                    }
                    catch { }
                    _stream = null;
                    _tcpClient = null;
                }

                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(ServerIpTextBox.Text, 9000);
                _stream = _tcpClient.GetStream();

                _isConnected = true;
                StatusText.Text = "Connected";
                StatusText.Style = (Style)FindResource("StatusConnected");

                await SendRegistrationMessage();

                // Start listening for server messages
                _ = Task.Run(ListenForServerMessagesAsync);

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

        /// <summary>
        /// Attempts to reconnect to the server
        /// </summary>
        public async Task<bool> ReconnectToServer()
        {
            LogMessage("Attempting to reconnect to server...");
            UpdateStatus("Reconnecting to server...");

            try
            {
                await ConnectToServer();
                return _isConnected;
            }
            catch (Exception ex)
            {
                LogMessage($"Reconnection failed: {ex.Message}");
                return false;
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
                    ServerIpTextBox.Text = key.GetValue("ServerIP", "192.168.1.11")?.ToString() ?? "192.168.1.11";
                    PcNameTextBox.Text = key.GetValue("PCName", Environment.MachineName)?.ToString() ?? Environment.MachineName;
                    key.Close();
                }
                else
                {
                    // Set defaults if registry key doesn't exist
                    ServerIpTextBox.Text = "192.168.1.11";
                    PcNameTextBox.Text = Environment.MachineName;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error loading settings: {ex.Message}");
                // Set defaults on error
                ServerIpTextBox.Text = "192.168.1.11";
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
                    key.SetValue("ServerIP", ServerIpTextBox.Text ?? "192.168.1.11");
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

        private async Task ListenForServerMessagesAsync()
        {
            if (_stream == null || _tcpClient == null) return;

            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            try
            {
                while (_isConnected && _tcpClient.Connected)
                {
                    var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        LogMessage("Server disconnected - connection closed by server");
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(message);

                    // Process complete messages (ending with newline)
                    var messages = messageBuilder.ToString().Split('\n');
                    for (int i = 0; i < messages.Length - 1; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(messages[i]))
                        {
                            await ProcessServerMessageAsync(messages[i]);
                        }
                    }

                    // Keep the last incomplete message in the buffer
                    messageBuilder.Clear();
                    if (messages.Length > 0)
                    {
                        messageBuilder.Append(messages[^1]);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error listening for messages: {ex.Message}");
            }
            finally
            {
                // Update UI to show disconnected status but DON'T close the app
                await Dispatcher.InvokeAsync(() =>
                {
                    _isConnected = false;
                    StatusText.Text = "Server Disconnected";
                    StatusText.Style = (Style)FindResource("StatusDisconnected");
                    UpdateStatus("Connection to server lost. You can continue working offline.");
                    LogMessage("Server connection lost - client remains open");
                });

                // Clean up network resources
                try
                {
                    _stream?.Close();
                    _stream = null;
                    _tcpClient?.Close();
                    _tcpClient = null;
                }
                catch (Exception ex)
                {
                    LogMessage($"Error cleaning up connection: {ex.Message}");
                }
            }
        }

        private async Task ProcessServerMessageAsync(string message)
        {
            try
            {
                LogMessage($"Received: {message}");

                var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                {
                    return;
                }

                var type = typeElement.GetString();

                if (type == "command")
                {
                    if (root.TryGetProperty("command", out var commandElement))
                    {
                        var command = commandElement.GetString();

                        // ✅ FIX: Extract parameters and pass to SessionWindow
                        string? parameters = null;
                        if (root.TryGetProperty("parameters", out var parametersElement))
                        {
                            parameters = parametersElement.GetString();
                        }

                        await Dispatcher.InvokeAsync(async () => await ExecuteCommandAsync(command, parameters));
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error processing message: {ex.Message}");
            }
        }

        // ✅ FIX: Added parameters argument
        private async Task ExecuteCommandAsync(string? command, string? parameters = null)
        {
            if (string.IsNullOrEmpty(command)) return;

            LogMessage($"Executing command: {command}" + (parameters != null ? $" with parameters: {parameters}" : ""));

            try
            {
                // If SessionWindow exists, forward command to it (the visible student window)
                if (_sessionWindow != null)
                {
                    LogMessage($"Forwarding command '{command}' to SessionWindow with parameters");
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            // ✅ FIX: Pass parameters to SessionWindow
                            await _sessionWindow.HandleServerCommand(command, parameters);
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Error forwarding command to SessionWindow: {ex.Message}");
                        }
                    });
                }
                else
                {
                    // No SessionWindow - handle critical commands locally for ClientWindow
                    switch (command.ToLowerInvariant())
                    {
                        case "shutdown":
                            await ShutdownAsync();
                            break;

                        case "restart":
                            await RestartAsync();
                            break;

                        case "sleep":
                            await SleepAsync();
                            break;

                        default:
                            LogMessage($"No SessionWindow available - cannot execute command: {command}");
                            break;
                    }
                }

                // Send acknowledgment
                await SendResponseAsync(command, "success");
            }
            catch (Exception ex)
            {
                LogMessage($"Error executing command: {ex.Message}");
                await SendResponseAsync(command, "error", ex.Message);
            }
        }

        private async Task SendResponseAsync(string command, string status, string? details = null)
        {
            if (_stream == null) return;

            try
            {
                var response = new
                {
                    type = "response",
                    command,
                    status,
                    details,
                    timestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(response);
                var data = Encoding.UTF8.GetBytes(json + "\n");

                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();
            }
            catch (Exception ex)
            {
                LogMessage($"Error sending response: {ex.Message}");
            }
        }

        private async Task ShutdownAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LogMessage("Shutdown requested by admin");
                UpdateStatus("Shutting down...");

                // Force close to allow shutdown
                _allowClose = true;

                // Execute shutdown command
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/s /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            });
        }

        private async Task RestartAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LogMessage("Restart requested by admin");
                UpdateStatus("Restarting...");

                // Force close to allow restart
                _allowClose = true;

                // Execute restart command
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            });
        }

        private async Task SleepAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LogMessage("Sleep requested by admin");
                UpdateStatus("Entering sleep mode...");

                // Set system to sleep mode using P/Invoke
                SetSuspendState(false, true, false);
            });
        }

        public void UpdateClientName(string newName)
        {
            Dispatcher.Invoke(() =>
            {
                PcNameTextBox.Text = newName;
                SaveSettings();
            });
            LogMessage($"PC name updated to: {newName}");
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