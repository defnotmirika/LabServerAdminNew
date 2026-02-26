using System;
using System.Windows;
using Microsoft.Extensions.Configuration;

namespace LabServerAdmin
{
    public partial class SessionSettingsDialog : Window
    {
        private int _originalTimeoutMinutes;
        private int _originalWarningMinutes;
        public bool WasSaved { get; set; }

        public int TimeoutMinutes { get; set; }
        public int WarningMinutes { get; set; }

        public SessionSettingsDialog(int currentTimeoutMinutes = 15, int currentWarningMinutes = 1)
        {
            InitializeComponent();
            
            _originalTimeoutMinutes = currentTimeoutMinutes;
            _originalWarningMinutes = currentWarningMinutes;
            
            TimeoutMinutes = currentTimeoutMinutes;
            WarningMinutes = currentWarningMinutes;
            
            AdminTimeoutMinutesTextBox.Text = currentTimeoutMinutes.ToString();
            AdminWarningMinutesTextBox.Text = currentWarningMinutes.ToString();
            
            WasSaved = false;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs())
            {
                return;
            }

            TimeoutMinutes = int.Parse(AdminTimeoutMinutesTextBox.Text);
            WarningMinutes = int.Parse(AdminWarningMinutesTextBox.Text);

            // Validation
            if (TimeoutMinutes < 5 || TimeoutMinutes > 120)
            {
                MessageBox.Show(
                    "Timeout must be between 5 and 120 minutes.",
                    "Invalid Input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (WarningMinutes < 1 || WarningMinutes > 10)
            {
                MessageBox.Show(
                    "Warning must be between 1 and 10 minutes.",
                    "Invalid Input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (WarningMinutes >= TimeoutMinutes)
            {
                MessageBox.Show(
                    $"Warning time ({WarningMinutes} min) must be less than timeout ({TimeoutMinutes} min).",
                    "Invalid Configuration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Show confirmation
            var result = MessageBox.Show(
                $"Apply new session lock settings?\n\n" +
                $"Lock after: {TimeoutMinutes} minutes\n" +
                $"Warning: {WarningMinutes} minute(s) before lock\n\n" +
                $"These settings will take effect immediately.",
                "Confirm Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                WasSaved = true;
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            WasSaved = false;
            DialogResult = false;
            Close();
        }

        private void HighSecurityButton_Click(object sender, RoutedEventArgs e)
        {
            AdminTimeoutMinutesTextBox.Text = "5";
            AdminWarningMinutesTextBox.Text = "1";
        }

        private void NormalButton_Click(object sender, RoutedEventArgs e)
        {
            AdminTimeoutMinutesTextBox.Text = "15";
            AdminWarningMinutesTextBox.Text = "1";
        }

        private void RelaxedButton_Click(object sender, RoutedEventArgs e)
        {
            AdminTimeoutMinutesTextBox.Text = "30";
            AdminWarningMinutesTextBox.Text = "2";
        }

        private bool ValidateInputs()
        {
            if (!int.TryParse(AdminTimeoutMinutesTextBox.Text, out _))
            {
                MessageBox.Show(
                    "Timeout must be a valid number.",
                    "Invalid Input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                AdminTimeoutMinutesTextBox.Focus();
                return false;
            }

            if (!int.TryParse(AdminWarningMinutesTextBox.Text, out _))
            {
                MessageBox.Show(
                    "Warning must be a valid number.",
                    "Invalid Input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                AdminWarningMinutesTextBox.Focus();
                return false;
            }

            return true;
        }
    }
}
