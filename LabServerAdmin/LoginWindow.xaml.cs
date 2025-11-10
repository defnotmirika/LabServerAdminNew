using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LabServerAdmin.Services;

namespace LabServerAdmin
{
    public partial class LoginWindow : Window
    {
        private readonly DatabaseService? _databaseService;

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

        private HwndSource? _source;
        public bool IsAuthenticated { get; private set; } = false;

        public LoginWindow(DatabaseService? databaseService = null)
        {
            _databaseService = databaseService;
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
            Closing += LoginWindow_Closing;
            KeyDown += LoginWindow_KeyDown;
            UsernameTextBox.Focus();
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
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
        }

        private void LoginWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Prevent closing unless authenticated
            if (!IsAuthenticated)
            {
                e.Cancel = true;
                MessageBox.Show("You must login to exit the application.", "Login Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Unregister hotkeys only if closing is allowed
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, HOTKEY_ID);
            }
            _source?.RemoveHook(WndProc);
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
                    if (!IsAuthenticated)
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
            // Block Alt+Tab, Ctrl+Alt+Del, etc.
            if (e.Key == Key.System && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                if (e.SystemKey == Key.Tab || e.SystemKey == Key.F4)
                {
                    e.Handled = true;
                }
            }

            // Block Escape key
            if (e.Key == Key.Escape && !IsAuthenticated)
            {
                e.Handled = true;
            }
        }


        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            AttemptLogin();
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

                // Validate credentials against database if available
                if (_databaseService != null)
                {
                    isValid = await _databaseService.ValidateAdminAsync(username, password);
                }
                else
                {
                    // Fallback to hardcoded credentials if database is not available
                    isValid = (username == "admin" && password == "admin123");
                }

                if (isValid)
                {
                    IsAuthenticated = true;
                    ErrorTextBlock.Visibility = Visibility.Collapsed;
                    
                    // Log successful login attempt
                    if (_databaseService != null)
                    {
                        try
                        {
                            await _databaseService.LogSystemActionAsync(
                                "Login",
                                username,
                                "Success",
                                "User logged in successfully"
                            );
                        }
                        catch
                        {
                            // Ignore logging errors
                        }
                    }
                    
                    // Set DialogResult and close window
                    this.DialogResult = true;
                    this.Close();
                }
                else
                {
                    // Log failed login attempt
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
                        catch
                        {
                            // Ignore logging errors
                        }
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

