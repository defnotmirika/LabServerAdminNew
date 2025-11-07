using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LabServerClient
{
    public partial class ClientWindow : Window
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private bool _isConnected = false;
        private readonly DispatcherTimer _heartbeatTimer;
        private DateTime _lastResponse = DateTime.MinValue;
        private readonly DispatcherTimer _usageLimitUiTimer;
        private CancellationTokenSource? _usageLimitCts;
        private DateTime? _usageLimitExpiryUtc;
        private CancellationTokenSource? _screenShareCts;
        private int _screenShareIntervalMs = 500;

        public ClientWindow()
        {
            InitializeComponent();
            
            // Setup heartbeat timer
            _heartbeatTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _heartbeatTimer.Tick += HeartbeatTimer_Tick;

            _usageLimitUiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _usageLimitUiTimer.Tick += UsageLimitUiTimer_Tick;
            
            // Load settings
            LoadSettings();
            
            // Setup startup
            SetupStartup();
            
            UpdateStatus("Ready to connect");
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
            }
            catch (Exception ex)
            {
                LogMessage($"Error loading settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\LabServerClient", true);
                if (key != null)
                {
                    key.SetValue("ServerIP", ServerIpTextBox.Text);
                    key.SetValue("PCName", PcNameTextBox.Text);
                    key.Close();
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

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                await ConnectToServer();
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                await DisconnectFromServer();
            }
        }

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
                
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                ServerIpTextBox.IsEnabled = false;
                PcNameTextBox.IsEnabled = false;

                // Send registration message
                await SendRegistrationMessage();
                
                // Start listening for commands
                _ = Task.Run(ListenForCommands);
                
                // Start heartbeat
                _heartbeatTimer.Start();
                
                SaveSettings();
                UpdateStatus($"Connected to {ServerIpTextBox.Text}");
                LogMessage($"Connected to server {ServerIpTextBox.Text}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Connection failed: {ex.Message}");
                LogMessage($"Connection error: {ex.Message}");
            }
        }

        private async Task DisconnectFromServer()
        {
            try
            {
                _heartbeatTimer.Stop();
                
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
                
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                ServerIpTextBox.IsEnabled = true;
                PcNameTextBox.IsEnabled = true;

                ResetUsageLimitState(true);
                ResetScreenShareState();

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
            try
            {
                var message = new
                {
                    type = "register",
                    clientName = PcNameTextBox.Text,
                    timestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(message);
                var data = Encoding.UTF8.GetBytes(json + "\n");
                
                if (_stream != null)
                {
                    await _stream.WriteAsync(data, 0, data.Length);
                    await _stream.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Registration error: {ex.Message}");
            }
        }

        private async Task ListenForCommands()
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            while (_isConnected && _tcpClient?.Connected == true)
            {
                try
                {
                    if (_stream == null) break;
                    
                    var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(message);

                    // Process complete messages
                    var fullMessage = messageBuilder.ToString();
                    var lines = fullMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            await ProcessCommand(line.Trim());
                        }
                    }

                    messageBuilder.Clear();
                }
                catch (Exception ex)
                {
                    LogMessage($"Command listening error: {ex.Message}");
                    break;
                }
            }
        }

        private async Task ProcessCommand(string commandJson)
        {
            try
            {
                var command = JsonSerializer.Deserialize<ServerCommand>(commandJson);
                if (command == null) return;

                Dispatcher.Invoke(() =>
                {
                    CurrentCommandText.Text = $"{command.Command} {command.Parameters}";
                    LogMessage($"Received command: {command.Command}");
                });

                var result = await ExecuteCommand(command.Command, command.Parameters);
                
                if (!string.IsNullOrEmpty(result))
                {
                    await SendResponse(result);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Command processing error: {ex.Message}");
                await SendResponse($"Error: {ex.Message}");
            }
        }

        private async Task<string?> ExecuteCommand(string command, string? parameters)
        {
            try
            {
                switch (command.ToLower())
                {
                    case "lock":
                        return await LockSystem();
                    case "unlock":
                        return await UnlockSystem();
                    case "shutdown":
                        return await ShutdownSystem();
                    case "restart":
                        return await RestartSystem();
                    case "sleep":
                        return await SleepSystem();
                    case "status":
                        return await GetSystemStatus();
                    case "set_usage_limit":
                        return await SetUsageLimit(parameters);
                    case "start_screen_stream":
                        return await StartScreenShare(parameters);
                    case "stop_screen_stream":
                        return await StopScreenShare();
                    case "remote_input":
                        return await HandleRemoteInput(parameters);
                    default:
                        return $"Unknown command: {command}";
                }
            }
            catch (Exception ex)
            {
                return $"Command execution error: {ex.Message}";
            }
        }

        private async Task<string> LockSystem()
        {
            try
            {
                // Lock the workstation
                await Task.Run(() => 
                {
                    // Use Windows API to lock the workstation
                    System.Diagnostics.Process.Start("rundll32.exe", "user32.dll,LockWorkStation");
                });
                
                LogMessage("System locked");
                return "System locked successfully";
            }
            catch (Exception ex)
            {
                LogMessage($"Lock error: {ex.Message}");
                return $"Lock failed: {ex.Message}";
            }
        }

        private Task<string> UnlockSystem()
        {
            try
            {
                // Note: Unlocking requires user interaction, so we just log it
                LogMessage("Unlock command received (requires user interaction)");
                return Task.FromResult("Unlock command received - user interaction required");
            }
            catch (Exception ex)
            {
                LogMessage($"Unlock error: {ex.Message}");
                return Task.FromResult($"Unlock failed: {ex.Message}");
            }
        }

        private async Task<string> ShutdownSystem()
        {
            try
            {
                LogMessage("Shutdown command received");
                
                // Schedule shutdown in 30 seconds
                await Task.Run(() =>
                {
                    System.Diagnostics.Process.Start("shutdown", "/s /t 30 /c \"Lab Server shutdown command\"");
                });
                
                return "Shutdown scheduled in 30 seconds";
            }
            catch (Exception ex)
            {
                LogMessage($"Shutdown error: {ex.Message}");
                return $"Shutdown failed: {ex.Message}";
            }
        }

        private async Task<string> RestartSystem()
        {
            try
            {
                LogMessage("Restart command received");
                
                // Schedule restart in 30 seconds
                await Task.Run(() =>
                {
                    System.Diagnostics.Process.Start("shutdown", "/r /t 30 /c \"Lab Server restart command\"");
                });
                
                return "Restart scheduled in 30 seconds";
            }
            catch (Exception ex)
            {
                LogMessage($"Restart error: {ex.Message}");
                return $"Restart failed: {ex.Message}";
            }
        }

        private async Task<string> SleepSystem()
        {
            try
            {
                LogMessage("Sleep command received");
                
                await Task.Run(() =>
                {
                    // Use Windows API to put system to sleep
                    System.Diagnostics.Process.Start("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
                });
                
                return "System going to sleep";
            }
            catch (Exception ex)
            {
                LogMessage($"Sleep error: {ex.Message}");
                return $"Sleep failed: {ex.Message}";
            }
        }

        private Task<string> GetSystemStatus()
        {
            try
            {
                var status = new
                {
                    machineName = Environment.MachineName,
                    userName = Environment.UserName,
                    osVersion = Environment.OSVersion.ToString(),
                    uptime = Environment.TickCount,
                    timestamp = DateTime.UtcNow
                };
                
                return Task.FromResult(JsonSerializer.Serialize(status));
            }
            catch (Exception ex)
            {
                return Task.FromResult($"Status error: {ex.Message}");
            }
        }

        private async Task SendResponse(string response)
        {
            try
            {
                var message = new
                {
                    type = "response",
                    clientName = PcNameTextBox.Text,
                    data = response,
                    timestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(message);
                var data = Encoding.UTF8.GetBytes(json + "\n");
                
                if (_stream != null)
                {
                    await _stream.WriteAsync(data, 0, data.Length);
                    await _stream.FlushAsync();
                }
                
                _lastResponse = DateTime.UtcNow;
                Dispatcher.Invoke(() =>
                {
                    LastResponseText.Text = $"Last Response: {_lastResponse:HH:mm:ss}";
                });
            }
            catch (Exception ex)
            {
                LogMessage($"Response error: {ex.Message}");
            }
        }

        private async void HeartbeatTimer_Tick(object? sender, EventArgs e)
        {
            if (_isConnected)
            {
                try
                {
                    await SendResponse("heartbeat");
                }
                catch (Exception ex)
                {
                    LogMessage($"Heartbeat error: {ex.Message}");
                    await DisconnectFromServer();
                }
            }
        }

        private void UsageLimitUiTimer_Tick(object? sender, EventArgs e)
        {
            UpdateUsageLimitDisplay();
        }

        private async Task<string> SetUsageLimit(string? parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters))
            {
                ResetUsageLimitState(true);
                return "Usage limit cleared";
            }

            if (!double.TryParse(parameters, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) &&
                !double.TryParse(parameters, NumberStyles.Float, CultureInfo.CurrentCulture, out hours))
            {
                return $"Invalid usage limit parameter: {parameters}";
            }

            if (hours <= 0)
            {
                ResetUsageLimitState(true);
                return "Usage limit cleared";
            }

            ResetUsageLimitState(true);

            _usageLimitExpiryUtc = DateTime.UtcNow.AddHours(hours);
            _usageLimitCts = new CancellationTokenSource();

            Dispatcher.Invoke(() =>
            {
                _usageLimitUiTimer.Start();
                UpdateUsageLimitDisplay();
            });

            _ = Task.Run(() => MonitorUsageLimitAsync(_usageLimitExpiryUtc.Value, _usageLimitCts.Token));

            return $"Usage limit set to {hours:0.##} hours (until {_usageLimitExpiryUtc.Value.ToLocalTime():t}).";
        }

        private async Task MonitorUsageLimitAsync(DateTime expiryUtc, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var remaining = expiryUtc - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                LogMessage("Usage limit reached - locking system");
                var result = await LockSystem();
                await SendResponse(result);

                ResetUsageLimitState(false);
            }
            catch (TaskCanceledException)
            {
                // Expected when limit is cleared or updated
            }
            catch (Exception ex)
            {
                LogMessage($"Usage limit monitor error: {ex.Message}");
            }
        }

        private void ResetUsageLimitState(bool cancelMonitoring)
        {
            if (cancelMonitoring)
            {
                _usageLimitCts?.Cancel();
            }

            _usageLimitCts?.Dispose();
            _usageLimitCts = null;
            _usageLimitExpiryUtc = null;

            Dispatcher.Invoke(() =>
            {
                _usageLimitUiTimer.Stop();
                UpdateUsageLimitDisplay();
            });
        }

        private void UpdateUsageLimitDisplay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(UpdateUsageLimitDisplay);
                return;
            }

            if (_usageLimitExpiryUtc.HasValue)
            {
                var remaining = _usageLimitExpiryUtc.Value - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                UsageLimitStatusText.Text = $"Usage limit: {remaining:hh\\:mm\\:ss} remaining";
                UsageLimitStatusText.Foreground = remaining <= TimeSpan.FromMinutes(5) ? Brushes.DarkRed : Brushes.DarkBlue;
            }
            else
            {
                UsageLimitStatusText.Text = "Usage limit: none";
                UsageLimitStatusText.Foreground = Brushes.Gray;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private async Task<string?> StartScreenShare(string? parameters)
        {
            try
            {
                int interval = _screenShareIntervalMs;

                if (!string.IsNullOrWhiteSpace(parameters))
                {
                    var request = JsonSerializer.Deserialize<ScreenStreamRequest>(parameters);
                    if (request?.Interval > 0)
                    {
                        interval = request.Interval;
                    }
                }

                interval = Math.Clamp(interval, 100, 2000);

                ResetScreenShareState();

                if (_stream == null)
                {
                    return "Screen streaming unavailable: client not connected";
                }

                _screenShareIntervalMs = interval;
                _screenShareCts = new CancellationTokenSource();

                _ = Task.Run(() => CaptureScreenLoopAsync(_screenShareIntervalMs, _screenShareCts.Token));

                LogMessage($"Screen streaming started at {1000.0 / _screenShareIntervalMs:F1} FPS");
                return $"Screen streaming started ({_screenShareIntervalMs} ms interval)";
            }
            catch (Exception ex)
            {
                LogMessage($"Screen streaming error: {ex.Message}");
                return $"Screen streaming error: {ex.Message}";
            }
        }

        private Task<string?> StopScreenShare()
        {
            if (_screenShareCts == null)
            {
                return Task.FromResult<string?>("Screen streaming already stopped");
            }

            ResetScreenShareState();
            LogMessage("Screen streaming stopped by server");
            return Task.FromResult<string?>("Screen streaming stopped");
        }

        private void ResetScreenShareState()
        {
            if (_screenShareCts != null)
            {
                _screenShareCts.Cancel();
                _screenShareCts.Dispose();
                _screenShareCts = null;
            }
        }

        private async Task CaptureScreenLoopAsync(int intervalMs, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var frame = CaptureScreenFrame();
                    if (frame != null)
                    {
                        await SendScreenFrameAsync(frame.Value.ImageBytes, frame.Value.Width, frame.Value.Height, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage($"Screen capture error: {ex.Message}");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }

                try
                {
                    await Task.Delay(intervalMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private (byte[] ImageBytes, int Width, int Height)? CaptureScreenFrame()
        {
            var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            var screenHeight = (int)SystemParameters.PrimaryScreenHeight;

            using var bitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Jpeg);

            return (ms.ToArray(), screenWidth, screenHeight);
        }

        private async Task SendScreenFrameAsync(byte[] imageBytes, int width, int height, CancellationToken token)
        {
            if (_stream == null || !_isConnected)
            {
                return;
            }

            var message = new
            {
                type = "screen",
                clientName = PcNameTextBox.Text,
                data = Convert.ToBase64String(imageBytes),
                timestamp = DateTime.UtcNow,
                metadata = new Dictionary<string, string>
                {
                    ["width"] = width.ToString(CultureInfo.InvariantCulture),
                    ["height"] = height.ToString(CultureInfo.InvariantCulture)
                }
            };

            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json + "\n");

            try
            {
                await _stream.WriteAsync(data.AsMemory(0, data.Length), token);
                await _stream.FlushAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
            catch (Exception ex)
            {
                LogMessage($"Screen stream send error: {ex.Message}");
                ResetScreenShareState();
            }
        }

        private Task<string?> HandleRemoteInput(string? parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters))
            {
                return Task.FromResult<string?>(null);
            }

            try
            {
                var payload = JsonSerializer.Deserialize<RemoteInputPayload>(parameters);
                if (payload == null || string.IsNullOrWhiteSpace(payload.Event))
                {
                    return Task.FromResult<string?>(null);
                }

                switch (payload.Event)
                {
                    case "mouse_move":
                        HandleMouseMove(payload.Data);
                        break;
                    case "mouse_button":
                        HandleMouseButton(payload.Data);
                        break;
                    case "mouse_wheel":
                        HandleMouseWheel(payload.Data);
                        break;
                    case "key_down":
                        HandleKeyInput(payload.Data, false);
                        break;
                    case "key_up":
                        HandleKeyInput(payload.Data, true);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Remote input error: {ex.Message}");
            }

            return Task.FromResult<string?>(null);
        }

        private void HandleMouseMove(Dictionary<string, string> data)
        {
            if (!data.TryGetValue("x", out var xValue) || !data.TryGetValue("y", out var yValue))
            {
                return;
            }

            if (!double.TryParse(xValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var relX) ||
                !double.TryParse(yValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var relY))
            {
                return;
            }

            relX = Math.Clamp(relX, 0, 1);
            relY = Math.Clamp(relY, 0, 1);

            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            var targetX = (int)Math.Round(relX * (screenWidth - 1));
            var targetY = (int)Math.Round(relY * (screenHeight - 1));

            SetCursorPos(targetX, targetY);
        }

        private void HandleMouseButton(Dictionary<string, string> data)
        {
            if (!data.TryGetValue("button", out var button) || !data.TryGetValue("action", out var action))
            {
                return;
            }

            uint flag = 0;

            if (button.Equals("left", StringComparison.OrdinalIgnoreCase))
            {
                flag = action.Equals("down", StringComparison.OrdinalIgnoreCase) ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            }
            else if (button.Equals("right", StringComparison.OrdinalIgnoreCase))
            {
                flag = action.Equals("down", StringComparison.OrdinalIgnoreCase) ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
            }

            if (flag != 0)
            {
                mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
            }
        }

        private void HandleMouseWheel(Dictionary<string, string> data)
        {
            if (!data.TryGetValue("delta", out var deltaValue))
            {
                return;
            }

            if (!int.TryParse(deltaValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
            {
                return;
            }

            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, UIntPtr.Zero);
        }

        private void HandleKeyInput(Dictionary<string, string> data, bool isKeyUp)
        {
            if (!data.TryGetValue("vk", out var vkValue))
            {
                return;
            }

            if (!int.TryParse(vkValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vk))
            {
                return;
            }

            var flags = isKeyUp ? KEYEVENTF_KEYUP : 0u;
            keybd_event((byte)vk, 0, flags, UIntPtr.Zero);
        }

        private class ScreenStreamRequest
        {
            public int Interval { get; set; } = 500;
        }

        private class RemoteInputPayload
        {
            public string Event { get; set; } = string.Empty;
            public Dictionary<string, string> Data { get; set; } = new();
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogTextBlock.Text += $"[{timestamp}] {message}\n";
                
                // Auto-scroll to bottom
                var scrollViewer = LogTextBlock.Parent as ScrollViewer;
                scrollViewer?.ScrollToEnd();
            });
        }

        private void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                StatusBarText.Text = $"{DateTime.Now:HH:mm:ss} - {message}";
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_isConnected)
            {
                _ = Task.Run(async () => await DisconnectFromServer());
            }
            
            _heartbeatTimer?.Stop();
            base.OnClosed(e);
        }
    }

    public class ServerCommand
    {
        public string Type { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string? Parameters { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
