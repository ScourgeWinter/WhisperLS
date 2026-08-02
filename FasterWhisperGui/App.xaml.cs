using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace FasterWhisperGui
{
    public partial class App : Application
    {
        private const double InitialLogicalWidth = 700;
        private const double InitialLogicalHeight = 560;

        public static Window? MainWindow { get; private set; }
        public static Frame? RootFrame { get; private set; }

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var window = new Window
            {
                Title = "WhisperLS"
            };
            MainWindow = window;
            SetWindowIcon(window);
            ConfigureCustomChrome(window);

            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            RootFrame = rootFrame;
            window.Content = rootFrame;
            rootFrame.Navigate(typeof(MainPage), args.Arguments);

            window.AppWindow.Closing += OnAppWindowClosing;
            ConfigureInitialWindowBounds(window);
            window.Activate();
        }

        private static void ConfigureCustomChrome(Window window)
        {
            window.ExtendsContentIntoTitleBar = true;
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }
        }

        private static void ConfigureInitialWindowBounds(Window window)
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
            var displayArea = DisplayArea.GetFromWindowId(
                window.AppWindow.Id,
                DisplayAreaFallback.Nearest);
            var workArea = displayArea.WorkArea;
            var width = Math.Min(workArea.Width - 48, (int)Math.Round(InitialLogicalWidth * scale));
            var height = Math.Min(workArea.Height - 48, (int)Math.Round(InitialLogicalHeight * scale));
            width = Math.Max(620, width);
            height = Math.Max(520, height);
            var x = workArea.X + Math.Max(24, (workArea.Width - width) / 2);
            var y = workArea.Y + Math.Max(24, (workArea.Height - height) / 2);
            window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }
        }

        private static void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (RootFrame?.Content is MainPage mainPage)
            {
                if (mainPage.RequestAnimatedClose())
                {
                    args.Cancel = true;
                    return;
                }
                mainPage.CancelActiveOperation();
            }
        }

        private static void SetWindowIcon(Window window)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WhisperLS.ico");
            if (File.Exists(iconPath))
            {
                window.AppWindow.SetIcon(iconPath);
            }
        }

        private static void OnNavigationFailed(object sender, NavigationFailedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
            if (sender is Frame frame)
            {
                frame.Content = new TextBlock
                {
                    Margin = new Thickness(24),
                    Text = "Impossibile caricare l'interfaccia. " +
                           (args.Exception?.Message ?? "Errore di navigazione sconosciuto."),
                    TextWrapping = TextWrapping.Wrap
                };
            }
        }

        private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hwnd);
    }
}
