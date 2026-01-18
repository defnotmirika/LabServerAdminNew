using System.Windows;
using LabServerClient.Services;

namespace LabServerClient
{
    /// <summary>
    /// Dialog window for handling PC mismatch scenarios.
    /// Allows users to request access from their current PC when it doesn't match the assigned PC.
    /// </summary>
    public partial class PCMismatchDialog : Window
    {
        private readonly LoginRequestService _loginRequestService;
        public bool UserSelectedYes { get; private set; } = false;

        public PCMismatchDialog(string assignedPcName, string currentPcName)
        {
            InitializeComponent();
            _loginRequestService = new LoginRequestService();

            // Display PC names
            AssignedPcNameTextBlock.Text = assignedPcName;
            CurrentPcNameTextBlock.Text = currentPcName;
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            // User wants to send request to admin
            UserSelectedYes = true;
            this.DialogResult = true;
            this.Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            // User wants to cancel login
            UserSelectedYes = false;
            this.DialogResult = false;
            this.Close();
        }
    }
}
