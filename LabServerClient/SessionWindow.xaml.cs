using LabServerClient;
using LabServerClient.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;


namespace LabServerClient
{
    public partial class SessionWindow : Window

    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private bool _isConnected = false;
        private readonly DispatcherTimer _heartbeatTimer;
        private DateTime _lastResponse = DateTime.MinValue;
        private readonly DispatcherTimer _usageLimitUiTimer;
        private CancellationTokenSource? _usageLimitCts;
        private DateTime? _usageLimitExpiryUtc;
        private readonly DatabaseService? _databaseService;
        private bool _allowClose = false;
        private LockpcWindow? _kioskModeWindow;
        private readonly ClientWindow? _clientWindow;
        private readonly DispatcherTimer _updateTimer;
        private readonly int? _clientId;
        private readonly string? _connectionString;
        private string? _username;
        private readonly DispatcherTimer _logoutRequestCheckTimer;
        private bool _hasPendingLogoutRequest = false;
        private readonly DispatcherTimer _serverStartCheckTimer;
        private bool _isWaitingForServerStart = false;

        public event EventHandler<string>? CurrentCommandChanged;

        // Remote viewing (screen sharing) fields
        private CancellationTokenSource? _screenShareCts;
        private int _screenShareIntervalMs = 500;

        // DllImport for remote input
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

        public SessionWindow(ClientWindow? clientWindow = null, int? clientId = null, DatabaseService? databaseService = null)
        {
            _clientWindow = clientWindow;
            _clientId = clientId;
            _databaseService = databaseService;
            _connectionString = _databaseService?.ConnectionString;
            _username = Environment.UserName;
            InitializeComponent();

            _heartbeatTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _heartbeatTimer.Tick += HeartbeatTimer_Tick;
            _heartbeatTimer.Start();

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();

            _usageLimitUiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _usageLimitUiTimer.Tick += UsageLimitUiTimer_Tick;

            // Timer to check for logout request approval
            _logoutRequestCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5) // Check every 5 seconds
            };
            _logoutRequestCheckTimer.Tick += LogoutRequestCheckTimer_Tick;

            // Timer to check for server start (when locked waiting for instructor)
            _serverStartCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3) // Check every 3 seconds
            };
            _serverStartCheckTimer.Tick += ServerStartCheckTimer_Tick;

            // Usage limit now managed independently; default to no limit
            _usageLimitExpiryUtc = null;

            UpdateDisplay();
            Closing += SessionWindow_Closing;

            // Set automatic timer based on schedule
        }

        /// <summary>
        /// Initializes the automatic timer based on the student's class schedule
        /// </summary>
        private async Task InitializeScheduleBasedTimerAsync()
        {
            try
            {
                if (_databaseService == null)
                {
                    LogMessage("[TIMER] No database service - skipping schedule-based timer");
                    return;
                }

                // ← Gamitin ang studNo, hindi Environment.UserName
                var studNo = _username;


                if (string.IsNullOrWhiteSpace(studNo))
                {
                    LogMessage("[TIMER] No studNo set - skipping schedule-based timer");
                    return;
                }

                var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;
                LogMessage($"[TIMER] Fetching schedule for studNo='{studNo}', pcName='{pcName}'");

                var schedule = await _databaseService.GetStudentScheduleAsync(studNo, pcName);

                if (schedule == null || !schedule.Value.scheduleEnd.HasValue)
                {
                    LogMessage("[TIMER] No schedule found for today - no automatic timer set");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Ipakita na walang schedule pero huwag mag-lock agad
                        TimeRemainingText.Text = "No Schedule";
                        TimeRemainingText.Foreground = System.Windows.Media.Brushes.Gray;
                    });
                    return;
                }

                var now = DateTime.Now;
                var scheduleEnd = schedule.Value.scheduleEnd.Value;
                var scheduleStart = schedule.Value.scheduleStart;
                var serverStartTime = schedule.Value.serverStart;

                LogMessage($"[TIMER] Schedule: {scheduleStart:HH:mm} - {scheduleEnd:HH:mm}, ServerStart: {serverStartTime}");

                // Check kung tapos na ang schedule
                if (scheduleEnd <= now)
                {
                    LogMessage($"[TIMER] Schedule already ended at {scheduleEnd:HH:mm:ss} - locking system");
                    await Dispatcher.InvokeAsync(async () => await LockSystem());
                    return;
                }

                // Check kung nagsimula na ang server
                if (serverStartTime == null)
                {
                    LogMessage("[TIMER] Server has not started yet - locking screen and waiting");
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await LockSystemWithMessage(
                            "⏳ Waiting for Instructor\n\n" +
                            "The lab server has not been started yet.\n" +
                            "Please wait for your instructor to start the session.\n\n" +
                            "Timer will begin when server starts.");
                        _isWaitingForServerStart = true;
                        _serverStartCheckTimer.Start();
                    });
                    return;
                }

                // I-set ang timer based sa schedule end time
                var timeRemaining = scheduleEnd - now;

                LogMessage($"[TIMER] Time remaining: {timeRemaining:hh\\:mm\\:ss} (ends at {scheduleEnd:HH:mm:ss})");

                await Dispatcher.InvokeAsync(() =>
                {
                    // I-cancel ang existing timer kung meron
                    _usageLimitCts?.Cancel();
                    _usageLimitCts?.Dispose();

                    _usageLimitExpiryUtc = scheduleEnd.ToUniversalTime();
                    _usageLimitCts = new CancellationTokenSource();
                    _usageLimitUiTimer.Start();
                    UpdateDisplay();

                    LogMessage($"[TIMER] Timer activated — expires {scheduleEnd:HH:mm:ss}");
                });

                _ = Task.Run(() => MonitorUsageLimitAsync(
                    _usageLimitExpiryUtc!.Value,
                    _usageLimitCts!.Token));
            }
            catch (Exception ex)
            {
                LogMessage($"[TIMER] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks periodically if the server has started (used when locked waiting for instructor)
        /// </summary>
        private async void ServerStartCheckTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isWaitingForServerStart || _databaseService == null || string.IsNullOrWhiteSpace(_username))
            {
                return;
            }

            try
            {
                var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;
                var schedule = await _databaseService.GetStudentScheduleAsync(_username, pcName);
                var serverStartTime = schedule?.serverStart;

                if (serverStartTime.HasValue)
                {
                    // Server has started! Unlock the screen
                    LogMessage($"[SERVER] Server started at {serverStartTime.Value:HH:mm:ss} - unlocking screen");

                    _serverStartCheckTimer.Stop();
                    _isWaitingForServerStart = false;

                    await Dispatcher.InvokeAsync(async () =>
                    {
                        // Unlock the system
                        if (_kioskModeWindow != null)
                        {
                            try
                            {
                                _kioskModeWindow.Close();
                            }
                            catch { }
                            _kioskModeWindow = null;
                        }

                        // Show notification
                        MessageBox.Show(
                            "The instructor has started the lab server.\n\nYou may now begin your work.",
                            "Server Started",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        // Re-initialize timer now that server has started
                        await InitializeScheduleBasedTimerAsync();
                    });
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[SERVER] Error checking server start: {ex.Message}");
            }
        }

        /// <summary>
        /// Locks the system with a custom message displayed on the lock screen
        /// </summary>
        private async Task LockSystemWithMessage(string message)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    // Close existing kiosk window if any
                    if (_kioskModeWindow != null)
                    {
                        try
                        {
                            _kioskModeWindow.Close();
                        }
                        catch { }
                        _kioskModeWindow = null;
                    }

                    // Show kiosk mode window with custom message
                    _kioskModeWindow = new LockpcWindow(message);
                    _kioskModeWindow.WindowState = WindowState.Maximized;
                    _kioskModeWindow.Show();
                    _kioskModeWindow.Activate();
                    _kioskModeWindow.Focus();
                    _kioskModeWindow.BringIntoView();
                });

                LogMessage($"System locked - Waiting for server to start");
            }
            catch (Exception ex)
            {
                LogMessage($"Lock with message error: {ex.Message}");
            }
        }

        public void SetUsername(string username)
        {
            _username = username;
            Dispatcher.Invoke(() => UsernameText.Text = _username ?? string.Empty);
            Dispatcher.BeginInvoke(async () => await InitializeScheduleBasedTimerAsync()); 
        }

        public void InitializeTcpListening()
        {
            if (_clientWindow == null || !_clientWindow.IsConnected())
            {
                LogMessage("Cannot initialize TCP listening - client not connected");
                return;
            }

            _stream = _clientWindow.GetNetworkStream();
            if (_stream != null)
            {
                _isConnected = true;
                LogMessage("TCP listening initialized - starting command listener");
                _ = Task.Run(() => ListenForCommands());
            }
            else
            {
                LogMessage("TCP listening failed - network stream is null");
            }
        }

        private void UpdateDisplay()
        {
            UsernameText.Text = _username ?? string.Empty;
            TimeRemainingText.Visibility = Visibility.Visible;

            if (_usageLimitExpiryUtc.HasValue)
            {
                var now = DateTime.UtcNow;
                var remaining = _usageLimitExpiryUtc.Value - now;

                if (remaining.TotalSeconds > 0)
                {
                    var hours = (int)remaining.TotalHours;
                    var minutes = remaining.Minutes;
                    var seconds = remaining.Seconds;

                    TimeRemainingText.Text = $"{hours:D2}:{minutes:D2}:{seconds:D2}";

                    if (remaining.TotalMinutes < 5)
                    {
                        TimeRemainingText.Foreground = System.Windows.Media.Brushes.Red;
                    }
                    else if (remaining.TotalMinutes < 15)
                    {
                        TimeRemainingText.Foreground = System.Windows.Media.Brushes.Orange;
                    }
                    else
                    {
                        TimeRemainingText.Foreground = System.Windows.Media.Brushes.DarkBlue;
                    }
                }
                else
                {
                    TimeRemainingText.Text = "00:00:00";
                    TimeRemainingText.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            else
            {
                TimeRemainingText.Text = "00:00:00";
                TimeRemainingText.Foreground = System.Windows.Media.Brushes.DarkBlue;
            }
        }

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            UpdateDisplay();
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            // Check if student is in active schedule
            if (_databaseService != null && !string.IsNullOrWhiteSpace(_username))
            {
                var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;
                var isInActiveSchedule = await _databaseService.IsStudentInActiveScheduleAsync(_username, pcName);

                if (isInActiveSchedule)
                {
                    // Show logout request dialog
                    var logoutRequestDialog = new LogoutRequestDialog();
                    var dialogResult = logoutRequestDialog.ShowDialog();

                    if (dialogResult == true && logoutRequestDialog.UserConfirmed)
                    {
                        // User wants to send logout request
                        var requestId = await _databaseService.CreateLogoutRequestAsync(
                            _username,
                            pcName,
                            $"Student '{_username}' requesting early logout during active schedule"
                        );

                        if (requestId.HasValue)
                        {
                            MessageBox.Show(
                                "Your logout request has been sent to the instructor.\n\nYou will be notified when it is approved.",
                                "Request Sent",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);

                            // Log the logout request activity
                            await _databaseService.LogStudentActivityAsync(_username, pcName, "Logout Request", "Student requested early logout");

                            // Start polling for approval
                            _hasPendingLogoutRequest = true;
                            _logoutRequestCheckTimer.Start();
                        }
                        else
                        {
                            MessageBox.Show(
                                "Failed to send logout request. Please try again or contact your instructor.",
                                "Request Failed",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }

                        // Don't log out - wait for approval
                        return;
                    }
                    else
                    {
                        // User cancelled the logout request
                        return;
                    }
                }
            }

            // Not in active schedule - show normal logout confirmation
            var result = MessageBox.Show(
                "Are you sure you want to log out?",
                "Confirm Logout",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            // Record logout in attendance logs before closing
            if (_databaseService != null && !string.IsNullOrWhiteSpace(_username))
            {
                var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;

                try
                {
                    // Record logout attendance
                    var logoutRecorded = await _databaseService.RecordStudentLogoutAsync(_username, pcName);

                    if (logoutRecorded)
                    {
                        LogMessage($"Logout attendance recorded for {_username}");
                    }

                    // Log logout activity
                    await _databaseService.LogStudentActivityAsync(_username, pcName, "Logout", "Student logged out");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error recording logout: {ex.Message}");
                }
            }

            // Proceed with logout
            var app = Application.Current as App;

            // Prevent app shutdown when closing windows during logout
            if (app != null)
            {
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            _allowClose = true;

            // Close this window
            try
            {
                Close();
            }
            catch { }

            // Close ClientWindow
            if (_clientWindow != null)
            {
                try
                {
                    await _clientWindow.DisconnectFromServer();
                    _clientWindow.Close();
                }
                catch { }
            }

            // Show login again
            if (app != null)
            {
                app.ShowLoginWindow();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
        }

        private void StorageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_clientId.HasValue)
            {
                MessageBox.Show("Storage is unavailable because the client account id is missing.", "Storage", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Log activity: Opened Storage
            if (_databaseService != null && !string.IsNullOrWhiteSpace(_username))
            {
                var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;
                _ = _databaseService.LogStudentActivityAsync(_username, pcName, "Open Storage", "Student accessed My Storage");
            }

            var storageWindow = new StorageWindow(_clientId.Value, _connectionString)
            {
                Owner = this
            };
            storageWindow.Show();
        }

        private void SessionWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                MessageBox.Show("Please use the Logout button to exit.", "Logout Required",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        public void SetUsageLimitExpiry(DateTime? expiryUtc)
        {
            _usageLimitExpiryUtc = expiryUtc;
            UpdateDisplay();
        }

        // Remote viewing methods
        public Task<string?> StartScreenShare(string? parameters)
        {
            try
            {


                LogMessage($"[StartScreenShare] Called with parameters: {parameters ?? "null"}");
                int interval = _screenShareIntervalMs;

                if (!string.IsNullOrWhiteSpace(parameters))
                {
                    LogMessage($"[StartScreenShare] Deserializing parameters: {parameters}");
                    var request = JsonSerializer.Deserialize<ScreenStreamRequest>(parameters);
                    if (request?.Interval > 0)
                    {
                        interval = request.Interval;
                        LogMessage($"[StartScreenShare] Interval set from parameters: {interval}");
                    }
                }

                interval = Math.Clamp(interval, 100, 2000);
                LogMessage($"[StartScreenShare] Final interval: {interval}ms");

                ResetScreenShareState();

                if (_clientWindow == null)
                {
                    LogMessage("[StartScreenShare] ERROR: _clientWindow is null");
                    return Task.FromResult<string?>("Screen streaming unavailable: client window is null");
                }

                if (!_clientWindow.IsConnected())
                {
                    LogMessage("[StartScreenShare] ERROR: ClientWindow is not connected");
                    return Task.FromResult<string?>("Screen streaming unavailable: client not connected");
                }

                LogMessage("[StartScreenShare] ClientWindow check passed, starting capture loop");

                _screenShareIntervalMs = interval;
                _screenShareCts = new CancellationTokenSource();

                _ = Task.Run(() => CaptureScreenLoopAsync(_screenShareIntervalMs, _screenShareCts.Token));

                var successMessage = $"Screen streaming started ({_screenShareIntervalMs} ms interval)";
                LogMessage($"[StartScreenShare] {successMessage}");
                return Task.FromResult<string?>(successMessage);
            }
            catch (Exception ex)
            {
                LogMessage($"[StartScreenShare] EXCEPTION: {ex.Message}\nStack: {ex.StackTrace}");
                return Task.FromResult<string?>($"Screen streaming error: {ex.Message}");
            }
        }

        public Task<string?> StopScreenShare()
        {
            if (_screenShareCts == null)
            {
                return Task.FromResult<string?>("Screen streaming already stopped");
            }

            ResetScreenShareState();
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
            LogMessage($"[CaptureScreenLoop] Started with interval {intervalMs}ms");
            int frameCount = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var frame = CaptureScreenFrame();
                    if (frame != null)
                    {
                        frameCount++;
                        if (frameCount % 10 == 0) // Log every 10 frames to avoid spam
                        {
                            LogMessage($"[CaptureScreenLoop] Captured frame #{frameCount}, size: {frame.Value.ImageBytes.Length} bytes");
                        }

                        var success = await SendScreenFrameAsync(frame.Value.ImageBytes, frame.Value.Width, frame.Value.Height, token);
                        if (!success)
                        {
                            LogMessage("[CaptureScreenLoop] SendScreenFrameAsync returned false, stopping loop");
                            break;
                        }
                    }
                    else
                    {
                        LogMessage("[CaptureScreenLoop] CaptureScreenFrame returned null");
                    }
                }
                catch (OperationCanceledException)
                {
                    LogMessage("[CaptureScreenLoop] Cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage($"[CaptureScreenLoop] Exception: {ex.Message}");
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
                    LogMessage("[CaptureScreenLoop] Task cancelled during delay");
                    break;
                }
            }
            LogMessage($"[CaptureScreenLoop] Stopped after {frameCount} frames");
        }

        private async Task<bool> SendScreenFrameAsync(byte[] imageBytes, int width, int height, CancellationToken token)
        {
            if (_clientWindow == null || !_clientWindow.IsConnected())
            {
                LogMessage("[SendScreenFrame] ClientWindow is null or not connected");
                return false;
            }

            var stream = _clientWindow.GetNetworkStream();
            if (stream == null)
            {
                LogMessage("[SendScreenFrame] Network stream is null");
                return false;
            }

            var message = new
            {
                type = "screen",
                clientName = _clientWindow.GetClientName(),
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
                await stream.WriteAsync(data.AsMemory(), token);
                await stream.FlushAsync(token);
                return true;
            }
            catch (OperationCanceledException)
            {
                LogMessage("[SendScreenFrame] Operation cancelled");
                return false;
            }
            catch (Exception ex)
            {
                LogMessage($"[SendScreenFrame] Exception: {ex.Message}");
                return false;
            }
        }

        // ✅ FIX: SystemParameters must be accessed on the UI thread
        private (byte[] ImageBytes, int Width, int Height)? CaptureScreenFrame()
        {
            int screenWidth = 0;
            int screenHeight = 0;

            // SystemParameters must be accessed on UI thread
            Dispatcher.Invoke(() =>
            {
                screenWidth = (int)SystemParameters.PrimaryScreenWidth;
                screenHeight = (int)SystemParameters.PrimaryScreenHeight;
            });

            if (screenWidth == 0 || screenHeight == 0)
                return null;

            try
            {
                using var bitmap = new Bitmap(screenWidth, screenHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                }

                using var ms = new MemoryStream();

                // Use JPEG with reduced quality to keep frame size small and fast
                var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo
                    .GetImageEncoders()
                    .FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);

                if (jpegEncoder != null)
                {
                    var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, 40L); // 40% — visible but small
                    bitmap.Save(ms, jpegEncoder, encoderParams);
                }
                else
                {
                    bitmap.Save(ms, ImageFormat.Jpeg); // fallback
                }

                return (ms.ToArray(), screenWidth, screenHeight);
            }
            catch (Exception ex)
            {
                LogMessage($"Screen capture error: {ex.Message}");
                return null;
            }
        }

        public Task<string?> HandleRemoteInput(string? parameters)
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
            catch (Exception)
            {
                // Ignore remote input errors
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

            if (_isConnected && _clientWindow != null)
            {
                _ = Task.Run(async () => await _clientWindow.DisconnectFromServer());
            }

            _heartbeatTimer?.Stop();
            base.OnClosed(e);
        }

        private async Task ListenForCommands()
        {
            var buffer = new byte[8192];
            var messageBuilder = new StringBuilder();

            while (_isConnected && _stream != null)
            {
                try
                {
                    var bytesRead = await _stream.ReadAsync(buffer.AsMemory());
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(message);

                    // Process complete messages while preserving any partial trailing chunk
                    var fullMessage = messageBuilder.ToString();
                    var startIndex = 0;

                    while (true)
                    {
                        var newlineIndex = fullMessage.IndexOf('\n', startIndex);
                        if (newlineIndex == -1)
                        {
                            break; // No complete message available yet
                        }

                        var lineLength = newlineIndex - startIndex;
                        if (lineLength > 0)
                        {
                            var line = fullMessage.Substring(startIndex, lineLength).Trim();
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                await ProcessCommand(line);
                            }
                        }

                        startIndex = newlineIndex + 1;
                    }

                    // Keep any partial message for the next read
                    if (startIndex >= fullMessage.Length)
                    {
                        messageBuilder.Clear();
                    }
                    else
                    {
                        messageBuilder.Clear();
                        messageBuilder.Append(fullMessage.AsSpan(startIndex));
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Command listening error: {ex.Message}");
                    break;
                }
            }

            LogMessage("ListenForCommands loop ended");
        }

        private async Task ProcessCommand(string commandJson)
        {
            try
            {
                LogMessage($"Raw JSON: {commandJson}");

                var command = JsonSerializer.Deserialize<SessionServerCommand>(
                    commandJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (command == null || string.IsNullOrWhiteSpace(command.Command))
                {
                    LogMessage("Invalid or empty command received");
                    return;
                }

                var commandDisplay = $"{command.Command} {command.Parameters ?? ""}";

                Dispatcher.Invoke(() =>
                {
                    LogMessage($"Received command: {command.Command}");
                });

                CurrentCommandChanged?.Invoke(this, commandDisplay);

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

        /// <summary>
        /// Public method to handle server commands from ClientWindow
        /// </summary>
        public async Task<string?> HandleServerCommand(string command, string? parameters = null)
        {
            return await ExecuteCommand(command, parameters);
        }

        private async Task<string?> ExecuteCommand(string command, string? parameters)
        {
            LogMessage($"[ExecuteCommand] Command: '{command}', Parameters: '{parameters ?? "null"}'");

            if (string.IsNullOrWhiteSpace(command))
            {
                LogMessage("[ExecuteCommand] Invalid command - empty or null");
                return "Invalid command";
            }

            var result = string.Empty;
            switch (command.Trim().ToLowerInvariant())
            {
                case "lock":
                    result = await LockSystem();
                    break;
                case "unlock":
                    result = await UnlockSystem();
                    break;
                case "maintenance_lock":
                    result = await MaintenanceLockSystem(parameters);
                    break;
                case "shutdown":
                    result = await ShutdownSystem();
                    break;
                case "restart":
                    result = await RestartSystem();
                    break;
                case "sleep":
                    result = await SleepSystem();
                    break;
                case "status":
                    result = await GetSystemStatus();
                    break;
                case "set_usage_limit":
                    result = await SetUsageLimit(parameters);
                    break;
                case "start_screen_stream":
                    LogMessage("[ExecuteCommand] Calling StartScreenShare...");
                    result = await StartScreenShare(parameters);
                    LogMessage($"[ExecuteCommand] StartScreenShare returned: {result}");
                    break;
                case "stop_screen_stream":
                    result = await StopScreenShare();
                    break;
                case "remote_input":
                    result = await HandleRemoteInput(parameters);
                    break;
                case "force_logout":
                    result = await ForceLogout();
                    break;
                case "server_shutdown":
                    result = await HandleServerShutdown();
                    break;
                case "update_config":
                    result = await UpdateConfiguration(parameters);
                    break;
                default:
                    result = $"Unknown command: {command}";
                    LogMessage($"[ExecuteCommand] Unknown command: {command}");
                    break;
            }
            LogMessage($"[ExecuteCommand] Result for '{command}': {result}");
            return result;
        }

        private async Task<string> ForceLogout()
        {
            try
            {
                LogMessage("Force logout command received - logout request approved");

                // Stop polling timer if active
                _logoutRequestCheckTimer?.Stop();
                _hasPendingLogoutRequest = false;

                // Show notification
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        "Your logout request has been approved by the instructor.\n\nYou will now be logged out.",
                        "Logout Approved",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                // Perform logout
                await PerformLogout();

                return "Logout successful";
            }
            catch (Exception ex)
            {
                LogMessage($"Force logout error: {ex.Message}");
                return $"Force logout failed: {ex.Message}";
            }
        }

        private async Task<string> HandleServerShutdown()
        {
            try
            {
                LogMessage("Server shutdown command received - server is stopping");

                // Show notification
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "The server has been stopped by the administrator.\n\nYou will be logged out automatically.",
                        "Server Shutdown",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                // Perform logout
                await PerformLogout();

                return "Server shutdown processed - logout successful";
            }
            catch (Exception ex)
            {
                LogMessage($"Server shutdown handler error: {ex.Message}");
                return $"Server shutdown handler failed: {ex.Message}";
            }
        }

        private async Task<string> UpdateConfiguration(string? parameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(parameters))
                {
                    LogMessage("Update config command received but no parameters provided");
                    return "No configuration parameters provided";
                }

                LogMessage($"Update config command received: {parameters}");

                // Parse JSON configuration
                var config = JsonSerializer.Deserialize<ConfigurationUpdate>(parameters);
                if (config == null)
                {
                    return "Invalid configuration format";
                }

                // Update PC name in Windows Registry if provided
                if (!string.IsNullOrWhiteSpace(config.pc_name))
                {
                    try
                    {
                        var regKey = Registry.CurrentUser.OpenSubKey(@"Software\LabServerClient", true)
                            ?? Registry.CurrentUser.CreateSubKey(@"Software\LabServerClient");

                        regKey.SetValue("ClientName", config.pc_name);
                        regKey.Close();

                        LogMessage($"PC name updated in registry: {config.pc_name}");

                        // Update ClientWindow's PC name if available
                        if (_clientWindow != null)
                        {
                            _clientWindow.UpdateClientName(config.pc_name);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to update PC name in registry: {ex.Message}");
                    }
                }

                // Update Server IP in Windows Registry if provided
                if (!string.IsNullOrWhiteSpace(config.server_ip))
                {
                    try
                    {
                        var regKey = Registry.CurrentUser.OpenSubKey(@"Software\LabServerClient", true)
                            ?? Registry.CurrentUser.CreateSubKey(@"Software\LabServerClient");

                        regKey.SetValue("ServerIP", config.server_ip);
                        regKey.Close();

                        LogMessage($"Server IP updated in registry: {config.server_ip}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to update server IP in registry: {ex.Message}");
                    }
                }

                // Show notification to user
                await Dispatcher.InvokeAsync(() =>
                {
                    var message = "Configuration updated by administrator:\n\n";
                    if (!string.IsNullOrWhiteSpace(config.pc_name))
                    {
                        message += $"• PC Name: {config.pc_name}\n";
                    }
                    if (!string.IsNullOrWhiteSpace(config.server_ip))
                    {
                        message += $"• Server IP: {config.server_ip}\n";
                    }
                    message += "\nThe application will restart to apply changes.";

                    MessageBox.Show(
                        message,
                        "Configuration Updated",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                // Schedule application restart
                LogMessage("Scheduling application restart to apply configuration changes");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000); // Wait 2 seconds
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Restart the application
                        System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "LabServerClient.exe");
                        Application.Current.Shutdown();
                    });
                });

                return $"Configuration updated successfully. Application will restart.";
            }
            catch (Exception ex)
            {
                LogMessage($"Update configuration error: {ex.Message}");
                return $"Configuration update failed: {ex.Message}";
            }
        }

        private class ConfigurationUpdate
        {
            public string? pc_name { get; set; }
            public string? server_ip { get; set; }
        }

        private async Task<string> LockSystem()
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    // Close existing kiosk window if any
                    if (_kioskModeWindow != null)
                    {
                        try
                        {
                            _kioskModeWindow.Close();
                        }
                        catch { }
                        _kioskModeWindow = null;
                    }

                    // Show kiosk mode window
                    _kioskModeWindow = new LockpcWindow();
                    _kioskModeWindow.WindowState = WindowState.Maximized;
                    _kioskModeWindow.Show();
                    _kioskModeWindow.Activate();
                    _kioskModeWindow.Focus();
                    _kioskModeWindow.BringIntoView();
                });

                LogMessage("System locked - Kiosk mode activated");
                return "System locked successfully";
            }
            catch (Exception ex)
            {
                LogMessage($"Lock error: {ex.Message}");
                return $"Lock failed: {ex.Message}";
            }
        }

        private async Task<string> MaintenanceLockSystem(string? parameters)
        {
            try
            {
                string maintenanceMessage = "?? Maintenance Mode\n\nThis computer is currently undergoing maintenance.\nPlease use another computer.\n\nThank you for your patience.";

                // Parse maintenance message from parameters if provided
                if (!string.IsNullOrWhiteSpace(parameters))
                {
                    try
                    {
                        var payload = JsonSerializer.Deserialize<MaintenanceLockPayload>(parameters);
                        if (payload != null && !string.IsNullOrWhiteSpace(payload.message))
                        {
                            maintenanceMessage = payload.message;
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, use parameters as plain text message
                        maintenanceMessage = parameters;
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    // Close existing kiosk window if any
                    if (_kioskModeWindow != null)
                    {
                        try
                        {
                            _kioskModeWindow.Close();
                        }
                        catch { }
                        _kioskModeWindow = null;
                    }

                    // Show kiosk mode window with maintenance message
                    _kioskModeWindow = new LockpcWindow(maintenanceMessage);
                    _kioskModeWindow.WindowState = WindowState.Maximized;
                    _kioskModeWindow.Show();
                    _kioskModeWindow.Activate();
                    _kioskModeWindow.Focus();
                    _kioskModeWindow.BringIntoView();
                });

                LogMessage($"System locked - Maintenance mode activated with message: {maintenanceMessage}");
                return "Maintenance lock activated successfully";
            }
            catch (Exception ex)
            {
                LogMessage($"Maintenance lock error: {ex.Message}");
                return $"Maintenance lock failed: {ex.Message}";
            }
        }

        private class MaintenanceLockPayload
        {
            public string? message { get; set; }
        }

        private Task<string> UnlockSystem()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    // Close kiosk mode window if it's open
                    if (_kioskModeWindow != null)
                    {
                        try
                        {
                            _kioskModeWindow.Close();
                        }
                        catch { }
                        _kioskModeWindow = null;
                    }
                });

                LogMessage("System unlocked - Kiosk mode deactivated");
                return Task.FromResult("System unlocked successfully");
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
                    data = response,
                    timestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(message);
                var data = Encoding.UTF8.GetBytes(json + "\n");

                if (_stream != null)
                {
                    await _stream.WriteAsync(data.AsMemory());
                    await _stream.FlushAsync();
                }

                _lastResponse = DateTime.UtcNow;
                LogMessage($"Response sent at {_lastResponse:HH:mm:ss}");
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

        private Task<string> SetUsageLimit(string? parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters))
            {
                ResetUsageLimitState(true);
                return Task.FromResult("Usage limit cleared");
            }

            var trimmed = parameters.Trim();

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<UsageLimitCommandPayload>(parameters);
                    if (payload != null)
                    {
                        return ApplyUsageLimitPayload(payload);
                    }
                }
                catch (JsonException ex)
                {
                    LogMessage($"Usage limit payload error: {ex.Message}");
                }
            }

            if (!double.TryParse(parameters, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) &&
                !double.TryParse(parameters, NumberStyles.Float, CultureInfo.CurrentCulture, out hours))
            {
                return Task.FromResult($"Invalid usage limit parameter: {parameters}");
            }

            if (hours <= 0)
            {
                ResetUsageLimitState(true);
                return Task.FromResult("Usage limit cleared");
            }

            ResetUsageLimitState(true);

            _usageLimitExpiryUtc = DateTime.UtcNow.AddHours(hours);
            OnUsageLimitExpiryChanged();
            _usageLimitCts = new CancellationTokenSource();

            Dispatcher.Invoke(() =>
            {
                _usageLimitUiTimer.Start();
                UpdateUsageLimitDisplay();
            });

            _ = Task.Run(() => MonitorUsageLimitAsync(_usageLimitExpiryUtc.Value, _usageLimitCts.Token));

            return Task.FromResult($"Usage limit set to {hours:0.##} hours (until {_usageLimitExpiryUtc.Value.ToLocalTime():t}).");
        }

        private Task<string> ApplyUsageLimitPayload(UsageLimitCommandPayload payload)
        {
            if (!payload.Hours.HasValue || payload.Hours.Value <= 0 || !payload.ExpiresUtc.HasValue)
            {
                ResetUsageLimitState(true);
                return Task.FromResult("Usage limit cleared");
            }

            ResetUsageLimitState(true);

            _usageLimitExpiryUtc = DateTime.SpecifyKind(payload.ExpiresUtc.Value, DateTimeKind.Utc);
            OnUsageLimitExpiryChanged();
            _usageLimitCts = new CancellationTokenSource();

            Dispatcher.Invoke(() =>
            {
                _usageLimitUiTimer.Start();
                UpdateUsageLimitDisplay();
            });

            _ = Task.Run(() => MonitorUsageLimitAsync(_usageLimitExpiryUtc.Value, _usageLimitCts.Token));

            var sessionStart = payload.SessionStartUtc.HasValue
                ? payload.SessionStartUtc.Value.ToLocalTime().ToString("t")
                : "now";

            var expiryLocal = _usageLimitExpiryUtc.Value.ToLocalTime().ToString("t");
            var statusMessage = $"Usage limit synchronized. Session start: {sessionStart}, ends at {expiryLocal}.";
            LogMessage(statusMessage);

            return Task.FromResult($"Usage limit active until {expiryLocal}");
        }

        private async Task MonitorUsageLimitAsync(DateTime expiryUtc, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var remaining = expiryUtc - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;

                    // Mag-warn 5 minutes bago maubos
                    if (remaining.TotalMinutes <= 5 && remaining.TotalMinutes > 4.9)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show(
                                "⚠️ 5 minutes remaining!\n\nPlease save your work.",
                                "Session Ending Soon",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        });
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }

                if (token.IsCancellationRequested) return;

                LogMessage("[TIMER] Session time expired — locking and shutting down");

                // I-record ang logout sa database
                if (_databaseService != null && !string.IsNullOrWhiteSpace(_username))
                {
                    var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;
                    await _databaseService.RecordStudentLogoutAsync(_username, pcName);
                    await _databaseService.LogStudentActivityAsync(_username, pcName,
                        "Auto Logout", "Session ended - usage limit reached");
                }

                // I-lock muna bago mag-shutdown
                await LockSystem();
                await SendResponse("Session time expired - system will shut down");

                // Mag-shutdown after 30 seconds
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "Your session has ended.\n\nThis computer will shut down in 30 seconds.",
                        "Session Ended",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                await Task.Run(() =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/s /t 30 /c \"Lab session ended\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                });

                ResetUsageLimitState(false);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                LogMessage($"[TIMER] Monitor error: {ex.Message}");
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
            UpdateDisplay();
        }

        private void UpdateUsageLimitDisplay()
        {
            // Usage limit display is now handled by SessionWindow
            // This method is kept for compatibility but does nothing
        }

        private void OnUsageLimitExpiryChanged()
        {
            Dispatcher.Invoke(UpdateDisplay);
        }

        private async void LogoutRequestCheckTimer_Tick(object? sender, EventArgs e)
        {
            if (!_hasPendingLogoutRequest || _databaseService == null || string.IsNullOrWhiteSpace(_username))
            {
                return;
            }

            try
            {
                var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;
                var isApproved = await _databaseService.CheckLogoutRequestApprovalAsync(_username, pcName);

                if (isApproved)
                {
                    // Stop checking
                    _logoutRequestCheckTimer.Stop();
                    _hasPendingLogoutRequest = false;

                    // Show notification
                    MessageBox.Show(
                        "Your logout request has been approved by the instructor.\n\nYou will now be logged out.",
                        "Logout Approved",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // Proceed with logout
                    await PerformLogout();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error checking logout request: {ex.Message}");
            }
        }

        private async Task PerformLogout()
        {
            // Record logout time in attendance logs
            if (_databaseService != null && !string.IsNullOrWhiteSpace(_username))
            {
                var pcName = _clientWindow?.GetClientName() ?? Environment.MachineName;

                try
                {
                    // Record logout attendance
                    var logoutRecorded = await _databaseService.RecordStudentLogoutAsync(_username, pcName);

                    if (logoutRecorded)
                    {
                        LogMessage($"Logout attendance recorded for {_username}");
                    }

                    // Log logout activity
                    await _databaseService.LogStudentActivityAsync(_username, pcName, "Logout", "Student logged out");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error recording logout: {ex.Message}");
                }
            }

            var app = Application.Current as App;

            if (app != null)
            {
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            _allowClose = true;

            try
            {
                Close();
            }
            catch { }

            if (_clientWindow != null)
            {
                try
                {
                    await _clientWindow.DisconnectFromServer();
                    _clientWindow.Close();
                }
                catch { }
            }

            if (app != null)
            {
                app.ShowLoginWindow();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
        }

        private async Task DisconnectFromServer()
        {
            try
            {
                _isConnected = false;
                _heartbeatTimer?.Stop();
                _usageLimitUiTimer?.Stop();
                _logoutRequestCheckTimer?.Stop();
                _serverStartCheckTimer?.Stop();

                ResetUsageLimitState(true);
                ResetScreenShareState();

                if (_stream != null)
                {
                    await _stream.FlushAsync();
                    _stream.Close();
                    _stream.Dispose();
                    _stream = null;
                }

                if (_tcpClient != null)
                {
                    _tcpClient.Close();
                    _tcpClient.Dispose();
                    _tcpClient = null;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Disconnect error: {ex.Message}");
            }
        }

        private void LogMessage(string message)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[SessionWindow] {DateTime.Now:HH:mm:ss}: {message}");
            }
            catch
            {
                // Ignore logging failures
            }
        }

        private class UsageLimitCommandPayload
        {
            public double? Hours { get; set; }
            public DateTime? SessionStartUtc { get; set; }
            public DateTime? ExpiresUtc { get; set; }
        }
    }

    public class SessionServerCommand
    {
        public string Type { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string? Parameters { get; set; }
        public DateTime Timestamp { get; set; }
    }
}