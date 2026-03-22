using System.Windows;
using System.Windows.Controls;
using LabServerClient.Services;

namespace LabServerClient
{
    public partial class ForgotPasswordWindow : Window
    {
        private readonly DatabaseService _databaseService;

        public ForgotPasswordWindow(DatabaseService databaseService)
        {
            _databaseService = databaseService;
            InitializeComponent();
            Loaded += (_, _) => UsernameTextBox.Focus();
        }

        private async void SendFeedbackButton_Click(object sender, RoutedEventArgs e)
        {
            var username = UsernameTextBox.Text.Trim();
            var feedbackTextBox = FindName("FeedbackTextBox") as TextBox;
            var feedback = feedbackTextBox?.Text.Trim() ?? string.Empty;

            ErrorTextBlock.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError("Please enter your username.");
                return;
            }

            if (string.IsNullOrWhiteSpace(feedback))
            {
                ShowError("Please enter your reason or feedback.");
                return;
            }

            var submitted = await _databaseService.SubmitForgotPasswordFeedbackAsync(username, feedback, "Client");
            if (submitted)
            {
                MessageBox.Show(
                    "Your request has been sent.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
                return;
            }

            ShowError("Failed to send feedback. Please try again later.");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorTextBlock.Text = message;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
    }
}
