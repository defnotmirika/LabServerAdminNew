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
                StatusText.Text = "Requesting stream...";
                await _tcpServerService.StartScreenStreamAsync(_clientName, _refreshIntervalMs);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Stream error: {ex.Message}";
            }
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
            if (!string.Equals(e.ClientName, _clientName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var metadata = e.Metadata;
                if (metadata.TryGetValue("width", out var widthStr) && double.TryParse(widthStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                {
                    _remoteScreenWidth = width;
                }

                if (metadata.TryGetValue("height", out var heightStr) && double.TryParse(heightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                {
                    _remoteScreenHeight = height;
                }

                using var ms = new MemoryStream(e.ImageBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                Dispatcher.Invoke(() =>
                {
                    ScreenImage.Source = bitmap;
                    StatusText.Text = $"Last frame: {DateTime.Now:T}";
                });
            }
            catch
            {
                // Ignore decoding errors
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
    }
}

