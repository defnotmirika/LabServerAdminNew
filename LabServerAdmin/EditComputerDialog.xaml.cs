using System;
using System.Text.RegularExpressions;
using System.Windows;
using LabServerAdmin.Models;
using LabServerAdmin.Services;

namespace LabServerAdmin
{
    public partial class EditComputerDialog : Window
    {
        private readonly Computer _computer;
        private readonly DatabaseService _databaseService;
        private readonly TcpServerService _tcpServerService;

        public bool WasSaved { get; private set; } = false;
        public bool SendToClient => SendToClientCheckBox.IsChecked == true;

        public EditComputerDialog(Computer computer, DatabaseService databaseService, TcpServerService tcpServerService)
        {
            _computer = computer ?? throw new ArgumentNullException(nameof(computer));
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _tcpServerService = tcpServerService ?? throw new ArgumentNullException(nameof(tcpServerService));

            InitializeComponent();

            // Populate fields
            PcNameTextBox.Text = _computer.ClientName;

            // Enable "Send to Client" only if client is connected
            SendToClientCheckBox.IsEnabled = _tcpServerService.IsClientConnected(_computer.ClientName);
            if (!SendToClientCheckBox.IsEnabled)
            {
                SendToClientCheckBox.IsChecked = false;
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate input
            if (string.IsNullOrWhiteSpace(PcNameTextBox.Text))
            {
                MessageBox.Show("PC Name is required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                PcNameTextBox.Focus();
                return;
            }

            try
            {
                // Update computer in database (keep existing Lab ID)
                var success = await _databaseService.UpdateComputerAsync(
                    _computer.Id,
                    PcNameTextBox.Text.Trim(),
                    _computer.LabId // Keep existing Lab ID
                );

                if (success)
                {
                    // Update the computer object
                    _computer.ClientName = PcNameTextBox.Text.Trim();
                    // Lab ID remains unchanged

                    WasSaved = true;

                    // Send configuration to client if enabled and connected
                    if (SendToClient && _tcpServerService.IsClientConnected(_computer.ClientName))
                    {
                        var configJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            pc_name = _computer.ClientName
                        });

                        await _tcpServerService.SendCommandAsync(_computer.ClientName, "update_config", configJson);
                        
                        MessageBox.Show(
                            $"Computer '{_computer.ClientName}' updated successfully!\n\nConfiguration has been sent to the client.",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Computer '{_computer.ClientName}' updated successfully!",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }

                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(
                        "Failed to update computer. Please try again.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error updating computer: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static bool IsValidIpAddress(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return false;

            // Simple IP address validation
            var ipRegex = new Regex(@"^(\d{1,3}\.){3}\d{1,3}$");
            if (!ipRegex.IsMatch(ipAddress))
                return false;

            var parts = ipAddress.Split('.');
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out int num) || num < 0 || num > 255)
                    return false;
            }

            return true;
        }
    }
}
