using NAudio.Wave;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LabServerAdmin
{
    public partial class MicTest : Window
    {
        // Tutorial state for the embedded tutorial box
        private int tutorialStep = 0;

        private void ShowTutorialStep()
        {
            var tutorialBox = FindName("TutorialBox") as Border;
            var tutorialText = FindName("TutorialText") as TextBlock;
            var prevBtn = FindName("PrevButton") as Button;
            var nextBtn = FindName("NextButton") as Button;

            if (tutorialBox == null || tutorialText == null || prevBtn == null || nextBtn == null)
                return;

            tutorialBox.Visibility = Visibility.Visible;

            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            tutorialBox.BeginAnimation(OpacityProperty, fadeIn);

            if (tutorialStep == 0)
            {
                tutorialText.Text = "Step 1: Let’s start! 🎉 Select your microphone input from the dropdown list below the mic button.";
            }
            else if (tutorialStep == 1)
            {
                tutorialText.Text = "Step 2: Great! ✅ Click the mic button to start testing your selected input device.";
            }
            else if (tutorialStep == 2)
            {
                tutorialText.Text = "Step 3: Awesome! 🎙️ Speak normally and watch the sound bars move. Click the mic button again to stop the test.";
            }
            else
            {
                tutorialBox.Visibility = Visibility.Collapsed;
            }

            prevBtn.Visibility = tutorialStep > 0 ? Visibility.Visible : Visibility.Collapsed;
            nextBtn.Content = "Next";
        }
        private WaveInEvent waveIn;
        private DispatcherTimer timer;
        private float level = 0;
        private bool isRunning = false;
        private int selectedDeviceIndex = 0;

        private const int WM_DEVICECHANGE = 0x0219;

        public MicTest()
        {
            InitializeComponent();
            RefreshDevices();
            ShowTutorialStep();
        }

        // 🔥 Hook into Windows messages
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                Dispatcher.Invoke(() =>
                {
                    if (isRunning)
                    {
                        StopMic();
                        StopGlow();
                        ResetBars();
                        isRunning = false;
                    }

                    RefreshDevices();
                });
            }

            return IntPtr.Zero;
        }

        // 🎤 MIC BUTTON
        private void MicButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                StartMic();
                StartGlow();
                MicButton.Background = new SolidColorBrush(Colors.Gray);
            }
            else
            {
                StopMic();
                StopGlow();
                ResetBars();
                MicButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#F8B800");
            }

            isRunning = !isRunning;
        }

        private void StartMic()
        {
            waveIn = new WaveInEvent
            {
                DeviceNumber = selectedDeviceIndex,
                WaveFormat = new WaveFormat(44100, 1)
            };

            waveIn.DataAvailable += OnDataAvailable;
            waveIn.StartRecording();

            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };

            timer.Tick += UpdateWave;
            timer.Start();
        }

        private void StopMic()
        {
            timer?.Stop();
            waveIn?.StopRecording();
            waveIn?.Dispose();
        }

        // 🌟 GLOW EFFECT
        private void StartGlow()
        {
            DoubleAnimation glow = new DoubleAnimation
            {
                From = 0.2,
                To = 0.8,
                Duration = TimeSpan.FromSeconds(0.6),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };

            Glow.BeginAnimation(OpacityProperty, glow);
        }

        private void StopGlow()
        {
            Glow.BeginAnimation(OpacityProperty, null);
            Glow.Opacity = 0;
        }

        // 🎧 AUDIO LEVEL
        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            float max = 0;

            for (int i = 0; i < e.Buffer.Length; i += 2)
            {
                short sample = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
                float sample32 = sample / 32768f;
                max = Math.Max(max, Math.Abs(sample32));
            }

            level = max * 100;
        }

        private void UpdateWave(object sender, EventArgs e)
        {
            Animate(Wave1, level);
            Animate(Wave2, level + 10);
            Animate(Wave3, level + 20);
            Animate(Wave4, level + 10);
            Animate(Wave5, level);
        }

        private void Animate(System.Windows.Shapes.Rectangle bar, double value)
        {
            double height = Math.Max(10, Math.Min(150, value * 2));
            bar.Height = height;

            if (value < 40)
                bar.Fill = new SolidColorBrush(Colors.Green);
            else if (value < 80)
                bar.Fill = new SolidColorBrush(Colors.Yellow);
            else
                bar.Fill = new SolidColorBrush(Colors.Red);
        }

        private void ResetBars()
        {
            ResetBar(Wave1);
            ResetBar(Wave2);
            ResetBar(Wave3);
            ResetBar(Wave4);
            ResetBar(Wave5);
        }

        private void ResetBar(System.Windows.Shapes.Rectangle bar)
        {
            bar.Height = 20;
            bar.Fill = new SolidColorBrush(Colors.Green);
        }

        // 🔄 DEVICE HANDLING
        private void RefreshDevices()
        {
            int previousIndex = DeviceList.SelectedIndex;

            DeviceList.Items.Clear();

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var device = WaveIn.GetCapabilities(i);
                DeviceList.Items.Add(device.ProductName);
            }

            if (DeviceList.Items.Count > 0)
            {
                DeviceList.SelectedIndex = Math.Min(previousIndex, DeviceList.Items.Count - 1);
            }
        }

        // Tutorial navigation handlers (for the embedded tutorial box)
        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (tutorialStep == 2)
            {
                TransitionToVoiceEnrollment();
                return;
            }

            tutorialStep++;
            ShowTutorialStep();
        }

        private void TransitionToVoiceEnrollment()
        {
            if (isRunning)
            {
                StopMic();
                StopGlow();
                ResetBars();
                isRunning = false;
            }

            var mainWindow = Owner as MainWindow ?? Application.Current.MainWindow as MainWindow;
            if (mainWindow == null)
            {
                MessageBox.Show(
                    "Cannot open Voice Enrollment from this context.",
                    "Navigation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Hide();
            mainWindow.OpenVoiceEnrollmentFromMicTest();
            Close();
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (tutorialStep > 0)
            {
                tutorialStep--;
                ShowTutorialStep();
            }
        }

        private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedDeviceIndex = DeviceList.SelectedIndex;
        }
    }
}
