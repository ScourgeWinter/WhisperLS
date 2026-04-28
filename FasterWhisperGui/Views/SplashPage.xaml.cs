using Microsoft.UI.Xaml.Media.Animation;

namespace FasterWhisperGui.Views
{
    public sealed partial class SplashPage : Page
    {
        public SplashPage()
        {
            this.InitializeComponent();
            Loaded += SplashPage_Loaded;
        }

        private async void SplashPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Must be synchronous (before any await) to prevent WinUI from drawing
            // a default title-bar strip at the top of the window
            App.MainWindow?.SetTitleBar(SplashRoot);

            try
            {
                var loaded = new TaskCompletionSource();
                SplashImage.ImageOpened += (_, _) => loaded.TrySetResult();
                SplashImage.ImageFailed += (_, _) => loaded.TrySetResult();
                await Task.WhenAny(loaded.Task, Task.Delay(250));

                await App.RevealStartupWindowAsync();
                await PlayEntranceAsync();
                await Task.Delay(1000);
                await PlayExitAsync();
                await App.ExpandToFullSizeAsync();
            }
            catch { }
            finally
            {
                Frame?.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
            }
        }

        private async Task PlayEntranceAsync()
        {
            var storyboard = new Storyboard();

            var scaleX = new DoubleAnimation
            {
                From = 0.92, To = 1.0,
                Duration = TimeSpan.FromMilliseconds(360),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scaleX, SplashScale);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");

            var scaleY = new DoubleAnimation
            {
                From = 0.92, To = 1.0,
                Duration = TimeSpan.FromMilliseconds(360),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scaleY, SplashScale);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");

            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);

            var tcs = new TaskCompletionSource();
            storyboard.Completed += (_, _) => tcs.TrySetResult();
            storyboard.Begin();
            await tcs.Task;
        }

        private async Task PlayExitAsync()
        {
            var storyboard = new Storyboard();

            var fadeOut = new DoubleAnimation
            {
                From = 1, To = 0,
                Duration = TimeSpan.FromMilliseconds(320),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeOut, SplashImage);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");

            var scaleX = new DoubleAnimation
            {
                From = 1.0, To = 1.18,
                Duration = TimeSpan.FromMilliseconds(320),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scaleX, SplashScale);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");

            var scaleY = new DoubleAnimation
            {
                From = 1.0, To = 1.18,
                Duration = TimeSpan.FromMilliseconds(320),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scaleY, SplashScale);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");

            storyboard.Children.Add(fadeOut);
            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);

            var tcs = new TaskCompletionSource();
            storyboard.Completed += (_, _) => tcs.TrySetResult();
            storyboard.Begin();
            await tcs.Task;
        }
    }
}
