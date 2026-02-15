using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LabServerClient
{
    public partial class LockpcWindow : Window
    {
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

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_TAB = 0x09;
        private const int HOTKEY_ID = 9001;

        private HwndSource? _source;

        public LockpcWindow(string? customMessage = null)
        {
            InitializeComponent();
            Loaded += LockpcWindow_Loaded;
            Closing += LockpcWindow_Closing;
            KeyDown += LockpcWindow_KeyDown;
            
            // Ensure window is visible
            Visibility = Visibility.Visible;
            ShowActivated = true;

            // Set custom message if provided
            if (!string.IsNullOrWhiteSpace(customMessage))
            {
                SetCustomMessage(customMessage);
            }
        }

        /// <summary>
        /// Sets a custom message to display on the lock screen
        /// </summary>
        private void SetCustomMessage(string message)
        {
            // Parse message for title and body (split by first newline)
            var lines = message.Split(new[] { '\n' }, 2);
            
            if (lines.Length >= 1)
            {
                TitleTextBlock.Text = lines[0].Trim();
            }
            
            if (lines.Length >= 2)
            {
                MessageTextBlock.Text = lines[1].Trim();
                SubMessageTextBlock.Visibility = Visibility.Collapsed; // Hide default sub-message
            }
        }

        private void LockpcWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Make window stay on top
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOPMOST);
            SetForegroundWindow(hwnd);

            // Register window message hook to intercept system keys
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);

            // Register hotkeys to block Alt+Tab
            if (hwnd != IntPtr.Zero)
            {
                RegisterHotKey(hwnd, HOTKEY_ID, MOD_ALT, VK_TAB);
            }
        }

        private void LockpcWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Unregister hotkeys
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
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            return IntPtr.Zero;
        }

        private void LockpcWindow_KeyDown(object sender, KeyEventArgs e)
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
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
            }
        }
    }
}

