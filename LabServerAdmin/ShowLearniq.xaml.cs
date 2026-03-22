using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LabServerAdmin
{
    /// <summary>
    /// Interaction logic for ShowLearniq.xaml
    /// </summary>
    public partial class ShowLearniq : Window
    {
        public event EventHandler? AnimationCompleted;

        public ShowLearniq()
        {
            InitializeComponent();
            Loaded += ShowLearniq_Loaded;
        }

        private void ShowLearniq_Loaded(object sender, RoutedEventArgs e)
        {
            WelcomeOverlay.Opacity = 0;
            Logo.Opacity = 0;
            TitleText.Opacity = 0;

            var storyboard = new Storyboard();

            var overlayFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900));
            Storyboard.SetTarget(overlayFadeIn, WelcomeOverlay);
            Storyboard.SetTargetProperty(overlayFadeIn, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(overlayFadeIn);

            var logoFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1200))
            {
                BeginTime = TimeSpan.FromMilliseconds(450)
            };
            Storyboard.SetTarget(logoFadeIn, Logo);
            Storyboard.SetTargetProperty(logoFadeIn, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(logoFadeIn);

            var titleFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1200))
            {
                BeginTime = TimeSpan.FromMilliseconds(450)
            };
            Storyboard.SetTarget(titleFadeIn, TitleText);
            Storyboard.SetTargetProperty(titleFadeIn, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(titleFadeIn);

            var overlayFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(2600)
            };
            Storyboard.SetTarget(overlayFadeOut, WelcomeOverlay);
            Storyboard.SetTargetProperty(overlayFadeOut, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(overlayFadeOut);

            var logoFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(2600)
            };
            Storyboard.SetTarget(logoFadeOut, Logo);
            Storyboard.SetTargetProperty(logoFadeOut, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(logoFadeOut);

            var titleFadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(2600)
            };
            Storyboard.SetTarget(titleFadeOut, TitleText);
            Storyboard.SetTargetProperty(titleFadeOut, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(titleFadeOut);

            storyboard.Completed += (_, _) => AnimationCompleted?.Invoke(this, EventArgs.Empty);
            storyboard.Begin();
        }
    }
}
