using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LabServerClient.Services;
using LabServerClient.ViewModels;
using Microsoft.Win32;

namespace LabServerClient
{
    /// <summary>
    /// LoginWindow - Main login interface with PC Name validation.
    /// 
    /// Login Flow:
    /// 1. Window loads and displays current PC Name from registry
    /// 2. User enters username and password
    /// 3. On login click, LoginViewModel validates credentials and PC Name
    /// 4. If successful and PC matches ? set DialogResult = true and close
    /// 5. If credentials valid but PC doesn't match ? show PCMismatchDialog
    ///    - If user clicks "Yes" ? send access request to admin
    ///    - If user clicks "No" ? cancel login attempt
    /// 6. If credentials invalid ? show error message
    /// </summary>
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

        private HwndSource? _source;
        public bool IsAuthenticated { get; private set; } = false;
        public string? AuthenticatedUsername { get; private set; } = null;
        public string? UserRole { get; private set; } = null;
        public int? AuthenticatedClientId { get; private set; } = null;

        public LoginWindow(DatabaseService? databaseService = null, bool requireAuthenticationToClose = true)
        {
            _databaseService = databaseService;
            _requireAuthenticationToClose = requireAuthenticationToClose;

            // Initialize ViewModel - separates business logic from UI
            _viewModel = new LoginViewModel(databaseService);

            InitializeComponent();

            // Set DataContext for MVVM binding
            this.DataContext = _viewModel;

            // Wire up ViewModel events
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
            // Display current PC Name from registry
            PcNameTextBlock.Text = _viewModel.CurrentPcName;

            // Enable kiosk mode - fullscreen with no taskbar
            var hwnd = new WindowInteropHelper(this).Handle;

            // Make window fullscreen (borderless)
            this.WindowState = WindowState.Normal;
            this.Top = 0;
            this.Left = 0;
            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;

            // Set window to topmost and prevent interactions with other windows
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
            System.Diagnostics.Debug.WriteLine($"[LOGIN] LoginWindow_Closing - _requireAuthenticationToClose: {_requireAuthenticationToClose}, IsAuthenticated: {IsAuthenticated}");

            // Prevent closing unless authenticated
            if (_requireAuthenticationToClose && !IsAuthenticated)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Canceling close - not authenticated");
                e.Cancel = true;
                MessageBox.Show("You must login to exit the application.", "Login Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[LOGIN] Allowing close");

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
            const int WM_SYSKEYDOWN = 0x0104;
            const int WM_SYSKEYUP = 0x0105;
            const int SC_TASKLIST = 0xF170; // Alt+Tab
            const int SC_CLOSE = 0xF060; // Alt+F4

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
                    // Block Alt+Tab and Alt+F4
                    if (_requireAuthenticationToClose && !IsAuthenticated)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
            }

            // Block Alt key combinations
            if (msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
            {
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private void LoginWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Block Alt+Tab, Ctrl+Alt+Del, Alt+F4, etc.
            if (e.Key == Key.System && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                if (e.SystemKey == Key.Tab || e.SystemKey == Key.F4)
                {
                    e.Handled = true;
                }
            }

            // Block Escape key to prevent closing
            if (e.Key == Key.Escape && _requireAuthenticationToClose && !IsAuthenticated)
            {
                e.Handled = true;
            }

            // Block Windows key (Super key)
            if (e.Key == Key.LWin || e.Key == Key.RWin)
            {
                e.Handled = true;
            }

            // Block Ctrl+Alt combinations
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                e.Handled = true;
            }
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

        /// <summary>
        /// Initiates the login process by calling the ViewModel.
        /// The ViewModel handles all business logic and will invoke appropriate events.
        /// </summary>
        private async void AttemptLogin()
        {
            // Manually bind PasswordBox to ViewModel (PasswordBox doesn't support binding for security)
            System.Diagnostics.Debug.WriteLine($"[LOGIN] AttemptLogin called");
            System.Diagnostics.Debug.WriteLine($"[LOGIN] Username from UI: '{UsernameTextBox.Text}'");
            System.Diagnostics.Debug.WriteLine($"[LOGIN] Password length: {PasswordBox.Password?.Length ?? 0}");

            _viewModel.Password = PasswordBox.Password ?? string.Empty;
            System.Diagnostics.Debug.WriteLine($"[LOGIN] ViewModel.Password set to: '{_viewModel.Password}'");
            System.Diagnostics.Debug.WriteLine($"[LOGIN] ViewModel.Username: '{_viewModel.Username}'");

            await _viewModel.AttemptLoginAsync();
        }

        /// <summary>
        /// Event handler for successful login from ViewModel.
        /// Completes the login process and closes the dialog.
        /// </summary>
        private void ViewModel_LoginSuccess()
        {
            System.Diagnostics.Debug.WriteLine($"[LOGIN] ViewModel_LoginSuccess called");

            // Set authenticated properties from ViewModel
            IsAuthenticated = _viewModel.IsAuthenticated;
            AuthenticatedUsername = _viewModel.AuthenticatedUsername;
            UserRole = _viewModel.UserRole;
            AuthenticatedClientId = _viewModel.AuthenticatedClientId;

            System.Diagnostics.Debug.WriteLine($"[LOGIN] Window IsAuthenticated set to: {IsAuthenticated}");

            ErrorTextBlock.Visibility = Visibility.Collapsed;

            // Set DialogResult and close window
            this.DialogResult = true;
            System.Diagnostics.Debug.WriteLine($"[LOGIN] DialogResult set to true, closing window");
            this.Close();
        }

        /// <summary>
        /// Event handler for PC mismatch detection from ViewModel.
        /// Shows dialog asking user if they want to request access from this PC.
        /// </summary>
        private async void ViewModel_PcMismatchDetected(string username, string assignedPcName)
        {
            // Show PC mismatch dialog
            var mismatchDialog = new PCMismatchDialog(assignedPcName, _viewModel.CurrentPcName);
            bool? dialogResult = mismatchDialog.ShowDialog();

            if (dialogResult == true && mismatchDialog.UserSelectedYes)
            {
                // User selected "Yes" - send request to admin
                ShowError("Sending request to administrator...");

                try
                {
                    bool requestSent = await _viewModel.SendLoginRequestAsync();
                    if (requestSent)
                    {
                        ShowError("Request sent to administrator. Please wait for approval.");
                    }
                    else
                    {
                        ShowError("Failed to send request. Please try again later.");
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"Error sending request: {ex.Message}");
                }
            }
            else
            {
                // User selected "No" - cancel login attempt
                ShowError("Login cancelled.");
            }

            // Clear password and reset focus
            PasswordBox.Password = "";
            PasswordBox.Focus();
        }

        /// <summary>
        /// Event handler for login cancellation from ViewModel.
        /// </summary>
        private void ViewModel_LoginCancelled()
        {
            // ViewModel already set the error message
            // Just make sure UI is in correct state
            // Only clear password if this is truly a cancellation (not just validation failure)
            // Don't clear password - let user retry without retyping
            System.Diagnostics.Debug.WriteLine($"[LOGIN] Login cancelled. Error message: {_viewModel.ErrorMessage}");
            MessageBox.Show(_viewModel.ErrorMessage, "Login Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            PasswordBox.Focus();
        }

        private void ShowError(string message)
        {
            ErrorTextBlock.Text = message;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
    }
}

