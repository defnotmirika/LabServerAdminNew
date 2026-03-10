using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabServerAdmin.Services;

namespace LabServerAdmin
{
    public partial class RemoteControlWindow : Window
    {
        private readonly TcpServerService _tcpServerService;
        private readonly string _clientName;
        private int _refreshIntervalMs = 500;
        private double _remoteScreenWidth = 0;
        private double _remoteScreenHeight = 0;
        private bool _isControlEnabled = false;
        private DateTime _lastMouseMoveSent = DateTime.MinValue;

        public RemoteControlWindow(TcpServerService tcpServerService, string clientName)
        {
            InitializeComponent();

            _tcpServerService = tcpServerService;
            _clientName = clientName;

            Title = $"Remote Control - {clientName}";
            HeaderText.Text = $"Viewing {clientName}";
            RefreshRateComboBox.SelectedIndex = 1; // Default to 2 FPS

            _tcpServerService.ScreenDataReceived += TcpServerService_ScreenDataReceived;

            Loaded += RemoteControlWindow_Loaded;
            Closed += RemoteControlWindow_Closed;

            PreviewKeyDown += RemoteControlWindow_PreviewKeyDown;
            PreviewKeyUp += RemoteControlWindow_PreviewKeyUp;
        }

        private async void RemoteControlWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Window loaded for {_clientName}");
                StatusText.Text = $"Requesting stream from {_clientName}...";
                await _tcpServerService.StartScreenStreamAsync(_clientName, _refreshIntervalMs);

                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Start stream command sent to {_clientName}");

                // Wait for first frame with timeout
                await WaitForFirstFrameAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Error: {ex.Message}");
                StatusText.Text = $"Stream error: {ex.Message}";
            }
        }

        private async Task WaitForFirstFrameAsync()
        {
            var timeout = TimeSpan.FromSeconds(10);
            var startTime = DateTime.UtcNow;
            var hasReceivedFrame = false;

            while (DateTime.UtcNow - startTime < timeout)
            {
                await Task.Delay(500);

                // Check if we've received a frame (ScreenImage.Source will be set)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ScreenImage.Source != null)
                    {
                        hasReceivedFrame = true;
                    }
                });

                if (hasReceivedFrame)
                {
                    System.Diagnostics.Debug.WriteLine("[RemoteControl] First frame received successfully");
                    return;
                }
            }

            // Timeout - show diagnostic info
            await Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = $"Waiting for stream... (Client may need to be logged in as student)";
                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Timeout waiting for first frame from {_clientName}");
            });
        }

        private async void RemoteControlWindow_Closed(object? sender, EventArgs e)
        {
            _tcpServerService.ScreenDataReceived -= TcpServerService_ScreenDataReceived;

            try
            {
                await _tcpServerService.StopScreenStreamAsync(_clientName);
            }
            catch
            {
                // Ignore errors when shutting down
            }
        }

        private void TcpServerService_ScreenDataReceived(object? sender, ScreenDataReceivedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[RemoteControl] ScreenDataReceived: ClientName={e.ClientName}, Expected={_clientName}, Size={e.ImageBytes?.Length ?? 0}");

            if (!string.Equals(e.ClientName, _clientName, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Ignoring data from {e.ClientName}, expecting {_clientName}");
                return;
            }

            try
            {
                if (e.ImageBytes == null || e.ImageBytes.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[RemoteControl] ERROR: Received empty image data");
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Error: Received empty image data";
                    });
                    return;
                }

                var metadata = e.Metadata;
                if (metadata.TryGetValue("width", out var widthStr) && double.TryParse(widthStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                {
                    _remoteScreenWidth = width;
                }

                if (metadata.TryGetValue("height", out var heightStr) && double.TryParse(heightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                {
                    _remoteScreenHeight = height;
                }

                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Decoding image: {e.ImageBytes.Length} bytes, {_remoteScreenWidth}x{_remoteScreenHeight}");

                BitmapImage bitmap;
                using (var ms = new MemoryStream(e.ImageBytes))
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Image decoded successfully, updating UI");
                Dispatcher.Invoke(() =>
                {
                    ScreenImage.Source = bitmap;
                    var fps = _refreshIntervalMs > 0 ? (1000.0 / _refreshIntervalMs).ToString("F1") : "N/A";
                    StatusText.Text = $"Streaming at {fps} FPS ({e.ImageBytes.Length / 1024} KB/frame) - {DateTime.Now:T}";
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteControl] ERROR decoding image: {ex.Message}\nStack: {ex.StackTrace}");
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Error decoding image: {ex.Message}";
                });
            }
        }

        private async void RefreshRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            if (RefreshRateComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag &&
                int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
            {
                _refreshIntervalMs = interval;

                try
                {
                    await _tcpServerService.StartScreenStreamAsync(_clientName, _refreshIntervalMs);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Refresh update failed: {ex.Message}";
                }
            }
        }

        private void ControlToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            ToggleRemoteControl(true);
        }

        private void ControlToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            ToggleRemoteControl(false);
        }

        private void ToggleRemoteControl(bool enable)
        {
            _isControlEnabled = enable;
            ControlToggleButton.Content = enable ? "Control Enabled" : "Enable Control";
            ControlToggleButton.Foreground = enable ? Brushes.White : Brushes.Black;
            ControlToggleButton.Background = enable ? Brushes.DarkGreen : Brushes.LightGray;

            if (enable)
            {
                ScreenImage.Focus();
                StatusText.Text = "Remote control enabled";
            }
            else
            {
                StatusText.Text = "Remote control disabled";
            }
        }

        private void RemoteControlWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0)
            {
                return;
            }

            var data = new Dictionary<string, string>
            {
                ["vk"] = vk.ToString(CultureInfo.InvariantCulture)
            };

            _ = _tcpServerService.SendRemoteInputAsync(_clientName, "key_down", data);
            e.Handled = true;
        }

        private void RemoteControlWindow_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0)
            {
                return;
            }

            var data = new Dictionary<string, string>
            {
                ["vk"] = vk.ToString(CultureInfo.InvariantCulture)
            };

            _ = _tcpServerService.SendRemoteInputAsync(_clientName, "key_up", data);
            e.Handled = true;
        }

        private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isControlEnabled || ScreenImage.ActualWidth <= 0 || ScreenImage.ActualHeight <= 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastMouseMoveSent).TotalMilliseconds < 30)
            {
                return;
            }

            _lastMouseMoveSent = now;

            var position = e.GetPosition(ScreenImage);
            var relativeX = Math.Clamp(position.X / ScreenImage.ActualWidth, 0, 1);
            var relativeY = Math.Clamp(position.Y / ScreenImage.ActualHeight, 0, 1);

            var data = new Dictionary<string, string>
            {
                ["x"] = relativeX.ToString(CultureInfo.InvariantCulture),
                ["y"] = relativeY.ToString(CultureInfo.InvariantCulture)
            };

            _ = _tcpServerService.SendRemoteInputAsync(_clientName, "mouse_move", data);
        }

        private void ScreenImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            ScreenImage.Focus();
            SendMouseButton("left", "down");
            e.Handled = true;
        }

        private void ScreenImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            SendMouseButton("left", "up");
            e.Handled = true;
        }

        private void ScreenImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            ScreenImage.Focus();
            SendMouseButton("right", "down");
            e.Handled = true;
        }

        private void ScreenImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            SendMouseButton("right", "up");
            e.Handled = true;
        }

        private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            var data = new Dictionary<string, string>
            {
                ["delta"] = e.Delta.ToString(CultureInfo.InvariantCulture)
            };

            _ = _tcpServerService.SendRemoteInputAsync(_clientName, "mouse_wheel", data);
            e.Handled = true;
        }

        private void SendMouseButton(string button, string action)
        {
            var data = new Dictionary<string, string>
            {
                ["button"] = button,
                ["action"] = action
            };

            _ = _tcpServerService.SendRemoteInputAsync(_clientName, "mouse_button", data);
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_isControlEnabled)
            {
                return;
            }

            ScreenImage_MouseWheel(ScreenImage, e);
            e.Handled = true;
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Retry button clicked for {_clientName}");

                // Get diagnostic information
                var diagnostics = _tcpServerService.GetClientDiagnostics(_clientName);
                System.Diagnostics.Debug.WriteLine(diagnostics);

                // Check if client is connected
                if (!_tcpServerService.IsClientConnected(_clientName))
                {
                    StatusText.Text = $"Error: {_clientName} is not connected";
                    MessageBox.Show(
                        $"Cannot start stream: {_clientName} is not connected to the server.\n\n" +
                        "Please ensure the client:\n" +
                        "1. Is powered on and running\n" +
                        "2. Has network connectivity\n" +
                        "3. Has LabServerClient application running\n" +
                        "4. Shows 'Connected' status\n\n" +
                        "Diagnostics:\n" + diagnostics,
                        "Client Not Connected",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                StatusText.Text = "Retrying stream request...";

                // Stop existing stream first
                await _tcpServerService.StopScreenStreamAsync(_clientName);
                await Task.Delay(500);

                // Request new stream
                await _tcpServerService.StartScreenStreamAsync(_clientName, _refreshIntervalMs);
                StatusText.Text = "Stream request sent, waiting for response...";

                // Wait for first frame
                await WaitForFirstFrameAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteControl] Retry error: {ex.Message}\nStack: {ex.StackTrace}");
                StatusText.Text = $"Retry failed: {ex.Message}";
                MessageBox.Show(
                    $"Failed to retry stream:\n\n{ex.Message}\n\nMake sure the client:\n" +
                    "1. Is connected to the server (check status in client list)\n" +
                    "2. Is logged in as a STUDENT (not admin)\n" +
                    "3. Has the SessionWindow open and visible\n" +
                    "4. Is not in locked/kiosk mode\n\n" +
                    "Check the Output window for detailed logs.",
                    "Stream Retry Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}

