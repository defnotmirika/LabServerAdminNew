using LabServerAdmin.Services;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

        public bool IsAuthenticated { get; private set; } = false;
        public string? AuthenticatedUsername { get; private set; } = null;
        public string? UserRole { get; private set; } = null;
        public string? AuthenticatedPassword { get; private set; } = null; // Store for lock screen

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

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // TEMPORARILY DISABLED: Alt+Tab blocking for testing
            // Uncomment when ready to re-enable

            /*
            // Prevent alt-tab and make window stay on top
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOPMOST);
            SetForegroundWindow(hwnd);

            // Register window message hook to intercept system keys
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);

            // Register hotkeys to block Alt+Tab, Ctrl+Alt+Del, etc.
            if (hwnd != IntPtr.Zero)
            {
                RegisterHotKey(hwnd, HOTKEY_ID, MOD_ALT, VK_TAB);
            }
            
            // Disable window animations and transitions
            DisableWindowAnimations(hwnd);
            */
        }

        /// <summary>
        /// Disables window animations and transitions for a specific window
        /// </summary>
        private void DisableWindowAnimations(IntPtr hwnd)
        {
            const int WM_CHANGEUISTATE = 0x0127;
            const int UIS_INITIALIZE = 3;

            // Disable UI state animations
            SendMessage(hwnd, WM_CHANGEUISTATE, new IntPtr(UIS_INITIALIZE), IntPtr.Zero);
        }

        private void LoginWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // TEMPORARILY DISABLED: Authentication requirement for testing
            // Uncomment when ready to re-enable

            /*
            // Prevent closing unless authenticated
            if (_requireAuthenticationToClose && !IsAuthenticated)
            {
                e.Cancel = true;
                MessageBox.Show("You must login to exit the application.", "Login Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            */

            // Unregister hotkeys only if closing is allowed
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, HOTKEY_ID);
            }
            // _source?.RemoveHook(WndProc); // Commented out - matching disabled feature above
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_TASKLIST = 0xF170; // Alt+Tab
            const int SC_CLOSE = 0xF060; // Close

            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                // Block Alt+Tab
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_SYSCOMMAND)
            {
                int command = wParam.ToInt32() & 0xFFF0;
                if (command == SC_TASKLIST || command == SC_CLOSE)
                {
                    // Block Alt+Tab and close
                    if (_requireAuthenticationToClose && !IsAuthenticated)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private void LoginWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // TEMPORARILY DISABLED: Key blocking for testing
            // Uncomment when ready to re-enable

            /*
            // Block Alt+Tab, Ctrl+Alt+Del, etc.
            if (e.Key == Key.System && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                if (e.SystemKey == Key.Tab || e.SystemKey == Key.F4)
                {
                    e.Handled = true;
                }
            }

            // Block Escape key
            if (e.Key == Key.Escape && _requireAuthenticationToClose && !IsAuthenticated)
            {
                e.Handled = true;
            }
            */
        }


        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            AttemptLogin();
        }

        private void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_databaseService == null)
            {
                MessageBox.Show("Database is not available.", "Forgot Password", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var window = new ForgotPasswordWindow(_databaseService)
            {
                Owner = this
            };

            window.ShowDialog();
        }

        private void UsernameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PasswordBox.Focus();
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AttemptLogin();
            }
        }

        private async void AttemptLogin()
        {
            string username = UsernameTextBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError("Please enter a username.");
                UsernameTextBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                ShowError("Please enter a password.");
                PasswordBox.Focus();
                return;
            }

            // Disable login button during authentication
            LoginButton.IsEnabled = false;
            ErrorTextBlock.Visibility = Visibility.Collapsed;

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
                            ShowError("Your account has been locked. Please contact your administrator.");
                            PasswordBox.Password = string.Empty;
                            PasswordBox.Focus();
                            LoginButton.IsEnabled = true;
                            return;
                        }

                        isValid = await _databaseService.ValidateInstructorAsync(username, password);
                        if (isValid)
                        {
                            role = "INSTRUCTOR";

                            // ✅ Reset failed attempts on successful login
                            await _databaseService.ResetInstructorFailedAttemptsAsync(username);

                            var isTempPassword = await _databaseService.IsInstructorTempPasswordAsync(username);
                            if (isTempPassword)
                            {
                                var firstTime = new FirstTimeLoginWindow(_databaseService, username)
                                {
                                    Owner = this
                                };

                                var result = firstTime.ShowDialog();
                                if (result == true)
                                {
                                    ShowError("Password updated. Please login with your new password.");
                                    PasswordBox.Password = string.Empty;
                                    PasswordBox.Focus();
                                    LoginButton.IsEnabled = true;
                                    return;
                                }

                                ShowError("You must set a new password to continue.");
                                PasswordBox.Password = string.Empty;
                                PasswordBox.Focus();
                                LoginButton.IsEnabled = true;
                                return;
                            }
                        }
                        else
                        {
                            // ✅ Wrong password — check if instructor exists to track attempts
                            bool instructorExists = await _databaseService.InstructorExistsAsync(username);
                            if (instructorExists)
                            {
                                int failedCount = await _databaseService.IncrementInstructorFailedAttemptsAsync(username);
                                int remaining = 4 - failedCount;

                                if (failedCount >= 4)
                                {
                                    await _databaseService.LockInstructorAccountAsync(username);
                                    await _databaseService.LogSystemActionAsync(
                                        "Account Locked", username, "Warning",
                                        $"Instructor account locked after 4 failed login attempts");

                                    ShowError("Your account has been locked. Please contact your administrator.");
                                }
                                else
                                {
                                    ShowError($"Invalid username or password. {remaining} attempt(s) remaining before your account is locked.");
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
                    // Fallback to hardcoded credentials if database is not available
                    isValid = (username == "admin" && password == "admin123");
                    role = isValid ? "ADMIN" : string.Empty;
                }

                if (isValid)
                {
                    IsAuthenticated = true;
                    AuthenticatedUsername = username;
                    UserRole = string.IsNullOrWhiteSpace(role) ? "ADMIN" : role;
                    AuthenticatedPassword = password;
                    ErrorTextBlock.Visibility = Visibility.Collapsed;

                    // Log successful login attempt (fire and forget)
                    if (_databaseService != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _databaseService.LogSystemActionAsync(
                                    "Login",
                                    username,
                                    "Success",
                                    $"User ({UserRole}) logged in successfully"
                                );
                            }
                            catch { }
                        });
                    }

                    this.DialogResult = true;
                    this.Close();
                }
                else
                {
                    // Generic error for unknown username or failed admin
                    if (_databaseService != null)
                    {
                        try
                        {
                            await _databaseService.LogSystemActionAsync(
                                "Login Failed",
                                username,
                                "Failed",
                                "Invalid credentials provided"
                            );
                        }
                        catch { }
                    }

                    ShowError("Invalid username or password. Please try again.");
                    PasswordBox.Password = "";
                    PasswordBox.Focus();
                }
            }
            catch (Exception ex)
            {
                ShowError($"Authentication error: {ex.Message}");
                PasswordBox.Password = "";
                PasswordBox.Focus();
            }
            finally
            {
                LoginButton.IsEnabled = true;
            }
        }

        private void ShowError(string message)
        {
            ErrorTextBlock.Text = message;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
    }
}

