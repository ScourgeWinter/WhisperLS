using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Diagnostics;
using System.Text;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FasterWhisperGui.Views
{
    public sealed partial class MainPage : Page
    {
        private static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".wma"
        };

        private static readonly string[] VideoExtensions =
        {
            ".mp4", ".mov", ".mkv", ".avi", ".webm"
        };

        private static readonly string[] CuratedModels =
        {
            "tiny",
            "base",
            "small",
            "medium",
            "large-v3-turbo"
        };

        private string? selectedAudioFile;
        private string? selectedModel;
        private string? lastOutputDirectory;
        private bool selectedModelIsDownloaded;
        private CancellationTokenSource? _analyzeCts;
        private Process? _activeProcess;

        public MainPage()
        {
            this.InitializeComponent();

            Loaded += MainPage_Loaded;
            SizeChanged += MainPage_SizeChanged;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigureWindowChrome();
            ApplyAdaptiveLayout(RootGrid.ActualWidth);
            await PlayPageEntranceAsync();
        }

        private void MainPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyAdaptiveLayout(e.NewSize.Width);
        }

        private void ConfigureWindowChrome()
        {
            if (App.MainWindow is null)
            {
                return;
            }

            App.MainWindow.SetTitleBar(DragRegion);

            if (App.MainWindow.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = true;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            App.SuppressNativeChrome();
        }

        private void ApplyAdaptiveLayout(double width)
        {
            if (width <= 0)
            {
                return;
            }

            ShellPanel.Width = width < 700 ? double.NaN : 620;
            ShellPanel.MaxWidth = width < 700 ? Math.Max(320, width - 32) : 620;
        }

        private async void BrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileStatusText.Text = "Apro il selettore file...";

                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.VideosLibrary
                };

                foreach (var extension in AudioExtensions.Concat(VideoExtensions))
                {
                    picker.FileTypeFilter.Add(extension);
                }

                picker.FileTypeFilter.Add("*");

                if (App.MainWindow is null)
                {
                    FileStatusText.Text = "Finestra principale non disponibile.";
                    return;
                }

                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    FileStatusText.Text = "";
                    return;
                }

                selectedAudioFile = file.Path;
                selectedModel = null;
                selectedModelIsDownloaded = false;
                lastOutputDirectory = null;
                FileNameText.Text = Path.GetFileName(selectedAudioFile);
                FileNameText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 234, 237));
                FileStatusText.Text = "File pronto.";
                ModelSlider.Value = 0;
                SelectedModelText.Text = CuratedModels[0];
                ResetAnalysis();
                OpenOutputButton.Visibility = Visibility.Collapsed;
                SetOperationStatus("");

                await RevealAsync(ModelSelectorPanel, ModelSelectorTransform);
                selectedModel = CuratedModels[0];
                await AnalyzeSelectionAsync();
            }
            catch (Exception ex)
            {
                FileStatusText.Text = $"Impossibile aprire il selettore file: {ex.Message}";
            }
        }

        private async void ModelSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (SelectedModelText is null)
            {
                return;
            }

            try
            {
                var index = Math.Clamp((int)Math.Round(e.NewValue), 0, CuratedModels.Length - 1);
                var model = CuratedModels[index];
                selectedModel = model;
                selectedModelIsDownloaded = false;
                SelectedModelText.Text = model;
                OpenOutputButton.Visibility = Visibility.Collapsed;
                SetOperationStatus("");

                if (!string.IsNullOrWhiteSpace(selectedAudioFile) && ModelSelectorPanel.Visibility == Visibility.Visible)
                {
                    await AnalyzeSelectionAsync();
                }
            }
            catch (Exception ex)
            {
                SetOperationStatus($"Errore analisi: {ex.Message}");
            }
        }

        private async Task AnalyzeSelectionAsync()
        {
            _analyzeCts?.Cancel();
            _analyzeCts?.Dispose();
            var cts = new CancellationTokenSource();
            _analyzeCts = cts;
            var token = cts.Token;

            if (string.IsNullOrWhiteSpace(selectedAudioFile) || string.IsNullOrWhiteSpace(selectedModel))
            {
                return;
            }

            try
            {
                ResetAnalysis();
                await RevealAsync(AnalysisPanel, AnalysisTransform);
                token.ThrowIfCancellationRequested();
                AnalysisStatusText.Text = "Analisi del file e del computer...";

                AnalysisProgressBar.Value = 12;
                await Task.Delay(180, token);

                var duration = await TryGetMediaDurationAsync(selectedAudioFile);
                token.ThrowIfCancellationRequested();
                var durationWasDetected = duration > TimeSpan.Zero;
                if (!durationWasDetected)
                {
                    duration = TimeSpan.FromMinutes(10);
                }

                AnalysisProgressBar.Value = 42;
                await Task.Delay(180, token);

                var hardwareProfile = await DetectHardwareProfileAsync();
                token.ThrowIfCancellationRequested();
                var estimate = BuildEstimate(selectedModel, duration, hardwareProfile);
                QualityStarsText.Text = ToStars(estimate.QualityStars);
                TimeStarsText.Text = ToStars(estimate.TimeStars);
                EstimatedTimeText.Text = $"~{FormatDuration(estimate.EstimatedDuration)}{(durationWasDetected ? string.Empty : " circa")}";

                AnalysisProgressBar.Value = 72;
                AnalysisStatusText.Text = "Controllo se il modello è già disponibile...";
                selectedModelIsDownloaded = await CheckModelDownloadedAsync(selectedModel);
                token.ThrowIfCancellationRequested();

                AnalysisProgressBar.Value = 100;
                AnalysisStatusText.Text = selectedModelIsDownloaded
                    ? "Analisi completata. Il modello è già pronto."
                    : "Analisi completata. Il modello deve essere scaricato.";

                PrimaryActionButton.Content = selectedModelIsDownloaded
                    ? "Elabora la traccia audio"
                    : "Scarica il modello di whisper";

                await RevealAsync(ActionPanel, ActionTransform);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(selectedModel))
                {
                    SetOperationStatus("Seleziona un modello prima di continuare.");
                    return;
                }

                if (!selectedModelIsDownloaded)
                {
                    await DownloadSelectedModelAsync();
                    return;
                }

                await TranscribeSelectedFileAsync();
            }
            catch (Exception ex)
            {
                SetBusy(false);
                SetOperationStatus($"Errore: {ex.Message}");
            }
        }

        private async Task DownloadSelectedModelAsync()
        {
            if (string.IsNullOrWhiteSpace(selectedModel))
            {
                return;
            }

            SetBusy(true, "Download modello in corso...");
            var result = await RunWhisperCommandAsync(
                new[] { "--download-model", selectedModel },
                line => SetOperationStatus(line));

            SetBusy(false);
            if (result.ExitCode != 0)
            {
                SetOperationStatus($"Download non completato. {result.LastLine}");
                return;
            }

            selectedModelIsDownloaded = true;
            PrimaryActionButton.Content = "Elabora la traccia audio";
            SetOperationStatus("Modello scaricato. Puoi avviare la sbobinatura.");
        }

        private async Task TranscribeSelectedFileAsync()
        {
            if (string.IsNullOrWhiteSpace(selectedAudioFile) || !File.Exists(selectedAudioFile))
            {
                SetOperationStatus("Seleziona un file valido prima di avviare la sbobinatura.");
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedModel))
            {
                SetOperationStatus("Seleziona un modello prima di avviare la sbobinatura.");
                return;
            }

            SetBusy(true, "Sbobinatura in corso...");
            OpenOutputButton.Visibility = Visibility.Collapsed;

            var result = await RunWhisperCommandAsync(
                new[]
                {
                    selectedAudioFile,
                    selectedModel,
                    "--device",
                    "auto",
                    "--language",
                    "auto",
                    "--beam-size",
                    "5"
                },
                line => SetOperationStatus(line));

            SetBusy(false);
            if (result.ExitCode != 0)
            {
                SetOperationStatus($"Sbobinatura non completata. {result.LastLine}");
                return;
            }

            RegisterOutputFiles();
            OpenOutputButton.Visibility = Visibility.Visible;
            SetOperationStatus("Sbobinatura completata.");
        }

        private async Task<bool> CheckModelDownloadedAsync(string model)
        {
            var result = await RunWhisperCommandAsync(
                new[] { "--check-model", model },
                _ => { });
            return result.ExitCode == 0;
        }

        private async Task<WhisperCommandResult> RunWhisperCommandAsync(
            IEnumerable<string> arguments,
            Action<string> onOutputLine)
        {
            var repositoryRoot = FindRepositoryRoot();
            if (repositoryRoot is null)
            {
                return new WhisperCommandResult(1, "Whisper.py non trovato.");
            }

            var pythonExecutable = FindPythonExecutable(repositoryRoot);
            var scriptPath = Path.Combine(repositoryRoot, "Whisper.py");
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                WorkingDirectory = repositoryRoot,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var lastLine = string.Empty;
            try
            {
                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        lastLine = args.Data!;
                        DispatcherQueue.TryEnqueue(() => onOutputLine(args.Data!));
                    }
                };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        lastLine = args.Data!;
                        DispatcherQueue.TryEnqueue(() => onOutputLine(args.Data!));
                    }
                };

                if (!process.Start())
                {
                    return new WhisperCommandResult(1, "Processo non avviato.");
                }

                _activeProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                return new WhisperCommandResult(process.ExitCode, lastLine);
            }
            catch (Exception ex)
            {
                return new WhisperCommandResult(1, ex.Message);
            }
            finally
            {
                _activeProcess = null;
            }
        }

        private static AnalysisEstimate BuildEstimate(string model, TimeSpan mediaDuration, HardwareProfile hardwareProfile)
        {
            var qualityStars = model switch
            {
                "tiny" => 2,
                "base" => 3,
                "small" => 4,
                "medium" => 4,
                "large-v3-turbo" => 5,
                _ => 3
            };

            var realTimeFactor = hardwareProfile.HasCudaGpu
                ? model switch
                {
                    "tiny" => 0.04,
                    "base" => 0.06,
                    "small" => 0.12,
                    "medium" => hardwareProfile.GpuMemoryGb >= 10 ? 0.16 : 0.28,
                    "large-v3-turbo" => hardwareProfile.GpuMemoryGb >= 10 ? 0.24 : 0.38,
                    _ => 0.25
                }
                : model switch
                {
                    "tiny" => 0.35,
                    "base" => 0.60,
                    "small" => 1.05,
                    "medium" => 1.65,
                    "large-v3-turbo" => 1.30,
                    _ => 1.0
                };

            var cpuFactor = hardwareProfile.HasCudaGpu
                ? 1.0
                : Environment.ProcessorCount switch
                {
                    >= 16 => 0.70,
                    >= 12 => 0.82,
                    >= 8 => 1.0,
                    >= 6 => 1.15,
                    _ => 1.35
                };

            var estimatedSeconds = Math.Max(8, mediaDuration.TotalSeconds * realTimeFactor * cpuFactor);
            var estimatedDuration = TimeSpan.FromSeconds(estimatedSeconds);
            var timeStars = estimatedDuration.TotalMinutes switch
            {
                <= 5 => 5,
                <= 15 => 4,
                <= 45 => 3,
                <= 90 => 2,
                _ => 1
            };

            return new AnalysisEstimate(qualityStars, timeStars, estimatedDuration);
        }

        private static async Task<HardwareProfile> DetectHardwareProfileAsync()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--query-gpu=memory.total");
                startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return new HardwareProfile(false, 0);
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    return new HardwareProfile(false, 0);
                }

                var firstLine = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (double.TryParse(firstLine, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var memoryMiB))
                {
                    return new HardwareProfile(true, memoryMiB / 1024d);
                }
            }
            catch
            {
            }

            return new HardwareProfile(false, 0);
        }

        private static async Task<TimeSpan> TryGetMediaDurationAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var extension = Path.GetExtension(path).ToLowerInvariant();

                if (VideoExtensions.Contains(extension))
                {
                    var videoProps = await file.Properties.GetVideoPropertiesAsync();
                    if (videoProps.Duration > TimeSpan.Zero)
                    {
                        return videoProps.Duration;
                    }
                    var musicPropsFromVideo = await file.Properties.GetMusicPropertiesAsync();
                    return musicPropsFromVideo.Duration;
                }
                else
                {
                    var musicProps = await file.Properties.GetMusicPropertiesAsync();
                    if (musicProps.Duration > TimeSpan.Zero)
                    {
                        return musicProps.Duration;
                    }
                    var videoPropsFromAudio = await file.Properties.GetVideoPropertiesAsync();
                    return videoPropsFromAudio.Duration;
                }
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private async Task RevealAsync(UIElement element, TranslateTransform transform)
        {
            element.Visibility = Visibility.Visible;
            element.Opacity = 0;
            transform.Y = 14;

            var storyboard = new Storyboard();
            var opacityAnimation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220)
            };
            Storyboard.SetTarget(opacityAnimation, element);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

            var translateAnimation = new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220)
            };
            Storyboard.SetTarget(translateAnimation, transform);
            Storyboard.SetTargetProperty(translateAnimation, "Y");

            storyboard.Children.Add(opacityAnimation);
            storyboard.Children.Add(translateAnimation);

            var completion = new TaskCompletionSource();
            storyboard.Completed += (_, _) => completion.TrySetResult();
            storyboard.Begin();
            await completion.Task;
        }

        private async Task PlayPageEntranceAsync()
        {
            RootGrid.Opacity = 0;
            MainContentTransform.Y = 10;

            var storyboard = new Storyboard();

            var opacityAnimation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacityAnimation, RootGrid);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

            var translateAnimation = new DoubleAnimation
            {
                From = 10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(translateAnimation, MainContentTransform);
            Storyboard.SetTargetProperty(translateAnimation, "Y");

            storyboard.Children.Add(opacityAnimation);
            storyboard.Children.Add(translateAnimation);

            var completion = new TaskCompletionSource();
            storyboard.Completed += (_, _) => completion.TrySetResult();
            storyboard.Begin();
            await completion.Task;
        }

        private void ResetAnalysis()
        {
            AnalysisPanel.Visibility = Visibility.Collapsed;
            ActionPanel.Visibility = Visibility.Collapsed;
            AnalysisProgressBar.Value = 0;
            AnalysisStatusText.Text = "Analisi in corso...";
            QualityStarsText.Text = "-----";
            TimeStarsText.Text = "-----";
            EstimatedTimeText.Text = string.Empty;
        }

        private void SetBusy(bool isBusy, string? status = null)
        {
            PrimaryActionButton.IsEnabled = !isBusy;
            ModelSlider.IsEnabled = !isBusy;

            AnalysisProgressBar.IsIndeterminate = isBusy;
            if (!string.IsNullOrWhiteSpace(status))
            {
                SetOperationStatus(status);
            }
        }

        private void SetOperationStatus(string message)
        {
            OperationStatusText.Text = message;
        }

        private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(lastOutputDirectory) || !Directory.Exists(lastOutputDirectory))
            {
                SetOperationStatus("La cartella output non è ancora disponibile.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = lastOutputDirectory,
                UseShellExecute = true
            });
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try { _activeProcess?.Kill(entireProcessTree: true); } catch { }
            App.MainWindow?.Close();
        }

        private void RegisterOutputFiles()
        {
            if (string.IsNullOrWhiteSpace(selectedAudioFile))
            {
                return;
            }

            var inputPath = Path.GetFullPath(selectedAudioFile);
            lastOutputDirectory = Path.GetDirectoryName(inputPath);
        }

        private static string ToStars(int count)
        {
            count = Math.Clamp(count, 1, 5);
            return new string('★', count) + new string('☆', 5 - count);
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
            }

            if (duration.TotalMinutes >= 1)
            {
                return $"{duration.Minutes}m {duration.Seconds}s";
            }

            return $"{Math.Max(1, duration.Seconds)}s";
        }

        private static string? FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Whisper.py")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static string FindPythonExecutable(string repositoryRoot)
        {
            var localVenvPython = Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe");
            return File.Exists(localVenvPython) ? localVenvPython : "python";
        }

        private sealed record AnalysisEstimate(int QualityStars, int TimeStars, TimeSpan EstimatedDuration);

        private sealed record HardwareProfile(bool HasCudaGpu, double GpuMemoryGb);

        private sealed record WhisperCommandResult(int ExitCode, string LastLine);
    }
}
