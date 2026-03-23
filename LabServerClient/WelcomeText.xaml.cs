using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LabServerClient
{
    /// <summary>
    /// Interaction logic for WelcomeText.xaml
    /// </summary>
    public partial class WelcomeText : Window
    {
        public event EventHandler? AnimationCompleted;

        public WelcomeText()
        {
            InitializeComponent();
            Loaded += WelcomeText_Loaded;
        }

        private void WelcomeText_Loaded(object sender, RoutedEventArgs e)
        {
            var welcomeOverlay = FindName("WelcomeOverlay") as Grid;
            var welcomeTitle = FindName("WelcomeTitle") as TextBlock;
            var subtitleText = FindName("SubtitleText") as TextBlock;

            if (welcomeOverlay == null || welcomeTitle == null || subtitleText == null)
            {
                return;
            }

            welcomeOverlay.Opacity = 0;
            welcomeTitle.Opacity = 0;
            subtitleText.Opacity = 0;

            var storyboard = new Storyboard();

            var overlayFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1000));
            Storyboard.SetTarget(overlayFadeIn, welcomeOverlay);
            Storyboard.SetTargetProperty(overlayFadeIn, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(overlayFadeIn);

            var titleFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(500)
            };
            Storyboard.SetTarget(titleFadeIn, welcomeTitle);
            Storyboard.SetTargetProperty(titleFadeIn, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(titleFadeIn);

            var subtitleFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(700)
            };
            Storyboard.SetTarget(subtitleFadeIn, subtitleText);
            Storyboard.SetTargetProperty(subtitleFadeIn, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(subtitleFadeIn);

            var overlayFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(3200))
            {
                BeginTime = TimeSpan.FromMilliseconds(4200)
            };
            Storyboard.SetTarget(overlayFadeOut, welcomeOverlay);
            Storyboard.SetTargetProperty(overlayFadeOut, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(overlayFadeOut);

            var titleFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(3200))
            {
                BeginTime = TimeSpan.FromMilliseconds(4200)
            };
            Storyboard.SetTarget(titleFadeOut, welcomeTitle);
            Storyboard.SetTargetProperty(titleFadeOut, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(titleFadeOut);

            var subtitleFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(3200))
            {
                BeginTime = TimeSpan.FromMilliseconds(4200)
            };
            Storyboard.SetTarget(subtitleFadeOut, subtitleText);
            Storyboard.SetTargetProperty(subtitleFadeOut, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(subtitleFadeOut);

            storyboard.Completed += (_, _) => AnimationCompleted?.Invoke(this, EventArgs.Empty);
            storyboard.Begin();
        }
    }
}
