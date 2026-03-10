using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using LabServerAdmin.Services;

namespace LabServerAdmin
{
    public partial class VoiceEnrollmentDialog : Window
    {
        private readonly VoiceSpeakerService _speakerService;
        private readonly string _username;

        private readonly List<byte[]> _recordedSamples = new();
        private int _currentPhrase = 0;
        private bool _isRecording = false;
        private CancellationTokenSource? _recordingCts;

        public bool WasEnrolled { get; private set; } = false;

        public VoiceEnrollmentDialog(VoiceSpeakerService speakerService, string username)
        {
            _speakerService = speakerService;
            _username = username;
            InitializeComponent();

            UsernameLabel.Text = $"Professor: {username}";

            // Show warning if profile already exists
            if (_speakerService.HasProfile(username))
                ExistingProfileWarning.Visibility = Visibility.Visible;

            UpdatePhraseDisplay();
        }

        private async void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording) return;

            _isRecording = true;
            _recordingCts = new CancellationTokenSource();

            // Update UI to recording state
            RecordButton.IsEnabled = false;
            RecordButton.Content = "🔴 Recording...";
            RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
            RecordingStatusText.Text = "Recording... speak now!";
            RecordingProgress.Value = 0;

            try
            {
                var progress = new Progress<double>(p =>
                {
                    Dispatcher.Invoke(() => RecordingProgress.Value = p * 100);
                });

                var sample = await _speakerService.RecordSampleAsync(progress, _recordingCts.Token);
                _recordedSamples.Add(sample);

                // Mark this sample as done
                MarkSampleDone(_currentPhrase);
                _currentPhrase++;

                if (_currentPhrase < VoiceSpeakerService.EnrollmentPhrases.Length)
                {
                    // Move to next phrase
                    UpdatePhraseDisplay();
                    RecordingStatusText.Text = $"✅ Sample {_currentPhrase} recorded! Ready for next.";
                    RecordingIndicator.Fill = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                    RecordingProgress.Value = 0;
                    RecordButton.Content = "🎤 Start Recording";
                    RecordButton.IsEnabled = true;
                }
                else
                {
                    // All 3 samples done
                    RecordingStatusText.Text = "✅ All 3 samples recorded! Click Save Profile.";
                    RecordingIndicator.Fill = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                    RecordingProgress.Value = 100;
                    RecordButton.IsEnabled = false;
                    RecordButton.Content = "✅ All Done";
                    SaveButton.IsEnabled = true;
                }
            }
            catch (OperationCanceledException)
            {
                RecordingStatusText.Text = "Recording cancelled.";
                RecordingIndicator.Fill = new SolidColorBrush(Colors.Gray);
                RecordButton.Content = "🎤 Start Recording";
                RecordButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Recording error: {ex.Message}\n\nMake sure your microphone is connected and allowed.",
                    "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
                RecordingStatusText.Text = "Error — please try again.";
                RecordingIndicator.Fill = new SolidColorBrush(Colors.OrangeRed);
                RecordButton.Content = "🎤 Start Recording";
                RecordButton.IsEnabled = true;
            }
            finally
            {
                _isRecording = false;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _speakerService.SaveProfile(_username, _recordedSamples);
                WasEnrolled = true;

                MessageBox.Show(
                    $"✅ Voice profile saved for '{_username}'!\n\n" +
                    "The system will now verify your voice before executing commands.\n\n" +
                    "If commands are rejected, you can re-enroll anytime from Settings.",
                    "Enrollment Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to save voice profile: {ex.Message}",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _recordingCts?.Cancel();
            DialogResult = false;
            Close();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdatePhraseDisplay()
        {
            if (_currentPhrase < VoiceSpeakerService.EnrollmentPhrases.Length)
            {
                PhraseNumberText.Text = $"Phrase {_currentPhrase + 1} of {VoiceSpeakerService.EnrollmentPhrases.Length}";
                PhraseText.Text = VoiceSpeakerService.EnrollmentPhrases[_currentPhrase];
            }
        }

        private void MarkSampleDone(int index)
        {
            var (border, text) = index switch
            {
                0 => (Sample1Border, Sample1Text),
                1 => (Sample2Border, Sample2Text),
                2 => (Sample3Border, Sample3Text),
                _ => (null, null)
            };

            if (border == null || text == null) return;

            border.Background = new SolidColorBrush(Color.FromRgb(212, 237, 218));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            border.BorderThickness = new Thickness(1);
            text.Text = $"✅ Sample {index + 1} done";
            text.Foreground = new SolidColorBrush(Color.FromRgb(21, 87, 36));
        }
    }
}