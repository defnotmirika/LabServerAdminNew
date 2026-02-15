using System.Windows;

namespace LabServerClient
{
    /// <summary>
    /// Dialog window for handling logout requests during active lab sessions.
    /// Allows students to request logout approval from instructor when they're in an active schedule.
    /// </summary>
    public partial class LogoutRequestDialog : Window
    {
        public bool UserConfirmed { get; private set; } = false;

        public LogoutRequestDialog()
        {
            InitializeComponent();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            // User wants to send logout request
            UserConfirmed = true;
            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // User wants to cancel and stay logged in
            UserConfirmed = false;
            this.DialogResult = false;
            this.Close();
        }
    }
}
