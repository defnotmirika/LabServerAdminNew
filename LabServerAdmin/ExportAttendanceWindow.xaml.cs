using System;
using System.Windows;
using Microsoft.Win32;

namespace LabServerAdmin
{
    public partial class ExportAttendanceWindow : Window
    {
        public DateTime? SelectedStartDate { get; private set; }
        public DateTime? SelectedEndDate { get; private set; }
        public string? SelectedFilePath { get; private set; }
        public bool IsExportConfirmed { get; private set; } = false;

        public ExportAttendanceWindow()
        {
            InitializeComponent();
            
            // Set default dates to today
            var today = DateTime.Today;
            StartDatePicker.SelectedDate = today;
            EndDatePicker.SelectedDate = today;
            
            // Set minimum date to prevent selecting future dates
            StartDatePicker.DisplayDateStart = DateTime.Today.AddYears(-10);
            StartDatePicker.DisplayDateEnd = DateTime.Today;
            EndDatePicker.DisplayDateStart = DateTime.Today.AddYears(-10);
            EndDatePicker.DisplayDateEnd = DateTime.Today;
        }

        private void TodayButton_Click(object sender, RoutedEventArgs e)
        {
            var today = DateTime.Today;
            StartDatePicker.SelectedDate = today;
            EndDatePicker.SelectedDate = today;
        }

        private void ThisWeekButton_Click(object sender, RoutedEventArgs e)
        {
            var today = DateTime.Today;
            var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
            StartDatePicker.SelectedDate = startOfWeek;
            EndDatePicker.SelectedDate = today;
        }

        private void ThisMonthButton_Click(object sender, RoutedEventArgs e)
        {
            var today = DateTime.Today;
            var startOfMonth = new DateTime(today.Year, today.Month, 1);
            StartDatePicker.SelectedDate = startOfMonth;
            EndDatePicker.SelectedDate = today;
        }

        private void DatePicker_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Validate date range
            if (StartDatePicker.SelectedDate.HasValue && EndDatePicker.SelectedDate.HasValue)
            {
                if (StartDatePicker.SelectedDate.Value > EndDatePicker.SelectedDate.Value)
                {
                    ShowError("Start date cannot be after end date.");
                    EndDatePicker.SelectedDate = StartDatePicker.SelectedDate;
                }
                else
                {
                    HideError();
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsExportConfirmed = false;
            DialogResult = false;
            Close();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (!StartDatePicker.SelectedDate.HasValue || !EndDatePicker.SelectedDate.HasValue)
            {
                ShowError("Please select both start and end dates.");
                return;
            }

            if (StartDatePicker.SelectedDate.Value > EndDatePicker.SelectedDate.Value)
            {
                ShowError("Start date cannot be after end date.");
                return;
            }

            // Show save file dialog
            var saveDialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                DefaultExt = "csv",
                FileName = $"attendance_logs_{StartDatePicker.SelectedDate.Value:yyyyMMdd}_to_{EndDatePicker.SelectedDate.Value:yyyyMMdd}"
            };

            if (saveDialog.ShowDialog() == true)
            {
                SelectedStartDate = StartDatePicker.SelectedDate.Value;
                SelectedEndDate = EndDatePicker.SelectedDate.Value;
                SelectedFilePath = saveDialog.FileName;
                IsExportConfirmed = true;
                DialogResult = true;
                Close();
            }
        }

        private void ShowError(string message)
        {
            ErrorTextBlock.Text = message;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
        }
    }
}

