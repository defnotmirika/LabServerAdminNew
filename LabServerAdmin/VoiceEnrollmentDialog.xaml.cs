using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
        private int _totalSamples = VoiceSpeakerService.EnrollmentPhrases.Length;
        private readonly List<(Border Border, TextBlock Text)> _sampleIndicators = new();
        private readonly Random _waveRandom = new();
        private DispatcherTimer? _waveTimer;
        private System.Windows.Shapes.Rectangle[] _waveBars = Array.Empty<System.Windows.Shapes.Rectangle>();
        private int _tutorialStepIndex;
        private const int EnrollmentSampleDurationSeconds = 5;
        private static readonly string[] TutorialSteps =
        {
            "This is the voice register button. Click it to begin recording your voice samples.",
            "Read the phrase shown in the blue box clearly when recording starts.",
            "When all samples are complete, save your voice profile to finish."
        };

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

            InitializeWaveVisualizer();
            InitializeSampleIndicators();
            UpdateTutorialStep();
            UpdatePhraseDisplay();
        }

        private void MicButton_Click(object sender, RoutedEventArgs e)
        {
            RecordButton_Click(sender, e);
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_tutorialStepIndex > 0)
            {
                _tutorialStepIndex--;
                UpdateTutorialStep();
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_tutorialStepIndex < TutorialSteps.Length - 1)
            {
                _tutorialStepIndex++;
                UpdateTutorialStep();
                return;
            }

            if (_tutorialStepIndex == TutorialSteps.Length - 1)
            {
                if (WasEnrolled)
                {
                    _tutorialStepIndex = TutorialSteps.Length;
                    UpdateTutorialStep();
                }
                return;
            }

            if (_tutorialStepIndex == TutorialSteps.Length)
            {
                DialogResult = true;
                Close();
            }
        }

        private void UpdateTutorialStep()
        {
            if (_tutorialStepIndex < TutorialSteps.Length)
            {
                TutorialText.Text = TutorialSteps[_tutorialStepIndex];
                PrevButton.Visibility = _tutorialStepIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
                NextButton.Content = _tutorialStepIndex == TutorialSteps.Length - 1 ? "Done" : "Next";
                return;
            }

            TutorialText.Text = "Congratulations! 🎉 Your voice profile is saved and ready. Click Let’s go! to continue.";
            PrevButton.Visibility = Visibility.Collapsed;
            NextButton.Content = "Let's go!";
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

            var process = await Task.Run(() => StartPythonProcess(scriptPath, _username, _configuration["VoiceRecognition:PythonExecutable"], _totalSamples, EnrollmentSampleDurationSeconds));
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
            DialogResult = WasEnrolled;
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

                            MarkSampleRecording(current - 1);
                            _currentPhrase = Math.Max(0, Math.Min(current - 1, VoiceSpeakerService.EnrollmentPhrases.Length - 1));
                            UpdatePhraseDisplay();
                            RecordingStatusText.Text = $"Recording sample {current} of {total}... speak now!";
                            SampleTimerStatusText.Text = $"Timer running: sample {current}/{total} ({EnrollmentSampleDurationSeconds}s)";
                            RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
                            RecordingProgress.IsIndeterminate = true;
                            RecordingProgress.Value = ((double)(current - 1) / total) * 100;
                            RecordButton.Content = "🔴 Recording...";
                            StartMicAnimation();
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
                            SampleTimerStatusText.Text = "✅ Timer finished: all samples completed.";
                            RecordingProgress.IsIndeterminate = false;
                            RecordingProgress.Value = 100;
                            RecordButton.Content = "🔁 Re-record";
                            RecordButton.IsEnabled = true;
                            SaveButton.Content = "Done";
                            SaveButton.IsEnabled = true;
                            WasEnrolled = true;
                            StopMicAnimation();

                            _tutorialStepIndex = TutorialSteps.Length;
                            UpdateTutorialStep();
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

                StopMicAnimation();
            });

            CleanupProcess();
        }

        private void ResetUiForEnrollment()
        {
            WasEnrolled = false;
            _currentPhrase = 0;
            _totalSamples = VoiceSpeakerService.EnrollmentPhrases.Length;
            RecordButton.IsEnabled = false;
            RecordButton.Content = "🔴 Recording...";
            SaveButton.IsEnabled = false;
            SaveButton.Content = "✅ Save Profile";
            RecordingIndicator.Fill = new SolidColorBrush(Colors.Red);
            RecordingStatusText.Text = "Starting enrollment...";
            SampleTimerStatusText.Text = "Timer: Starting...";
            RecordingProgress.Value = 0;
            RecordingProgress.IsIndeterminate = true;
            ResetSampleState();
            UpdatePhraseDisplay();
            StopMicAnimation();
        }

        private void ResetSampleState()
        {
            for (var i = 0; i < _sampleIndicators.Count; i++)
            {
                ResetSample(_sampleIndicators[i].Border, _sampleIndicators[i].Text, GetSampleLabel(i));
            }
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
            SampleTimerStatusText.Text = $"⛔ Timer stopped: {reason}";
            StopMicAnimation();

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
            if (index < 0 || index >= _sampleIndicators.Count)
            {
                return;
            }

            var (border, text) = _sampleIndicators[index];

            border.Background = new SolidColorBrush(Color.FromRgb(212, 237, 218));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            border.BorderThickness = new Thickness(1);
            text.Text = $"☑ Phrase {index + 1} completed";
            text.Foreground = new SolidColorBrush(Color.FromRgb(21, 87, 36));
        }

        private void MarkSampleRecording(int index)
        {
            if (index < 0 || index >= _sampleIndicators.Count)
            {
                return;
            }

            var (border, text) = _sampleIndicators[index];
            border.Background = new SolidColorBrush(Color.FromRgb(255, 243, 205));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));
            border.BorderThickness = new Thickness(1);
            text.Text = $"◔ Phrase {index + 1} recording";
            text.Foreground = new SolidColorBrush(Color.FromRgb(133, 100, 4));
        }

        private void InitializeSampleIndicators()
        {
            SampleStatusPanel.Children.Clear();
            _sampleIndicators.Clear();

            for (var i = 0; i < VoiceSpeakerService.EnrollmentPhrases.Length; i++)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(233, 236, 239)),
                    CornerRadius = new CornerRadius(20),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(4, 4, 4, 4),
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };

                var text = new TextBlock
                {
                    Text = GetSampleLabel(i),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136))
                };

                border.Child = text;
                SampleStatusPanel.Children.Add(border);
                _sampleIndicators.Add((border, text));
            }
        }

        private static string GetSampleLabel(int index) => $"☐ Phrase {index + 1} not completed";

        private void InitializeWaveVisualizer()
        {
            _waveBars = new[] { Wave1, Wave2, Wave3, Wave4, Wave5 };
            _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _waveTimer.Tick += (_, _) => UpdateWaveFrame();
            StopMicAnimation();
        }

        private void StartMicAnimation()
        {
            if (_waveTimer == null)
            {
                return;
            }

            if (!_waveTimer.IsEnabled)
            {
                _waveTimer.Start();
            }
        }

        private void StopMicAnimation()
        {
            if (_waveTimer?.IsEnabled == true)
            {
                _waveTimer.Stop();
            }

            foreach (var bar in _waveBars)
            {
                bar.Height = 20;
                bar.Fill = Brushes.Green;
            }

            Glow.Opacity = 0;
        }

        private void UpdateWaveFrame()
        {
            foreach (var bar in _waveBars)
            {
                bar.Height = _waveRandom.Next(12, 56);
                bar.Fill = bar.Height > 42
                    ? Brushes.OrangeRed
                    : bar.Height > 28
                        ? Brushes.Gold
                        : Brushes.Green;
            }

            Glow.Opacity = 0.15 + _waveRandom.NextDouble() * 0.35;
        }

        private void StopEnrollmentProcess()
        {
            var process = _enrollmentProcess;
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
            finally
            {
                CleanupProcess();
                _isEnrollmentRunning = false;
                StopMicAnimation();
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

        private static Process? StartPythonProcess(string scriptPath, string username, string? configuredPython, int samples, int durationSeconds)
        {
            var workingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;
            var escapedUsername = username.Replace("\"", "\\\"");
            var sampleCount = Math.Max(1, samples);
            var duration = Math.Max(1, durationSeconds);
            var launchers = new List<(string FileName, string Arguments)>
            {
                (configuredPython ?? string.Empty, $"\"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --samples {sampleCount} --duration {duration} --overwrite"),
                ("py", $"-3 \"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --samples {sampleCount} --duration {duration} --overwrite"),
                ("python", $"\"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --samples {sampleCount} --duration {duration} --overwrite"),
                ("python3", $"\"{scriptPath}\" --mode enroll --name \"{escapedUsername}\" --samples {sampleCount} --duration {duration} --overwrite")
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