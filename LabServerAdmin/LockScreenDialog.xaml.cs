using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LabServerAdmin
{
    public partial class LockScreenDialog : Window
    {
        private readonly string _username;
        private readonly string _password;
        private readonly DispatcherTimer _clockTimer;
        private DateTime _lockTime;

        public bool WasUnlocked { get; private set; }
        public bool WasLogoutRequested { get; private set; }

        public LockScreenDialog(string username, string password)
        {
            InitializeComponent();
            WindowState = WindowState.Maximized;
            
            _username = username;
            _password = password;
            _lockTime = DateTime.Now;
            
            // Set username display
            UsernameTextBlock.Text = $"Instructor: {username}";
            LockTimeTextBlock.Text = $"Locked at: {_lockTime:HH:mm:ss}";
            
            // Setup clock timer
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();
            
            // Update clock immediately
            UpdateClock();
            
            // Focus password box
            PasswordBox.Focus();
            
            // Prevent closing with Alt+F4
            this.Closing += (s, e) =>
            {
                if (!WasUnlocked && !WasLogoutRequested)
                {
                    e.Cancel = true;
                    ErrorTextBlock.Text = "?? Please enter password to unlock or choose Logout";
                    ErrorTextBlock.Visibility = Visibility.Visible;
                }
            };
        }

        private void ClockTimer_Tick(object? sender, EventArgs e)
        {
            UpdateClock();
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;
            ClockTextBlock.Text = now.ToString("HH:mm:ss");
            DateTextBlock.Text = now.ToString("dddd, MMMM d, yyyy");
        }

        private void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            AttemptUnlock();
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AttemptUnlock();
            }
            else if (e.Key == Key.Escape)
            {
                PasswordBox.Clear();
                ErrorTextBlock.Visibility = Visibility.Collapsed;
            }
        }

        private void AttemptUnlock()
        {
            if (string.IsNullOrWhiteSpace(PasswordBox.Password))
            {
                ErrorTextBlock.Text = "?? Please enter your password";
                ErrorTextBlock.Visibility = Visibility.Visible;
                return;
            }

            // Verify password
            if (PasswordBox.Password == _password)
            {
                // Success
                WasUnlocked = true;
                _clockTimer.Stop();
                DialogResult = true;
                Close();
            }
            else
            {
                // Failed
                ErrorTextBlock.Text = "? Incorrect password. Please try again.";
                ErrorTextBlock.Visibility = Visibility.Visible;
                PasswordBox.Clear();
                PasswordBox.Focus();
                
                // Shake animation effect (optional)
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 10,
                    Duration = TimeSpan.FromMilliseconds(50),
                    AutoReverse = true,
                    RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3)
                };
                
                var transform = new System.Windows.Media.TranslateTransform();
                ErrorTextBlock.RenderTransform = transform;
                transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
            }
        }

        private void LogoutLink_Click(object sender, MouseButtonEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to logout?\n\nYour session will be ended and you'll need to login again.",
                "Confirm Logout",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                WasLogoutRequested = true;
                _clockTimer.Stop();
                DialogResult = false;
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
