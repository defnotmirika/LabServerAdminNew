using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using LabServerAdmin.Services;
using Microsoft.Extensions.Configuration;

namespace LabServerAdmin
{
    public partial class VoiceEnrollmentDialog : Window
    {
        private readonly VoiceSpeakerService _speakerService;
        private readonly IConfiguration _configuration;
        private readonly string _username;
        private Process? _enrollmentProcess;
        private bool _isEnrollmentRunning;
        private int _currentPhrase;
        private int _totalSamples = 3;

        public bool WasEnrolled { get; private set; } = false;

        public VoiceEnrollmentDialog(VoiceSpeakerService speakerService, IConfiguration configuration, string username)
        {
            _speakerService = speakerService;
            _configuration = configuration;
            _username = username;
            InitializeComponent();

            UsernameLabel.Text = $"Professor: {username}";

            if (_speakerService.HasProfile(username))
            {
                ExistingProfileWarning.Visibility = Visibility.Visible;
            }

            UpdatePhraseDisplay();
        }

        private async void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isEnrollmentRunning)
            {
                return;
            }

            var scriptPath = ResolveEnrollorPath();
            if (scriptPath == null)
            {
                MessageBox.Show(
                    "main.py was not found.",
                    "Enrollment Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var process = await Task.Run(() => StartPythonProcess(scriptPath, _username, _configuration["VoiceRecognition:PythonExecutable"]));
            if (process == null)
            {
                MessageBox.Show(
                    "Python runtime was not found. Install Python or the py launcher.",
                    "Enrollment Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            ResetUiForEnrollment();

            _enrollmentProcess = process;
            _enrollmentProcess.OutputDataReceived += EnrollmentProcess_OutputDataReceived;
            _enrollmentProcess.ErrorDataReceived += EnrollmentProcess_ErrorDataReceived;
            _enrollmentProcess.Exited += EnrollmentProcess_Exited;
            _enrollmentProcess.BeginOutputReadLine();
            _enrollmentProcess.BeginErrorReadLine();
            _isEnrollmentRunning = true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!WasEnrolled)
            {
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            StopEnrollmentProcess();
            DialogResult = false;
            Close();
        }

        private void EnrollmentProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(e.Data);
                var root = document.RootElement;
                var status = root.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : null;

                switch (status)
                {
                    case "ready":
                        _totalSamples = root.TryGetProperty("total_samples", out var totalSamplesElement)
                            ? totalSamplesElement.GetInt32()
                            : 3;
                        var readyMessage = root.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString() ?? "Starting enrollment..."
                            : "Starting enrollment...";
                        Dispatcher.BeginInvoke(() =>
                        {
                            RecordingStatusText.Text = readyMessage;
                            RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
                            RecordingProgress.IsIndeterminate = true;
                            RecordButton.Content = "🔴 Recording...";
                        });
                        break;

                    case "sample":
                        var current = root.TryGetProperty("current", out var currentElement)
                            ? currentElement.GetInt32()
                            : 1;
                        var total = root.TryGetProperty("total", out var totalElement)
                            ? totalElement.GetInt32()
                            : _totalSamples;

                        Dispatcher.BeginInvoke(() =>
                        {
                            _totalSamples = total;
                            if (current > 1)
                            {
                                MarkSampleDone(current - 2);
                            }

                            _currentPhrase = Math.Max(0, Math.Min(current - 1, VoiceSpeakerService.EnrollmentPhrases.Length - 1));
                            UpdatePhraseDisplay();
                            RecordingStatusText.Text = $"Recording sample {current} of {total}... speak now!";
                            RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
                            RecordingProgress.IsIndeterminate = true;
                            RecordingProgress.Value = ((double)(current - 1) / total) * 100;
                            RecordButton.Content = "🔴 Recording...";
                        });
                        break;

                    case "warning":
                        var warningMessage = root.TryGetProperty("message", out var warningMessageElement)
                            ? warningMessageElement.GetString() ?? "Sample warning"
                            : "Sample warning";
                        Dispatcher.BeginInvoke(() =>
                        {
                            RecordingStatusText.Text = warningMessage;
                            RecordingIndicator.Fill = new SolidColorBrush(Colors.Orange);
                        });
                        break;

                    case "done":
                        Dispatcher.BeginInvoke(() =>
                        {
                            MarkSampleDone(_totalSamples - 1);
                            RecordingIndicator.Fill = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                            RecordingStatusText.Text = $"✅ Voice profile saved for '{_username}'.";
                            RecordingProgress.IsIndeterminate = false;
                            RecordingProgress.Value = 100;
                            RecordButton.Content = "✅ Saved";
                            RecordButton.IsEnabled = false;
                            SaveButton.Content = "Done";
                            SaveButton.IsEnabled = true;
                            WasEnrolled = true;
                        });
                        _isEnrollmentRunning = false;
                        break;

                    case "error":
                        var reason = root.TryGetProperty("reason", out var reasonElement)
                            ? reasonElement.GetString() ?? "unknown_error"
                            : "unknown_error";
                        Dispatcher.BeginInvoke(() => HandleEnrollmentError(reason));
                        _isEnrollmentRunning = false;
                        break;
                }
            }
            catch
            {
                Dispatcher.BeginInvoke(() =>
                {
                    RecordingStatusText.Text = e.Data;
                });
            }
        }

        private void EnrollmentProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                RecordingStatusText.Text = e.Data;
                RecordingIndicator.Fill = new SolidColorBrush(Colors.OrangeRed);
            });
        }

        private void EnrollmentProcess_Exited(object? sender, EventArgs e)
        {
            var wasEnrolled = WasEnrolled;
            _isEnrollmentRunning = false;

            Dispatcher.BeginInvoke(() =>
            {
                if (!wasEnrolled)
                {
                    RecordButton.IsEnabled = true;
                    RecordButton.Content = "🎤 Start Recording";
                    RecordingIndicator.Fill = new SolidColorBrush(Colors.Gray);
                    RecordingProgress.IsIndeterminate = false;
                    if (string.IsNullOrWhiteSpace(RecordingStatusText.Text) || RecordingStatusText.Text.StartsWith("Recording sample", StringComparison.OrdinalIgnoreCase))
                    {
                        RecordingStatusText.Text = "Enrollment stopped.";
                    }
                }
            });

            CleanupProcess();
        }

        private void ResetUiForEnrollment()
        {
            WasEnrolled = false;
            _currentPhrase = 0;
            _totalSamples = 3;
            RecordButton.IsEnabled = false;
            RecordButton.Content = "🔴 Recording...";
            SaveButton.IsEnabled = false;
            SaveButton.Content = "✅ Save Profile";
            RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
            RecordingStatusText.Text = "Starting enrollment...";
            RecordingProgress.Value = 0;
            RecordingProgress.IsIndeterminate = true;
            ResetSampleState();
            UpdatePhraseDisplay();
        }

        private void ResetSampleState()
        {
            ResetSample(Sample1Border, Sample1Text, "① Not recorded");
            ResetSample(Sample2Border, Sample2Text, "② Not recorded");
            ResetSample(Sample3Border, Sample3Text, "③ Not recorded");
        }

        private static void ResetSample(System.Windows.Controls.Border border, System.Windows.Controls.TextBlock text, string label)
        {
            border.Background = new SolidColorBrush(Color.FromRgb(233, 236, 239));
            border.BorderBrush = Brushes.Transparent;
            border.BorderThickness = new Thickness(0);
            text.Text = label;
            text.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
        }

        private void HandleEnrollmentError(string reason)
        {
            RecordButton.IsEnabled = true;
            RecordButton.Content = "🎤 Start Recording";
            SaveButton.IsEnabled = false;
            RecordingIndicator.Fill = new SolidColorBrush(Colors.OrangeRed);
            RecordingProgress.Value = 0;
            RecordingProgress.IsIndeterminate = false;
            RecordingStatusText.Text = $"Enrollment failed: {reason}";

            MessageBox.Show(
                $"Voice enrollment failed: {reason}",
                "Enrollment Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

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

            if (border == null || text == null)
            {
                return;
            }

            border.Background = new SolidColorBrush(Color.FromRgb(212, 237, 218));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            border.BorderThickness = new Thickness(1);
            text.Text = $"✅ Sample {index + 1} done";
            text.Foreground = new SolidColorBrush(Color.FromRgb(21, 87, 36));
        }

        private void StopEnrollmentProcess()
        {
            if (_enrollmentProcess == null)
            {
                return;
            }

            try
            {
                if (!_enrollmentProcess.HasExited)
                {
                    _enrollmentProcess.Kill(entireProcessTree: true);
                    _enrollmentProcess.WaitForExit(2000);
                }
            }
            catch
            {
            }
            finally
            {
                CleanupProcess();
                _isEnrollmentRunning = false;
            }
        }

        private void CleanupProcess()
        {
            if (_enrollmentProcess == null)
            {
                return;
            }

            _enrollmentProcess.OutputDataReceived -= EnrollmentProcess_OutputDataReceived;
            _enrollmentProcess.ErrorDataReceived -= EnrollmentProcess_ErrorDataReceived;
            _enrollmentProcess.Exited -= EnrollmentProcess_Exited;
            _enrollmentProcess.Dispose();
            _enrollmentProcess = null;
        }

        private static string? ResolveEnrollorPath()
        {
            var directCandidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Voice Auth", "main.py"),
                Path.Combine(Directory.GetCurrentDirectory(), "Voice Auth", "main.py")
            };

            foreach (var candidate in directCandidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Voice Auth", "main.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                candidate = Path.Combine(directory.FullName, "LabServerAdmin", "Voice Auth", "main.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static Process? StartPythonProcess(string scriptPath, string username, string? configuredPython)
        {
            var workingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;
            var escapedUsername = username.Replace("\"", "\\\"");
            var launchers = new List<(string FileName, string Arguments)>
            {
                (configuredPython ?? string.Empty, $"\"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --overwrite"),
                ("py", $"-3 \"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --overwrite"),
                ("python", $"\"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --overwrite"),
                ("python3", $"\"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --overwrite")
            };

            foreach (var launcher in launchers.Where(l => !string.IsNullOrWhiteSpace(l.FileName)))
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = launcher.FileName,
                        Arguments = launcher.Arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                    var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        continue;
                    }

                    process.EnableRaisingEvents = true;
                    return process;
                }
                catch
                {
                }
            }

            return null;
        }
    }
}