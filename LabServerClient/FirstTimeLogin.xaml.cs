using System.Windows;
using LabServerClient.Services;

namespace LabServerClient
{
    public partial class FirstTimeLoginWindow : Window
    {
        private readonly DatabaseService _databaseService;
        private readonly string _studNo;

        public FirstTimeLoginWindow(DatabaseService databaseService, string studNo)
        {
            _databaseService = databaseService;
            _studNo = studNo;
            InitializeComponent();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            var password = NewPasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;
            ErrorText.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            {
                ErrorText.Text = "Password must be at least 6 characters.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (password != confirmPassword)
            {
                ErrorText.Text = "Passwords do not match.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            var updated = await _databaseService.ResetStudentPasswordAsync(_studNo, password);
            if (updated)
            {
                MessageBox.Show(
                    "Password set successfully. Please login again with your new password.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
                return;
            }

            ErrorText.Text = "Failed to update password. Try again later.";
            ErrorText.Visibility = Visibility.Visible;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
