using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;
using System.IO;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace FasterWhisperGui
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }
        public static Frame?  RootFrame  { get; private set; }
        private Window? window;

        private const double SplashLogicalSize = 340;
        private const double FullLogicalWidth  = 820;
        private const double FullLogicalHeight = 640;
        private const int    GWL_STYLE         = -16;
        private const int    GWL_EXSTYLE       = -20;
        private const long   WS_EX_LAYERED     = 0x00080000L;
        private const uint   LWA_ALPHA         = 0x00000002;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            window ??= new Window();
            MainWindow = window;
            window.Title = "WhisperLS";
            window.ExtendsContentIntoTitleBar = true;
            SetWindowIcon(window);

            if (window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.Background = new SolidColorBrush(Color.FromArgb(255, 15, 17, 21));
                rootFrame.Opacity = 1;
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
            }
            else
            {
                rootFrame.Background = new SolidColorBrush(Color.FromArgb(255, 15, 17, 21));
                rootFrame.Opacity = 1;
            }

            RootFrame = rootFrame;

            _ = rootFrame.Navigate(typeof(SplashPage), e.Arguments);

            var hwnd = WindowNative.GetWindowHandle(window);
            HideWindowUntilSplashIsReady(hwnd);
            SuppressNativeChrome();

            var displayArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
            var dpiScale    = GetDpiForWindow(hwnd) / 96.0;

            // Start with a small square window for the splash screen
            var splashPhys = (int)Math.Round(SplashLogicalSize * dpiScale);
            window.AppWindow.Resize(new SizeInt32(splashPhys, splashPhys));
            window.AppWindow.Move(new PointInt32(
                Math.Max(0, displayArea.WorkArea.X + ((displayArea.WorkArea.Width  - splashPhys) / 2)),
                Math.Max(0, displayArea.WorkArea.Y + ((displayArea.WorkArea.Height - splashPhys) / 2))));

            window.Activate();
        }

        public static async Task RevealStartupWindowAsync()
        {
            if (MainWindow is null) return;

            var hwnd = WindowNative.GetWindowHandle(MainWindow);
            await WaitForRenderedFramesAsync(2);
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }

        public static async Task ExpandToFullSizeAsync()
        {
            if (MainWindow is null) return;
            var hwnd = WindowNative.GetWindowHandle(MainWindow);
            var dpiScale = GetDpiForWindow(hwnd) / 96.0;
            var displayArea = DisplayArea.GetFromWindowId(MainWindow.AppWindow.Id, DisplayAreaFallback.Primary);

            var endW = Math.Min(Math.Max(320, displayArea.WorkArea.Width  - 24),
                                (int)Math.Round(FullLogicalWidth  * dpiScale));
            var endH = Math.Min(Math.Max(320, displayArea.WorkArea.Height - 24),
                                (int)Math.Round(FullLogicalHeight * dpiScale));
            var x    = Math.Max(0, displayArea.WorkArea.X + ((displayArea.WorkArea.Width  - endW) / 2));
            var y    = Math.Max(0, displayArea.WorkArea.Y + ((displayArea.WorkArea.Height - endH) / 2));

            ClearLayeredWindow(hwnd);

            var appWindow = MainWindow.AppWindow;
            var startPosition = appWindow.Position;
            var startSize = appWindow.Size;
            var target = new RectInt32(x, y, endW, endH);
            var tcs = new TaskCompletionSource();
            var sw = Stopwatch.StartNew();
            const double durationMs = 420;

            void OnRendering(object? sender, object e)
            {
                var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
                var eased = 1 - Math.Pow(1 - t, 3);
                var bounds = new RectInt32(
                    Lerp(startPosition.X, target.X, eased),
                    Lerp(startPosition.Y, target.Y, eased),
                    Math.Max(1, Lerp(startSize.Width, target.Width, eased)),
                    Math.Max(1, Lerp(startSize.Height, target.Height, eased)));

                appWindow.MoveAndResize(bounds);

                if (t >= 1.0)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    appWindow.MoveAndResize(target);
                    tcs.TrySetResult();
                }
            }

            CompositionTarget.Rendering += OnRendering;
            await tcs.Task;
            SuppressNativeChrome();
        }

        private static Task WaitForRenderedFramesAsync(int frameCount)
        {
            var tcs = new TaskCompletionSource();
            var remaining = Math.Max(1, frameCount);

            void OnRendering(object? sender, object e)
            {
                remaining--;
                if (remaining > 0)
                {
                    return;
                }

                CompositionTarget.Rendering -= OnRendering;
                tcs.TrySetResult();
            }

            CompositionTarget.Rendering += OnRendering;
            return tcs.Task;
        }

        private static int Lerp(int start, int end, double amount) =>
            (int)Math.Round(start + ((end - start) * amount));

        public static void SuppressNativeChrome()
        {
            if (MainWindow is null)
            {
                return;
            }

            if (MainWindow.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = true;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            var hwnd = WindowNative.GetWindowHandle(MainWindow);
            RemoveNativeResizeBorder(hwnd);
            ApplyNativeWindowColors(hwnd);
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private static void SetWindowIcon(Window window)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WhisperLS.ico");
            if (File.Exists(iconPath))
            {
                window.AppWindow.SetIcon(iconPath);
                return;
            }

            iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.ico");
            if (File.Exists(iconPath))
            {
                window.AppWindow.SetIcon(iconPath);
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hWnd);

        private static void ApplyNativeWindowColors(nint hwnd)
        {
            const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWA_BORDER_COLOR             = 34;
            const int DWMWA_CAPTION_COLOR            = 35;
            const int DWMWA_TEXT_COLOR               = 36;
            // DWMWCP_ROUND = 2: force rounded corners even without WS_CAPTION
            const int DWMWCP_ROUND                   = 2;
            const int DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;

            var darkMode         = 1;
            var roundCorners     = DWMWCP_ROUND;
            var frameBorderColor = unchecked((int)0xFFFFFFFE);
            var windowBackground = 0x00FFD654;
            var textColor        = 0x00EDEAE8;
            var zeroFrameBorder  = 0;

            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,  ref darkMode,         sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundCorners,     sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR,             ref frameBorderColor, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR,            ref windowBackground, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR,               ref textColor,        sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS, ref zeroFrameBorder, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        private static void RemoveNativeResizeBorder(nint hwnd)
        {
            const long WS_CAPTION    = 0x00C00000L;
            const long WS_BORDER     = 0x00800000L;
            const long WS_THICKFRAME = 0x00040000L;
            const long WS_SYSMENU    = 0x00080000L;
            const long WS_MINIMIZEBOX = 0x00020000L;
            const uint SWP_NOSIZE    = 0x0001;
            const uint SWP_NOMOVE    = 0x0002;
            const uint SWP_NOZORDER  = 0x0004;
            const uint SWP_FRAMECHANGED = 0x0020;

            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style &= ~WS_CAPTION;
            style &= ~WS_BORDER;
            style &= ~WS_THICKFRAME;
            style |= WS_SYSMENU;
            style |= WS_MINIMIZEBOX;

            _ = SetWindowLongPtr(hwnd, GWL_STYLE, new nint(style));
            _ = SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        private static void HideWindowUntilSplashIsReady(nint hwnd)
        {
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(exStyle | WS_EX_LAYERED));
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        }

        private static void ClearLayeredWindow(nint hwnd)
        {
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(exStyle & ~WS_EX_LAYERED));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }
}
