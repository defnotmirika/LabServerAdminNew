using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LabServerClient.Services;
using LabServerClient.ViewModels;
using Microsoft.Win32;

namespace LabServerClient
{
    public partial class LoginWindow : Window
    {
        private readonly DatabaseService? _databaseService;
        private readonly bool _requireAuthenticationToClose;
        private readonly LoginViewModel _viewModel;

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern bool BlockInput(bool fBlockIt);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_TAB = 0x09;
        private const uint VK_ESCAPE = 0x1B;
        private const int HOTKEY_ID = 9000;
        private const int MAX_ATTEMPTS = 4;

        private HwndSource? _source;
        public bool IsAuthenticated { get; private set; } = false;
        public string? AuthenticatedUsername { get; private set; } = null;
        public string? UserRole { get; private set; } = null;
        public int? AuthenticatedClientId { get; private set; } = null;

        public LoginWindow(DatabaseService? databaseService = null, bool requireAuthenticationToClose = true)
        {
            _databaseService = databaseService;
            _requireAuthenticationToClose = requireAuthenticationToClose;

            _viewModel = new LoginViewModel(databaseService);

            InitializeComponent();

            this.DataContext = _viewModel;

            _viewModel.LoginSuccess += ViewModel_LoginSuccess;
            _viewModel.PcMismatchDetected += ViewModel_PcMismatchDetected;
            _viewModel.LoginCancelled += ViewModel_LoginCancelled;

            Loaded += LoginWindow_Loaded;
            Closing += LoginWindow_Closing;
            KeyDown += LoginWindow_KeyDown;
            UsernameTextBox.Focus();
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PcNameTextBlock.Text = _viewModel.CurrentPcName;

            var hwnd = new WindowInteropHelper(this).Handle;

            this.WindowState = WindowState.Normal;
            this.Top = 0;
            this.Left = 0;
            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;

            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOPMOST);
            SetForegroundWindow(hwnd);

            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);

            if (hwnd != IntPtr.Zero)
                RegisterHotKey(hwnd, HOTKEY_ID, MOD_ALT, VK_TAB);
        }

        private void LoginWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_requireAuthenticationToClose && !IsAuthenticated)
            {
                e.Cancel = true;
                MessageBox.Show("You must login to exit the application.", "Login Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                UnregisterHotKey(hwnd, HOTKEY_ID);
            _source?.RemoveHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            const int WM_SYSCOMMAND = 0x0112;
            const int WM_SYSKEYDOWN = 0x0104;
            const int WM_SYSKEYUP = 0x0105;
            const int SC_TASKLIST = 0xF170;
            const int SC_CLOSE = 0xF060;

            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_SYSCOMMAND)
            {
                int command = wParam.ToInt32() & 0xFFF0;
                if (command == SC_TASKLIST || command == SC_CLOSE)
                {
                    if (_requireAuthenticationToClose && !IsAuthenticated)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
            }

            if (msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
            {
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private void LoginWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.System && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                if (e.SystemKey == Key.Tab || e.SystemKey == Key.F4)
                    e.Handled = true;
            }

            if (e.Key == Key.Escape && _requireAuthenticationToClose && !IsAuthenticated)
                e.Handled = true;

            if (e.Key == Key.LWin || e.Key == Key.RWin)
                e.Handled = true;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
                e.Handled = true;
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e) => AttemptLogin();

        private void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_databaseService == null)
            {
                MessageBox.Show("Database is not available.", "Forgot Password", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new ForgotPasswordWindow(_databaseService) { Owner = this };
            window.ShowDialog();
        }

        private void UsernameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) PasswordBox.Focus();
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) AttemptLogin();
        }

        private async void AttemptLogin()
        {
            _viewModel.Password = PasswordBox.Password ?? string.Empty;
            await _viewModel.AttemptLoginAsync();
        }

        private void ViewModel_LoginSuccess()
        {
            IsAuthenticated = _viewModel.IsAuthenticated;
            AuthenticatedUsername = _viewModel.AuthenticatedUsername;
            UserRole = _viewModel.UserRole;
            AuthenticatedClientId = _viewModel.AuthenticatedClientId;

            HideError();
            this.DialogResult = true;
            this.Close();
        }

        private async void ViewModel_PcMismatchDetected(string username, string assignedPcName)
        {
            var mismatchDialog = new PCMismatchDialog(assignedPcName, _viewModel.CurrentPcName);
            bool? dialogResult = mismatchDialog.ShowDialog();

            if (dialogResult == true && mismatchDialog.UserSelectedYes)
            {
                ShowSimpleError("Sending request to administrator...");
                try
                {
                    bool requestSent = await _viewModel.SendLoginRequestAsync();
                    ShowSimpleError(requestSent
                        ? "Request sent to administrator. Please wait for approval."
                        : "Failed to send request. Please try again later.");
                }
                catch (Exception ex)
                {
                    ShowSimpleError($"Error sending request: {ex.Message}");
                }
            }
            else
            {
                ShowSimpleError("Login cancelled. Please try again.");
            }

            PasswordBox.Password = "";
            PasswordBox.Focus();
        }

        private void ViewModel_LoginCancelled()
        {
            if (_viewModel.IsAccountLocked)
            {
                ShowLockedError();
            }
            else if (_viewModel.FailedAttemptCount > 0)
            {
                int remaining = MAX_ATTEMPTS - _viewModel.FailedAttemptCount;
                ShowAttemptError(_viewModel.FailedAttemptCount, remaining);
            }
            else
            {
                ShowSimpleError(_viewModel.ErrorMessage ?? string.Empty);
            }

            PasswordBox.Password = "";
            PasswordBox.Focus();
        }

        // ── UI Helper Methods ─────────────────────────────────────────────────

        private void ShowSimpleError(string message)
        {
            ErrorIconText.Text = "⚠";
            ErrorTextBlock.Text = message;
            AttemptPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void ShowAttemptError(int failedCount, int remaining)
        {
            ErrorIconText.Text = "⚠";
            ErrorTextBlock.Text = remaining == 1
                ? $"Incorrect password. You have {remaining} attempt left before your account is locked."
                : $"Incorrect password. You have {remaining} attempts remaining before your account is locked.";

            AttemptCountText.Text = $"{failedCount} / {MAX_ATTEMPTS}";

            var activeColor = failedCount switch
            {
                1 => new SolidColorBrush(Color.FromRgb(230, 126, 34)),
                2 => new SolidColorBrush(Color.FromRgb(211, 84, 0)),
                _ => new SolidColorBrush(Color.FromRgb(192, 57, 43))
            };
            var inactiveColor = new SolidColorBrush(Color.FromRgb(234, 234, 234));

            Block1.Background = failedCount >= 1 ? activeColor : inactiveColor;
            Block2.Background = failedCount >= 2 ? activeColor : inactiveColor;
            Block3.Background = failedCount >= 3 ? activeColor : inactiveColor;
            Block4.Background = failedCount >= 4 ? activeColor : inactiveColor;

            AttemptPanel.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void ShowLockedError()
        {
            ErrorIconText.Text = "🔒";
            ErrorTextBlock.Text = "Your account has been locked due to too many failed attempts. Please contact your administrator to unlock your account.";
            AttemptPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            AttemptPanel.Visibility = Visibility.Collapsed;
        }
    }
}