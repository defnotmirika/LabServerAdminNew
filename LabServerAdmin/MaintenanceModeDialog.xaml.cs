using System.Windows;

namespace LabServerAdmin
{
    public partial class MaintenanceModeDialog : Window
    {
        public string? MaintenanceMessage { get; private set; }

        public MaintenanceModeDialog()
        {
            InitializeComponent();
        }

        private void EnableButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MaintenanceMessageTextBox.Text))
            {
                MessageBox.Show(
                    "Please enter a maintenance message.",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                MaintenanceMessageTextBox.Focus();
                return;
            }

            MaintenanceMessage = MaintenanceMessageTextBox.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
