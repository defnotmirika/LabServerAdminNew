using LabServerAdmin.Services;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Configuration;

namespace LabServerAdmin
{
    public partial class LoginWindow : Window
    {
        private readonly DatabaseService? _databaseService;
        private readonly bool _requireAuthenticationToClose;

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
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_TAB = 0x09;
        private const uint VK_ESCAPE = 0x1B;
        private const int HOTKEY_ID = 9000;
        private const int MAX_ATTEMPTS = 4;

        public bool IsAuthenticated { get; private set; } = false;
        public string? AuthenticatedUsername { get; private set; } = null;
        public string? UserRole { get; private set; } = null;
        public string? AuthenticatedPassword { get; private set; } = null;

        public LoginWindow(
            DatabaseService? databaseService = null,
            VoiceSpeakerService? voiceSpeakerService = null,
            IConfiguration? configuration = null,
            bool requireAuthenticationToClose = true)
        {
            _databaseService = databaseService;
            _requireAuthenticationToClose = requireAuthenticationToClose;
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
            Closing += LoginWindow_Closing;
            KeyDown += LoginWindow_KeyDown;
            UsernameTextBox.Focus();
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e) { }

        private void DisableWindowAnimations(IntPtr hwnd)
        {
            const int WM_CHANGEUISTATE = 0x0127;
            const int UIS_INITIALIZE = 3;
            SendMessage(hwnd, WM_CHANGEUISTATE, new IntPtr(UIS_INITIALIZE), IntPtr.Zero);
        }

        private void LoginWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                UnregisterHotKey(hwnd, HOTKEY_ID);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            const int WM_SYSCOMMAND = 0x0112;
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

            return IntPtr.Zero;
        }

        private void LoginWindow_KeyDown(object sender, KeyEventArgs e) { }

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
            string username = UsernameTextBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowSimpleError("Please enter your username.");
                UsernameTextBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                ShowSimpleError("Please enter your password.");
                PasswordBox.Focus();
                return;
            }

            LoginButton.IsEnabled = false;
            HideError();

            try
            {
                bool isValid = false;
                string role = "";

                if (_databaseService != null)
                {
                    // Try admin first (no lockout for admin)
                    isValid = await _databaseService.ValidateAdminAsync(username, password);
                    if (isValid)
                    {
                        role = "ADMIN";
                    }
                    else
                    {
                        // ✅ Check lockout BEFORE validating password
                        bool isLocked = await _databaseService.IsInstructorLockedAsync(username);
                        if (isLocked)
                        {
                            ShowLockedError();
                            PasswordBox.Password = string.Empty;
                            PasswordBox.Focus();
                            LoginButton.IsEnabled = true;
                            return;
                        }

                        isValid = await _databaseService.ValidateInstructorAsync(username, password);
                        if (isValid)
                        {
                            role = "INSTRUCTOR";
                            await _databaseService.ResetInstructorFailedAttemptsAsync(username);

                            var isTempPassword = await _databaseService.IsInstructorTempPasswordAsync(username);
                            if (isTempPassword)
                            {
                                var firstTime = new FirstTimeLoginWindow(_databaseService, username) { Owner = this };
                                var result = firstTime.ShowDialog();
                                if (result == true)
                                {
                                    ShowSimpleError("Password updated successfully. Please log in again with your new password.");
                                    PasswordBox.Password = string.Empty;
                                    PasswordBox.Focus();
                                    LoginButton.IsEnabled = true;
                                    return;
                                }

                                ShowSimpleError("You must set a new password before continuing.");
                                PasswordBox.Password = string.Empty;
                                PasswordBox.Focus();
                                LoginButton.IsEnabled = true;
                                return;
                            }
                        }
                        else
                        {
                            bool instructorExists = await _databaseService.InstructorExistsAsync(username);
                            if (instructorExists)
                            {
                                int failedCount = await _databaseService.IncrementInstructorFailedAttemptsAsync(username);
                                int remaining = MAX_ATTEMPTS - failedCount;

                                if (failedCount >= MAX_ATTEMPTS)
                                {
                                    await _databaseService.LockInstructorAccountAsync(username);
                                    await _databaseService.LogSystemActionAsync(
                                        "Account Locked", username, "Warning",
                                        $"Instructor account locked after {MAX_ATTEMPTS} failed login attempts");

                                    ShowLockedError();
                                }
                                else
                                {
                                    // ✅ Show attempt progress bar
                                    ShowAttemptError(failedCount, remaining);
                                }

                                PasswordBox.Password = "";
                                PasswordBox.Focus();
                                LoginButton.IsEnabled = true;
                                return;
                            }
                        }
                    }
                }
                else
                {
                    isValid = (username == "admin" && password == "admin123");
                    role = isValid ? "ADMIN" : string.Empty;
                }

                if (isValid)
                {
                    IsAuthenticated = true;
                    AuthenticatedUsername = username;
                    UserRole = string.IsNullOrWhiteSpace(role) ? "ADMIN" : role;
                    AuthenticatedPassword = password;
                    HideError();

                    if (_databaseService != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _databaseService.LogSystemActionAsync(
                                    "Login", username, "Success",
                                    $"User ({UserRole}) logged in successfully");
                            }
                            catch { }
                        });
                    }

                    this.DialogResult = true;
                    this.Close();
                }
                else
                {
                    if (_databaseService != null)
                    {
                        try
                        {
                            await _databaseService.LogSystemActionAsync(
                                "Login Failed", username, "Failed",
                                "Invalid credentials provided");
                        }
                        catch { }
                    }

                    ShowSimpleError("Invalid username or password. Please try again.");
                    PasswordBox.Password = "";
                    PasswordBox.Focus();
                }
            }
            catch (Exception ex)
            {
                ShowSimpleError($"An error occurred during login. Please try again.\n({ex.Message})");
                PasswordBox.Password = "";
                PasswordBox.Focus();
            }
            finally
            {
                LoginButton.IsEnabled = true;
            }
        }

        // ── UI Helper Methods ─────────────────────────────────────────────────

        /// <summary>Simple error with no progress bar (invalid input, generic errors)</summary>
        private void ShowSimpleError(string message)
        {
            ErrorIconText.Text = "⚠";
            ErrorTextBlock.Text = message;
            AttemptPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        /// <summary>Shows attempt warning with block indicators</summary>
        private void ShowAttemptError(int failedCount, int remaining)
        {
            ErrorIconText.Text = "⚠";
            ErrorTextBlock.Text = remaining == 1
                ? $"Incorrect password. You have {remaining} attempt left before your account is locked."
                : $"Incorrect password. You have {remaining} attempts remaining before your account is locked.";

            AttemptCountText.Text = $"{failedCount} / {MAX_ATTEMPTS}";

            // Block colors — orange → red as attempts increase
            var activeColor = failedCount switch
            {
                1 => new SolidColorBrush(Color.FromRgb(230, 126, 34)),  // Orange
                2 => new SolidColorBrush(Color.FromRgb(211, 84, 0)),    // Dark orange
                3 => new SolidColorBrush(Color.FromRgb(192, 57, 43)),   // Red
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

        /// <summary>Shows locked account error (no progress bar needed)</summary>
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