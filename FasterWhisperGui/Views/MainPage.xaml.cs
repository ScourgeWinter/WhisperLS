using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FasterWhisperGui.Services;
using Windows.Storage;
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

        private static readonly string[] SupportedModels =
        {
            "tiny", "tiny.en", "base", "base.en", "small", "small.en",
            "medium", "medium.en", "large-v1", "large-v2", "large-v3",
            "large", "large-v3-turbo", "turbo", "distil-small.en",
            "distil-medium.en", "distil-large-v2", "distil-large-v3",
            "distil-large-v3.5"
        };

        private const string ProgressPrefix = "__WHISPER_PROGRESS__ ";
        private const string ResultPrefix = "__WHISPER_RESULT__ ";
        private static readonly string AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperLS");
        private static readonly string PendingQueuePath = Path.Combine(
            AppDataDirectory,
            "pending-queue.json");
        private readonly SemaphoreSlim processGate = new(1, 1);
        private readonly object processSync = new();
        private readonly Queue<int> logEntryLengths = new();
        private readonly List<ModelOption> modelOptions = new();
        private readonly List<TranscriptionQueueJob> transcriptionQueue = new();
        private readonly DispatcherTimer settingsInfoTimer = new();
        private readonly UpdateService updateService = new();
        private CancellationTokenSource? analysisCts;
        private CancellationTokenSource? operationCts;
        private Process? activeProcess;
        private string? selectedAudioFile;
        private readonly List<string> selectedAudioFiles = new();
        private string? selectedOutputDirectory;
        private string? lastOutputDirectory;
        private string? recoverableCheckpointPath;
        private bool selectedModelIsDownloaded;
        private bool initialized;
        private bool consoleIsOpen;
        private bool settingsDrawerOpen;
        private bool modelManagerOpen;
        private bool modelManagerDeletionInProgress;
        private bool entranceAnimationStarted;
        private bool closeAnimationStarted;
        private bool closeAnimationComplete;
        private Storyboard? consoleStoryboard;
        private Storyboard? settingsDrawerStoryboard;
        private Storyboard? modelManagerStoryboard;
        private Storyboard? queueManagerStoryboard;
        private Storyboard? windowTransitionStoryboard;
        private CancellationTokenSource? modelManagerCts;
        private CancellationTokenSource? modelStatusCts;
        private HardwareProfile? cachedHardwareProfile;
        private Button? pendingInfoButton;
        private int logCharacterCount;
        private bool consoleShouldFollowTail = true;
        private string consoleProgressUnitLabel = "Parte";
        private string? currentBatchJobId;
        private bool queueManagerOpen;
        private List<TranscriptionQueueJob>? queueManagerWorkingQueue;
        private ObservableCollection<QueueManagerItem>? queueManagerItems;
        private string? queuePointerDragJobKey;
        private FrameworkElement? queuePointerDragHandle;
        private ListViewItem? queuePointerDragContainer;
        private FrameworkElement? queuePointerDragVisual;
        private CompositeTransform? queuePointerDragTransform;
        private readonly Dictionary<ListViewItem, (FrameworkElement Visual, CompositeTransform Transform)>
            queuePointerSiblingTransforms = new();
        private double queuePointerDragOffsetY;
        private double queuePointerLastY;
        private int queuePointerDragOriginIndex = -1;
        private int queuePointerDragTargetIndex = -1;
        private uint queuePointerId;

        public MainPage()
        {
            InitializeComponent();
            settingsInfoTimer.Interval = TimeSpan.FromMilliseconds(1200);
            settingsInfoTimer.Tick += SettingsInfoTimer_Tick;
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is not null)
            {
                App.MainWindow.SetTitleBar(TitleBarDragRegion);
                App.MainWindow.AppWindow.Changed -= AppWindow_Changed;
                App.MainWindow.AppWindow.Changed += AppWindow_Changed;
                UpdateMaximizeButton();
            }

            if (!entranceAnimationStarted)
            {
                entranceAnimationStarted = true;
                AnimateWindowIn();
            }

            if (initialized)
            {
                return;
            }

            modelOptions.AddRange(SupportedModels.Select(model => new ModelOption(model)));
            ModelComboBox.ItemsSource = modelOptions;
            ModelComboBox.SelectedItem = modelOptions.First(option => option.Name == "medium");
            UpdateModelSelectorBorderState();
            LanguageComboBox.SelectedIndex = 0;
            DeviceComboBox.SelectedIndex = 0;
            initialized = true;
            if (File.Exists(PendingQueuePath))
            {
                ActionPanel.Visibility = Visibility.Visible;
                PrimaryActionButton.Visibility = Visibility.Collapsed;
                RestoreQueueButton.Visibility = Visibility.Visible;
            }
            UpdateSettingsSummary();
            UpdateService.ConfirmSuccessfulStartup();
            _ = CheckForUpdatesAsync(manual: false);
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(manual: true);
        }

        private async Task CheckForUpdatesAsync(bool manual)
        {
            CheckUpdatesButton.IsEnabled = false;
            try
            {
                var candidate = await updateService.CheckAsync(manual, CancellationToken.None);
                if (candidate is null)
                {
                    if (manual)
                    {
                        ShowStatus(
                            $"WhisperLS {UpdateService.InstalledVersion.ToString(3)} è aggiornato.",
                            InfoBarSeverity.Success);
                    }
                    return;
                }
                await ShowUpdateDialogAsync(candidate);
            }
            catch (Exception exception)
            {
                AppendConsoleEvent("AVVISO", $"Controllo aggiornamenti non riuscito: {exception.Message}", "LogWarningBrush");
                if (manual)
                {
                    ShowStatus("Impossibile controllare gli aggiornamenti. Verifica la connessione.", InfoBarSeverity.Warning);
                }
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private async Task ShowUpdateDialogAsync(UpdateCandidate candidate)
        {
            var ignoreCheckBox = new CheckBox
            {
                Content = "Non ricordarmelo più per questa versione",
                Visibility = candidate.CancelCount >= 3 ? Visibility.Visible : Visibility.Collapsed
            };
            var changelog = new Expander
            {
                Header = "Novità della versione",
                Content = new ScrollViewer
                {
                    MaxHeight = 180,
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(candidate.Changelog)
                            ? "Consulta la release GitHub per i dettagli."
                            : candidate.Changelog,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        IsTextSelectionEnabled = true
                    }
                }
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = $"È disponibile WhisperLS {candidate.Version}. Hai installato la versione {UpdateService.InstalledVersion.ToString(3)}.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            content.Children.Add(changelog);
            content.Children.Add(ignoreCheckBox);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Aggiornamento disponibile",
                Content = content,
                PrimaryButtonText = "Download",
                CloseButtonText = "Annulla",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await updateService.RegisterCancellationAsync(
                    candidate.Version,
                    ignoreCheckBox.IsChecked == true,
                    CancellationToken.None);
                ShowStatus(
                    "L'aggiornamento verrà ricordato alla prossima apertura.",
                    InfoBarSeverity.Informational);
                return;
            }
            await DownloadAndApplyUpdateAsync(candidate);
        }

        private async Task DownloadAndApplyUpdateAsync(UpdateCandidate candidate)
        {
            using var downloadCts = new CancellationTokenSource();
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100 };
            var progressText = new TextBlock { Text = "Preparazione del download..." };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = $"Download di WhisperLS {candidate.Version}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            content.Children.Add(progressBar);
            content.Children.Add(progressText);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Aggiornamento di WhisperLS",
                Content = content,
                CloseButtonText = "Annulla"
            };
            dialog.Closing += (_, args) =>
            {
                if (args.Result == ContentDialogResult.None && downloadCts.IsCancellationRequested)
                {
                    return;
                }
                if (args.Result == ContentDialogResult.None || args.Result == ContentDialogResult.Secondary)
                {
                    downloadCts.Cancel();
                }
            };
            var dialogTask = dialog.ShowAsync().AsTask();
            PreparedUpdate? prepared = null;
            try
            {
                prepared = await updateService.DownloadAsync(
                    candidate,
                    new Progress<UpdateDownloadProgress>(value =>
                    {
                        progressBar.Value = value.Percent;
                        progressBar.IsIndeterminate = value.TotalBytes <= 0;
                        progressText.Text = value.TotalBytes <= 0
                            ? $"{FormatBytes(value.DownloadedBytes)} scaricati"
                            : $"{value.Percent:0}% · {FormatBytes(value.DownloadedBytes)} / {FormatBytes(value.TotalBytes)}";
                    }),
                    downloadCts.Token);
                progressBar.IsIndeterminate = false;
                progressBar.Value = 100;
                progressText.Text = "Download completato e verificato.";
                dialog.CloseButtonText = string.Empty;
                dialog.PrimaryButtonText = "Riavvia WhisperLS per rendere effettive le modifiche";
                dialog.IsPrimaryButtonEnabled = true;
                dialog.DefaultButton = ContentDialogButton.Primary;
            }
            catch (OperationCanceledException)
            {
                dialog.Hide();
                ShowStatus("Download dell'aggiornamento annullato.", InfoBarSeverity.Warning);
                return;
            }
            catch (Exception exception)
            {
                dialog.Hide();
                ShowStatus($"Aggiornamento non scaricato: {exception.Message}", InfoBarSeverity.Error);
                return;
            }

            var result = await dialogTask;
            if (result == ContentDialogResult.Primary && prepared is not null)
            {
                while (activeProcess is not null)
                {
                    progressText.Text = "Attendo il completamento dell'operazione in corso...";
                    await Task.Delay(500);
                }
                updateService.ApplyAndRestart(prepared);
            }
        }

        private static string FormatBytes(long value)
        {
            if (value >= 1024L * 1024 * 1024)
            {
                return $"{value / (1024d * 1024 * 1024):0.0} GB";
            }
            return value >= 1024L * 1024
                ? $"{value / (1024d * 1024):0.0} MB"
                : $"{value / 1024d:0.0} KB";
        }

        private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
        }

        private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else
            {
                presenter.Maximize();
            }
        }

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
        {
            RequestAnimatedClose();
        }

        public bool RequestAnimatedClose()
        {
            if (closeAnimationComplete)
            {
                return false;
            }

            if (!closeAnimationStarted)
            {
                closeAnimationStarted = true;
                WindowShell.IsHitTestVisible = false;
                AnimateWindowOut();
            }
            return true;
        }

        private void AnimateWindowIn()
        {
            StartWindowFade(
                targetOpacity: 1,
                durationMilliseconds: 240,
                easingMode: EasingMode.EaseOut,
                completed: null);
        }

        private void AnimateWindowOut()
        {
            StartWindowFade(
                targetOpacity: 0,
                durationMilliseconds: 180,
                easingMode: EasingMode.EaseIn,
                completed: () =>
                {
                    closeAnimationComplete = true;
                    CancelActiveOperation();
                    App.MainWindow?.Close();
                });
        }

        private void StartWindowFade(
            double targetOpacity,
            int durationMilliseconds,
            EasingMode easingMode,
            Action? completed)
        {
            var currentOpacity = WindowShell.Opacity;
            windowTransitionStoryboard?.Stop();

            var duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds));
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                From = currentOpacity,
                To = targetOpacity,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = easingMode }
            };
            Storyboard.SetTarget(animation, WindowShell);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);

            WindowShell.Opacity = targetOpacity;
            windowTransitionStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(windowTransitionStoryboard, storyboard))
                {
                    return;
                }

                storyboard.Stop();
                WindowShell.Opacity = targetOpacity;
                windowTransitionStoryboard = null;
                completed?.Invoke();
            };
            storyboard.Begin();
        }

        private void ToggleConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateConsole(!consoleIsOpen);
        }

        private void OpenSettingsDrawerButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateSettingsDrawer(open: true);
        }

        private async void OpenModelManagerButton_Click(object sender, RoutedEventArgs e)
        {
            if (settingsDrawerOpen)
            {
                AnimateSettingsDrawer(open: false);
            }

            AnimateModelManager(open: true);
            await RefreshModelManagerAsync();
        }

        private void CloseModelManagerButton_Click(object sender, RoutedEventArgs e)
        {
            CloseModelManager();
        }

        private void ModelManagerOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseModelManager();
        }

        private void ModelManagerPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void RefreshModelManagerButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshModelManagerAsync();
        }

        private async void ModelManagerActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string model } ||
                ModelManagerListView.ItemsSource is not IEnumerable<ModelManagerItem> items)
            {
                return;
            }

            var item = items.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelName, model, StringComparison.Ordinal));
            if (item is null)
            {
                return;
            }

            if (item.IsInstalled)
            {
                if (!await ConfirmModelUninstallAsync(model))
                {
                    return;
                }

                modelManagerCts?.Cancel();
                modelManagerCts?.Dispose();
                var deletionCts = new CancellationTokenSource();
                modelManagerCts = deletionCts;
                modelManagerDeletionInProgress = true;
                try
                {
                    SetModelManagerBusy(true, $"Disinstallazione di {model}...");
                    ModelManagerActivityProgressBar.IsIndeterminate = true;
                    var result = await RunWhisperCommandAsync(
                        new[] { "--delete-model", model },
                        AppendBackendLog,
                        null,
                        deletionCts.Token);
                    EnsureSuccess(result, "Disinstallazione");

                    var affectedAliases = GetModelAliases(model);
                    if (affectedAliases.Contains(
                            GetSelectedModel(),
                            StringComparer.Ordinal))
                    {
                        selectedModelIsDownloaded = false;
                        PrimaryActionButton.Content = "Scarica modello";
                    }

                    ModelManagerActivityText.Text = $"{model} è stato disinstallato.";
                    AppendLog($"Modello disinstallato: {model}.");
                    await LoadModelManagerAsync(deletionCts.Token);
                }
                catch (OperationCanceledException)
                {
                    ModelManagerActivityText.Text = "Disinstallazione interrotta.";
                }
                catch (Exception ex)
                {
                    ModelManagerActivityText.Text = $"Disinstallazione non riuscita: {ex.Message}";
                    AppendLog($"Errore Model Manager: {ex.Message}");
                }
                finally
                {
                    modelManagerDeletionInProgress = false;
                    if (ReferenceEquals(modelManagerCts, deletionCts))
                    {
                        SetModelManagerBusy(false);
                        deletionCts.Dispose();
                        modelManagerCts = null;
                    }
                }
                return;
            }

            modelManagerCts?.Cancel();
            modelManagerCts?.Dispose();
            var localCts = new CancellationTokenSource();
            modelManagerCts = localCts;

            try
            {
                SetModelManagerBusy(true, $"Download di {model}...");
                ModelManagerActivityProgressBar.IsIndeterminate = true;
                var result = await RunWhisperCommandAsync(
                    new[] { "--download-model", model },
                    AppendBackendLog,
                    progress =>
                    {
                        ModelManagerActivityProgressBar.IsIndeterminate = false;
                        ModelManagerActivityProgressBar.Value = progress.Percent;
                        ModelManagerActivityText.Text =
                            $"Download di {model}: {progress.Percent:0}% · " +
                            FormatProgressBytes(progress.DownloadedBytes, progress.TotalBytes);
                    },
                    localCts.Token);

                EnsureSuccess(result, "Download");
                ModelManagerActivityText.Text = $"{model} è stato scaricato e verificato.";
                await LoadModelManagerAsync(localCts.Token);
                if (string.Equals(GetSelectedModel(), model, StringComparison.Ordinal))
                {
                    selectedModelIsDownloaded = true;
                    PrimaryActionButton.Content = GetIdlePrimaryActionText();
                }
            }
            catch (OperationCanceledException)
            {
                ModelManagerActivityText.Text = "Download interrotto.";
            }
            catch (Exception ex)
            {
                ModelManagerActivityText.Text = $"Download non riuscito: {ex.Message}";
                AppendLog($"Errore Model Manager: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(modelManagerCts, localCts))
                {
                    SetModelManagerBusy(false);
                    modelManagerCts.Dispose();
                    modelManagerCts = null;
                }
            }
        }

        private void CloseSettingsDrawerButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateSettingsDrawer(open: false);
        }

        private void SettingsDrawerOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSettingsDrawer(open: false);
        }

        private void SettingsDrawer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void CloseModelManager()
        {
            if (modelManagerDeletionInProgress)
            {
                ModelManagerActivityText.Text =
                    "Attendi il completamento della disinstallazione.";
                return;
            }
            modelManagerCts?.Cancel();
            AnimateModelManager(open: false);
        }

        private async Task<bool> ConfirmModelUninstallAsync(string model)
        {
            var aliases = GetModelAliases(model);
            var aliasNotice = aliases.Length > 1
                ? $" Verranno rimossi anche gli alias collegati: {string.Join(", ", aliases.Where(alias => alias != model))}."
                : string.Empty;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Disinstallare {model}?",
                Content =
                    $"Il modello verrà eliminato dalla cache locale e dovrà essere scaricato nuovamente per riutilizzarlo.{aliasNotice}",
                PrimaryButtonText = "Disinstalla",
                CloseButtonText = "Annulla",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static string[] GetModelAliases(string model)
        {
            return model switch
            {
                "large" or "large-v3" => new[] { "large", "large-v3" },
                "large-v3-turbo" or "turbo" => new[] { "large-v3-turbo", "turbo" },
                _ => new[] { model }
            };
        }

        private async Task RefreshModelManagerAsync()
        {
            modelManagerCts?.Cancel();
            modelManagerCts?.Dispose();
            var localCts = new CancellationTokenSource();
            modelManagerCts = localCts;

            try
            {
                await LoadModelManagerAsync(localCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ModelManagerSummaryText.Text = "Impossibile leggere lo stato dei modelli.";
                ModelManagerActivityText.Text = ex.Message;
                AppendLog($"Errore Model Manager: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(modelManagerCts, localCts))
                {
                    SetModelManagerBusy(false);
                    modelManagerCts.Dispose();
                    modelManagerCts = null;
                }
            }
        }

        private async Task LoadModelManagerAsync(CancellationToken token)
        {
            SetModelManagerBusy(true, "Controllo dei modelli locali...");
            var hardwareTask = cachedHardwareProfile is null
                ? DetectHardwareProfileAsync(token)
                : Task.FromResult(cachedHardwareProfile);
            var statusTask = GetModelStatusesAsync(token);

            await Task.WhenAll(hardwareTask, statusTask);
            token.ThrowIfCancellationRequested();

            var hardware = await hardwareTask;
            cachedHardwareProfile = hardware;
            var statuses = await statusTask;
            UpdateModelOptionStatuses(statuses);

            ModelManagerHardwareText.Text = hardware.HasNvidiaGpu
                ? $"GPU NVIDIA · {hardware.GpuMemoryGb:0.#} GB VRAM · {hardware.SystemMemoryGb:0.#} GB RAM · {hardware.LogicalProcessors} thread"
                : $"CPU · {hardware.LogicalProcessors} thread · {hardware.SystemMemoryGb:0.#} GB RAM · GPU NVIDIA non rilevata";

            var models = SupportedModels
                .Select(model =>
                {
                    var installed = statuses.TryGetValue(model, out var value) && value;
                    return CreateModelManagerItem(model, installed, hardware);
                })
                .OrderByDescending(item => item.IsInstalled)
                .ThenByDescending(item => item.Score)
                .ThenBy(item => item.ModelName, StringComparer.Ordinal)
                .ToArray();

            ModelManagerListView.ItemsSource = models;
            var installedCount = models.Count(item => item.IsInstalled);
            ModelManagerSummaryText.Text =
                $"{installedCount} presenti · {models.Length - installedCount} non presenti · ordinati per stato e appropriatezza";
            ModelManagerActivityText.Text = "Stima indicativa: non è un benchmark.";
        }

        private async Task<Dictionary<string, bool>> GetModelStatusesAsync(CancellationToken token)
        {
            var result = await RunWhisperCommandAsync(
                new[] { "--model-status-json" },
                _ => { },
                null,
                token);
            EnsureSuccess(result, "Controllo modelli");
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(result.LastLine)
                ?? throw new InvalidOperationException(
                    "Il backend ha restituito uno stato modelli non valido.");
        }

        private void UpdateModelOptionStatuses(IReadOnlyDictionary<string, bool> statuses)
        {
            foreach (var option in modelOptions)
            {
                option.SetInstalled(
                    statuses.TryGetValue(option.Name, out var installed) && installed);
            }
            UpdateModelSelectorBorderState();
        }

        private async Task RefreshModelOptionStatusesAsync()
        {
            modelStatusCts?.Cancel();
            modelStatusCts?.Dispose();
            var localCts = new CancellationTokenSource();
            modelStatusCts = localCts;
            try
            {
                AnalysisStatusText.Text = "Aggiornamento dei modelli locali...";
                var statuses = await GetModelStatusesAsync(localCts.Token);
                UpdateModelOptionStatuses(statuses);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                AppendLog($"Impossibile aggiornare lo stato dei modelli: {exception.Message}");
            }
            finally
            {
                if (ReferenceEquals(modelStatusCts, localCts))
                {
                    localCts.Dispose();
                    modelStatusCts = null;
                }
            }
        }

        private void SetModelManagerBusy(bool busy, string? message = null)
        {
            ModelManagerListView.IsEnabled = !busy;
            RefreshModelManagerButton.IsEnabled = !busy;
            ModelManagerProgressRing.IsActive = busy;
            ModelManagerProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ModelManagerActivityProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy)
            {
                ModelManagerActivityProgressBar.IsIndeterminate = false;
                ModelManagerActivityProgressBar.Value = 0;
            }
            if (message is not null)
            {
                ModelManagerSummaryText.Text = message;
                ModelManagerActivityText.Text = message;
            }
        }

        private ModelManagerItem CreateModelManagerItem(
            string model,
            bool installed,
            HardwareProfile hardware)
        {
            var score = CalculateModelAppropriateness(model, hardware);
            var scoreBrush = GetResourceBrush(
                score >= 80
                    ? "AppropriatenessHighBrush"
                    : score >= 55
                        ? "AppropriatenessMediumBrush"
                        : "AppropriatenessLowBrush");
            var scoreDescription = score switch
            {
                >= 85 => "Eccellente",
                >= 70 => "Adatto",
                >= 55 => "Discreto",
                >= 35 => "Impegnativo",
                _ => "Poco adatto"
            };

            return new ModelManagerItem
            {
                ModelName = model,
                ClassLabel = GetModelClassLabel(model),
                IsInstalled = installed,
                StatusLabel = installed ? "PRESENTE" : "NON PRESENTE",
                StatusBrush = GetResourceBrush(installed ? "StatusSuccessBrush" : "StatusMissingBrush"),
                StatusBackgroundBrush = GetResourceBrush(
                    installed ? "StatusSuccessBackgroundBrush" : "StatusMissingBackgroundBrush"),
                Score = score,
                ScoreLabel = $"{score}% · {scoreDescription}",
                ScoreBrush = scoreBrush,
                DownloadActionVisibility = installed ? Visibility.Collapsed : Visibility.Visible,
                UninstallActionVisibility = installed ? Visibility.Visible : Visibility.Collapsed,
                UninstallAutomationName = $"Disinstalla {model}"
            };
        }

        private static int CalculateModelAppropriateness(string model, HardwareProfile hardware)
        {
            var (requiredVram, requiredRam, cpuCeiling, recommendedThreads) = model switch
            {
                "tiny" or "tiny.en" => (1.5, 3d, 96d, 4d),
                "base" or "base.en" => (2.0, 4d, 90d, 6d),
                "small" or "small.en" or "distil-small.en" => (3.0, 6d, 78d, 8d),
                "medium" or "medium.en" => (5.0, 10d, 62d, 12d),
                "distil-medium.en" => (4.0, 8d, 70d, 10d),
                "large-v3-turbo" or "turbo" => (6.0, 12d, 52d, 14d),
                "distil-large-v2" or "distil-large-v3" or "distil-large-v3.5" => (6.5, 12d, 50d, 16d),
                _ => (8.0, 16d, 44d, 20d)
            };

            var threadRatio = Math.Clamp(hardware.LogicalProcessors / recommendedThreads, 0.2, 1.0);
            var memoryRatio = Math.Clamp(hardware.SystemMemoryGb / requiredRam, 0.2, 1.0);
            double score;
            if (hardware.HasNvidiaGpu)
            {
                var vramRatio = Math.Clamp(hardware.GpuMemoryGb / requiredVram, 0, 1);
                score = 12 + (63 * vramRatio) + (10 * threadRatio) + (15 * memoryRatio);
                if (hardware.GpuMemoryGb < requiredVram * 0.7)
                {
                    score -= 8;
                }
            }
            else
            {
                score = cpuCeiling * (0.38 + (0.37 * threadRatio) + (0.25 * memoryRatio));
            }

            return (int)Math.Round(Math.Clamp(score, 10, 100));
        }

        private static string GetModelClassLabel(string model)
        {
            var language = model.EndsWith(".en", StringComparison.Ordinal) ? "inglese" : "multilingua";
            if (model == "large")
            {
                return "Alias di large-v3 · multilingua";
            }
            if (model == "turbo")
            {
                return "Alias di large-v3-turbo";
            }
            if (model.StartsWith("distil-", StringComparison.Ordinal))
            {
                return $"Distilled · {language}";
            }
            if (model is "large-v3-turbo" or "turbo")
            {
                return "Turbo · multilingua";
            }
            var family = model.Split('-', '.')[0] switch
            {
                "tiny" => "Ultraleggero",
                "base" => "Leggero",
                "small" => "Compatto",
                "medium" => "Bilanciato",
                _ => "Massima accuratezza"
            };
            return $"{family} · {language}";
        }

        private static Brush GetResourceBrush(string key)
        {
            return (Brush)Application.Current.Resources[key];
        }

        private void AnimateModelManager(bool open)
        {
            if (modelManagerOpen == open && modelManagerStoryboard is null)
            {
                return;
            }

            modelManagerStoryboard?.Stop();
            modelManagerOpen = open;
            if (open)
            {
                ModelManagerOverlay.Visibility = Visibility.Visible;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(open ? 180 : 140));
            var easing = new CubicEase
            {
                EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn
            };
            var storyboard = new Storyboard();
            var opacityAnimation = new DoubleAnimation
            {
                From = ModelManagerOverlay.Opacity,
                To = open ? 1 : 0,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacityAnimation, ModelManagerOverlay);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
            storyboard.Children.Add(opacityAnimation);

            foreach (var property in new[] { "ScaleX", "ScaleY" })
            {
                var scaleAnimation = new DoubleAnimation
                {
                    From = property == "ScaleX"
                        ? ModelManagerScaleTransform.ScaleX
                        : ModelManagerScaleTransform.ScaleY,
                    To = open ? 1 : 0.97,
                    Duration = duration,
                    EasingFunction = easing
                };
                Storyboard.SetTarget(scaleAnimation, ModelManagerScaleTransform);
                Storyboard.SetTargetProperty(scaleAnimation, property);
                storyboard.Children.Add(scaleAnimation);
            }

            ModelManagerOverlay.Opacity = open ? 1 : 0;
            ModelManagerScaleTransform.ScaleX = open ? 1 : 0.97;
            ModelManagerScaleTransform.ScaleY = open ? 1 : 0.97;
            modelManagerStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(modelManagerStoryboard, storyboard))
                {
                    return;
                }
                storyboard.Stop();
                if (!open)
                {
                    ModelManagerOverlay.Visibility = Visibility.Collapsed;
                }
                modelManagerStoryboard = null;
            };
            storyboard.Begin();
        }

        private void AnimateQueueManager(bool open)
        {
            if (queueManagerOpen == open && queueManagerStoryboard is null)
            {
                return;
            }

            queueManagerStoryboard?.Stop();
            queueManagerOpen = open;
            if (open)
            {
                QueueManagerOverlay.Visibility = Visibility.Visible;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(open ? 180 : 140));
            var easing = new CubicEase
            {
                EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn
            };
            var storyboard = new Storyboard();
            var opacityAnimation = new DoubleAnimation
            {
                From = QueueManagerOverlay.Opacity,
                To = open ? 1 : 0,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacityAnimation, QueueManagerOverlay);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
            storyboard.Children.Add(opacityAnimation);

            foreach (var property in new[] { "ScaleX", "ScaleY" })
            {
                var scaleAnimation = new DoubleAnimation
                {
                    From = property == "ScaleX"
                        ? QueueManagerScaleTransform.ScaleX
                        : QueueManagerScaleTransform.ScaleY,
                    To = open ? 1 : 0.97,
                    Duration = duration,
                    EasingFunction = easing
                };
                Storyboard.SetTarget(scaleAnimation, QueueManagerScaleTransform);
                Storyboard.SetTargetProperty(scaleAnimation, property);
                storyboard.Children.Add(scaleAnimation);
            }

            QueueManagerOverlay.Opacity = open ? 1 : 0;
            QueueManagerScaleTransform.ScaleX = open ? 1 : 0.97;
            QueueManagerScaleTransform.ScaleY = open ? 1 : 0.97;
            queueManagerStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(queueManagerStoryboard, storyboard))
                {
                    return;
                }
                storyboard.Stop();
                if (!open)
                {
                    QueueManagerOverlay.Visibility = Visibility.Collapsed;
                }
                queueManagerStoryboard = null;
            };
            storyboard.Begin();
        }

        private void SettingInfoIcon_PointerEntered(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            pendingInfoButton = button;
            settingsInfoTimer.Stop();
            settingsInfoTimer.Start();
        }

        private void SettingInfoIcon_PointerExited(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (ReferenceEquals(pendingInfoButton, button))
            {
                settingsInfoTimer.Stop();
                pendingInfoButton = null;
            }
            if (ReferenceEquals(SettingInfoTip.Target, button))
            {
                SettingInfoTip.IsOpen = false;
            }
        }

        private void SettingInfoIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                settingsInfoTimer.Stop();
                ShowSettingInfo(button);
            }
        }

        private void SettingsInfoTimer_Tick(object? sender, object e)
        {
            settingsInfoTimer.Stop();
            if (pendingInfoButton is not null)
            {
                ShowSettingInfo(pendingInfoButton);
            }
        }

        private void ShowSettingInfo(Button button)
        {
            var (title, text) = button.Tag?.ToString() switch
            {
                "device" => (
                    "Dispositivo di elaborazione",
                    "Automatico usa la GPU NVIDIA quando è compatibile e passa alla CPU in caso contrario. " +
                    "La GPU è più veloce; la CPU è più universale ma richiede più tempo."),
                "beam" => (
                    "Beam size",
                    "Determina quante alternative vengono valutate durante la decodifica. " +
                    "Valori più alti possono migliorare alcuni risultati, ma rallentano la trascrizione. 5 è il valore consigliato."),
                "vad" => (
                    "Filtro voce (VAD)",
                    "Rileva e ignora i silenzi prima della trascrizione. Riduce tempi e possibili allucinazioni, " +
                    "ma con audio molto debole può escludere qualche parola."),
                "timestamps" => (
                    "Timestamp per parola",
                    "Calcola l'intervallo temporale di ogni singola parola e produce sottotitoli più granulari. " +
                    "Richiede un po' più di elaborazione."),
                _ => ("Informazioni", "Nessuna descrizione disponibile.")
            };

            pendingInfoButton = null;
            SettingInfoTip.Target = button;
            SettingInfoTip.Title = null;
            SettingInfoTipTitleText.Text = title;
            SettingInfoTipText.Text = text;
            SettingInfoTip.IsOpen = true;
        }

        private void AnimateSettingsDrawer(bool open)
        {
            if (settingsDrawerOpen == open && settingsDrawerStoryboard is null)
            {
                return;
            }

            var currentOpacity = SettingsDrawerOverlay.Opacity;
            var currentOffset = SettingsDrawerTransform.X;
            settingsDrawerStoryboard?.Stop();
            settingsDrawerOpen = open;

            if (open)
            {
                SettingsDrawerOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                settingsInfoTimer.Stop();
                pendingInfoButton = null;
                SettingInfoTip.IsOpen = false;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(open ? 220 : 170));
            var easingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn;
            var storyboard = new Storyboard();
            var opacityAnimation = new DoubleAnimation
            {
                From = currentOpacity,
                To = open ? 1 : 0,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = easingMode }
            };
            Storyboard.SetTarget(opacityAnimation, SettingsDrawerOverlay);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
            var slideAnimation = new DoubleAnimation
            {
                From = currentOffset,
                To = open ? 0 : 430,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = easingMode }
            };
            Storyboard.SetTarget(slideAnimation, SettingsDrawerTransform);
            Storyboard.SetTargetProperty(slideAnimation, "X");
            storyboard.Children.Add(opacityAnimation);
            storyboard.Children.Add(slideAnimation);

            SettingsDrawerOverlay.Opacity = open ? 1 : 0;
            SettingsDrawerTransform.X = open ? 0 : 430;
            settingsDrawerStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(settingsDrawerStoryboard, storyboard))
                {
                    return;
                }

                storyboard.Stop();
                SettingsDrawerOverlay.Opacity = open ? 1 : 0;
                SettingsDrawerTransform.X = open ? 0 : 430;
                if (!open)
                {
                    SettingsDrawerOverlay.Visibility = Visibility.Collapsed;
                }
                settingsDrawerStoryboard = null;
            };
            storyboard.Begin();
        }

        private void AnimateConsole(bool open)
        {
            var currentHeight = ConsolePanel.Visibility == Visibility.Visible
                ? ConsolePanel.ActualHeight
                : 0;
            var currentOpacity = ConsolePanel.Opacity;
            var currentOffset = ConsoleSlideTransform.Y;
            consoleStoryboard?.Stop();

            consoleIsOpen = open;
            ConsoleToggleStateText.Text = open ? "Nascondi" : "Mostra";
            ConsoleToggleIcon.Glyph = open ? "\uE70E" : "\uE70D";
            AutomationProperties.SetName(
                ToggleConsoleButton,
                open ? "Nascondi console" : "Mostra console");

            if (open)
            {
                ConsolePanel.Visibility = Visibility.Visible;
            }

            var easing = new CubicEase
            {
                EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn
            };
            var duration = new Duration(TimeSpan.FromMilliseconds(open ? 260 : 210));
            var storyboard = new Storyboard();

            var heightAnimation = new DoubleAnimation
            {
                From = currentHeight,
                To = open ? 138 : 0,
                Duration = duration,
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(heightAnimation, ConsolePanel);
            Storyboard.SetTargetProperty(heightAnimation, "MaxHeight");

            var opacityAnimation = new DoubleAnimation
            {
                From = currentOpacity,
                To = open ? 1 : 0,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacityAnimation, ConsolePanel);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

            var slideAnimation = new DoubleAnimation
            {
                From = currentOffset,
                To = open ? 0 : -14,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(slideAnimation, ConsoleSlideTransform);
            Storyboard.SetTargetProperty(slideAnimation, "Y");

            storyboard.Children.Add(heightAnimation);
            storyboard.Children.Add(opacityAnimation);
            storyboard.Children.Add(slideAnimation);
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(consoleStoryboard, storyboard))
                {
                    return;
                }

                storyboard.Stop();
                ConsolePanel.MaxHeight = open ? double.PositiveInfinity : 0;
                ConsolePanel.Opacity = open ? 1 : 0;
                ConsoleSlideTransform.Y = open ? 0 : -14;
                if (!open)
                {
                    ConsolePanel.Visibility = Visibility.Collapsed;
                }
                consoleStoryboard = null;
            };

            ConsolePanel.MaxHeight = open ? double.PositiveInfinity : 0;
            ConsolePanel.Opacity = open ? 1 : 0;
            ConsoleSlideTransform.Y = open ? 0 : -14;
            consoleStoryboard = storyboard;
            storyboard.Begin();
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPresenterChange)
            {
                DispatcherQueue.TryEnqueue(UpdateMaximizeButton);
            }
        }

        private void UpdateMaximizeButton()
        {
            var isMaximized =
                App.MainWindow?.AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Maximized;
            MaximizeWindowIcon.Glyph = isMaximized ? "\uE923" : "\uE922";
            ToolTipService.SetToolTip(
                MaximizeWindowButton,
                isMaximized ? "Ripristina" : "Massimizza");
            AutomationProperties.SetName(
                MaximizeWindowButton,
                isMaximized ? "Ripristina" : "Massimizza");
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            settingsInfoTimer.Stop();
            SettingInfoTip.IsOpen = false;
            modelManagerCts?.Cancel();
            modelStatusCts?.Cancel();
            CancelActiveOperation();
        }

        public void CancelActiveOperation()
        {
            analysisCts?.Cancel();
            operationCts?.Cancel();
            lock (processSync)
            {
                TryKill(activeProcess);
            }
        }

        private async void BrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.VideosLibrary
                };
                foreach (var extension in AudioExtensions.Concat(VideoExtensions))
                {
                    picker.FileTypeFilter.Add(extension);
                }

                if (!InitializePicker(picker))
                {
                    ShowStatus("Finestra principale non disponibile.", InfoBarSeverity.Error);
                    return;
                }

                var files = await picker.PickMultipleFilesAsync();
                if (files.Count == 0)
                {
                    return;
                }

                var orderedFiles = files
                    .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                selectedAudioFiles.Clear();
                selectedAudioFiles.AddRange(orderedFiles.Select(file => file.Path));
                transcriptionQueue.Clear();
                transcriptionQueue.AddRange(
                    selectedAudioFiles.Select(source =>
                        new TranscriptionQueueJob(new[] { source })));
                selectedAudioFile = selectedAudioFiles[0];
                selectedOutputDirectory = null;
                lastOutputDirectory = null;
                FileNameText.Text = orderedFiles.Length == 1
                    ? orderedFiles[0].Name
                    : $"{orderedFiles.Length} tracce · {orderedFiles[0].Name}";
                ToolTipService.SetToolTip(
                    FileNameText,
                    string.Join(Environment.NewLine, selectedAudioFiles));
                SelectedPartsText.Text = string.Empty;
                SelectedPartsText.Visibility = Visibility.Collapsed;
                MultiFileControls.Visibility = Visibility.Visible;
                UpdateManageTracksButton();
                OutputDirectoryText.Text = "Stessa cartella del file sorgente";
                SettingsSummaryPanel.Visibility = Visibility.Visible;
                AnalysisPanel.Visibility = Visibility.Visible;
                ActionPanel.Visibility = Visibility.Visible;
                PrimaryActionButton.Visibility = Visibility.Visible;
                RestoreQueueButton.Visibility = Visibility.Collapsed;
                OpenOutputButton.Visibility = Visibility.Collapsed;
                UpdateSettingsSummary();
                UpdateRecoverableCheckpoint();
                ClearStatus();
                await RefreshModelOptionStatusesAsync();
                await AnalyzeSelectionAsync();
            }
            catch (Exception exception)
            {
                ShowStatus(
                    $"Impossibile aprire il selettore file: {exception.Message}",
                    InfoBarSeverity.Error);
            }
        }

        private async void ChooseOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add("*");
                if (!InitializePicker(picker))
                {
                    ShowStatus("Finestra principale non disponibile.", InfoBarSeverity.Error);
                    return;
                }

                var folder = await picker.PickSingleFolderAsync();
                if (folder is null)
                {
                    return;
                }

                selectedOutputDirectory = folder.Path;
                OutputDirectoryText.Text = folder.Path;
                OpenOutputButton.Visibility = Visibility.Collapsed;
                UpdateSettingsSummary();
                UpdateRecoverableCheckpoint();
            }
            catch (Exception exception)
            {
                ShowStatus(
                    $"Impossibile selezionare la cartella: {exception.Message}",
                    InfoBarSeverity.Error);
            }
        }

        private void UpdateManageTracksButton()
        {
            var jobLabel = transcriptionQueue.Count == 1 ? "1 lavoro" : $"{transcriptionQueue.Count} lavori";
            ManageTracksButtonText.Text = $"Gestisci coda · {selectedAudioFiles.Count} tracce · {jobLabel}";
        }

        private void ManageTracksButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedAudioFiles.Count == 0)
            {
                return;
            }

            if (settingsDrawerOpen)
            {
                AnimateSettingsDrawer(open: false);
            }
            if (modelManagerOpen)
            {
                CloseModelManager();
            }

            queueManagerWorkingQueue = transcriptionQueue.Select(job => job.Clone()).ToList();
            RefreshQueueManager(0);
            AnimateQueueManager(open: true);
        }

        private void RefreshQueueManager(int selectedIndex)
        {
            if (queueManagerWorkingQueue is not { Count: > 0 } workingQueue)
            {
                return;
            }
            QueueManagerListView.ItemsSource = null;
            queueManagerItems = new ObservableCollection<QueueManagerItem>(workingQueue
                .Select((job, index) => new QueueManagerItem
                {
                    JobKey = job.Key,
                    Title = $"LAVORO {index + 1}",
                    KindLabel = job.Sources.Count == 1
                        ? "SINGOLO"
                        : $"MULTIPARTE · {job.Sources.Count}",
                    SourcesLabel = string.Join("  →  ", job.Sources.Select(Path.GetFileName)),
                    OutputName = job.OutputName ?? string.Empty,
                    DefaultOutputName = GetAutomaticOutputName(job)
                })
                .ToArray());
            QueueManagerListView.ItemsSource = queueManagerItems;
            QueueManagerListView.SelectedIndex = Math.Clamp(selectedIndex, 0, workingQueue.Count - 1);
            QueueManagerValidationText.Visibility = Visibility.Collapsed;
            var trackCount = workingQueue.Sum(job => job.Sources.Count);
            QueueManagerSummaryText.Text =
                $"{trackCount} tracce · {workingQueue.Count} " +
                (workingQueue.Count == 1 ? "lavoro" : "lavori");
            UpdateQueueManagerActions();
        }

        private void UpdateQueueManagerActions()
        {
            var workingQueue = queueManagerWorkingQueue;
            var index = QueueManagerListView.SelectedIndex;
            var hasSelection = workingQueue is not null && index >= 0 && index < workingQueue.Count;
            QueueMoveUpButton.IsEnabled = hasSelection && index > 0;
            QueueMoveDownButton.IsEnabled = hasSelection && index < workingQueue!.Count - 1;
            QueueMergeButton.IsEnabled = hasSelection && index > 0;
            QueueSplitButton.IsEnabled = hasSelection && workingQueue![index].Sources.Count > 1;
            QueueRemoveButton.IsEnabled = hasSelection && workingQueue!.Count > 1;
        }

        private void QueueManagerListView_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateQueueManagerActions();

        private void QueueOutputNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textBox ||
                textBox.Tag is not string jobKey ||
                queueManagerWorkingQueue is not { } workingQueue)
            {
                return;
            }
            var job = workingQueue.FirstOrDefault(candidate => candidate.Key == jobKey);
            if (job is not null)
            {
                job.OutputName = textBox.Text;
                QueueManagerValidationText.Visibility = Visibility.Collapsed;
            }
        }

        private void QueueDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement handle ||
                handle.Tag is not string jobKey ||
                queueManagerItems is null ||
                !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var index = queueManagerItems.ToList().FindIndex(item => item.JobKey == jobKey);
            if (index < 0 || !handle.CapturePointer(e.Pointer))
            {
                return;
            }

            queuePointerDragJobKey = jobKey;
            queuePointerDragHandle = handle;
            queuePointerDragContainer = QueueManagerListView.ContainerFromIndex(index) as ListViewItem;
            queuePointerDragOriginIndex = index;
            queuePointerDragTargetIndex = index;
            queuePointerSiblingTransforms.Clear();
            queuePointerId = e.Pointer.PointerId;
            QueueManagerListView.SelectedIndex = index;
            if (queuePointerDragContainer is { } container)
            {
                queuePointerDragOffsetY = e.GetCurrentPoint(container).Position.Y;
                queuePointerLastY = e.GetCurrentPoint(QueueManagerListView).Position.Y;
                queuePointerDragVisual = container.ContentTemplateRoot as FrameworkElement ?? container;
                queuePointerDragTransform = new CompositeTransform();
                queuePointerDragVisual.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                queuePointerDragVisual.RenderTransform = queuePointerDragTransform;
                Canvas.SetZIndex(container, 1000);
                AnimateQueueDragVisual(queuePointerDragVisual, queuePointerDragTransform, engaged: true);
            }
            e.Handled = true;
        }

        private void QueueDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerId != queuePointerId ||
                queuePointerDragJobKey is null ||
                queueManagerItems is not { Count: > 0 } items ||
                queueManagerWorkingQueue is null)
            {
                return;
            }

            var point = e.GetCurrentPoint(QueueManagerListView).Position;
            queuePointerLastY = Math.Clamp(point.Y, 0, QueueManagerListView.ActualHeight);
            UpdateQueueDraggedItemPosition(queuePointerLastY);

            var targetIndex = GetQueueDropIndex(queuePointerLastY, items.Count);
            if (targetIndex >= 0 && targetIndex != queuePointerDragTargetIndex)
            {
                queuePointerDragTargetIndex = targetIndex;
                AnimateQueueDropGap(queuePointerDragOriginIndex, targetIndex, items.Count);
            }
            e.Handled = true;
        }

        private void AnimateQueueDropGap(int originIndex, int targetIndex, int itemCount)
        {
            if (queuePointerDragContainer is not { } draggedContainer)
            {
                return;
            }

            var step = draggedContainer.ActualHeight +
                draggedContainer.Margin.Top + draggedContainer.Margin.Bottom;
            for (var index = 0; index < itemCount; index++)
            {
                if (index == originIndex ||
                    QueueManagerListView.ContainerFromIndex(index) is not ListViewItem container)
                {
                    continue;
                }

                var targetOffset = 0d;
                if (targetIndex > originIndex && index > originIndex && index <= targetIndex)
                {
                    targetOffset = -step;
                }
                else if (targetIndex < originIndex && index >= targetIndex && index < originIndex)
                {
                    targetOffset = step;
                }

                if (targetOffset == 0 && !queuePointerSiblingTransforms.ContainsKey(container))
                {
                    continue;
                }

                if (!queuePointerSiblingTransforms.TryGetValue(container, out var state))
                {
                    var visual = container.ContentTemplateRoot as FrameworkElement ?? container;
                    var transform = new CompositeTransform();
                    visual.RenderTransform = transform;
                    state = (visual, transform);
                    queuePointerSiblingTransforms[container] = state;
                }
                AnimateQueueTranslate(state.Transform, targetOffset, 115);
            }
        }

        private void UpdateQueueDraggedItemPosition(double pointerY)
        {
            if (queuePointerDragContainer is not { } container ||
                queuePointerDragTransform is not { } transform)
            {
                return;
            }

            var bounds = container.TransformToVisual(QueueManagerListView).TransformBounds(
                new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var layoutTop = bounds.Y;
            var maxTop = Math.Max(0, QueueManagerListView.ActualHeight - container.ActualHeight);
            var desiredTop = Math.Clamp(pointerY - queuePointerDragOffsetY, 0, maxTop);
            transform.TranslateY = desiredTop - layoutTop;
        }

        private int GetQueueDropIndex(double pointerY, int itemCount)
        {
            var fallback = itemCount - 1;
            for (var index = 0; index < itemCount; index++)
            {
                if (QueueManagerListView.ContainerFromIndex(index) is not ListViewItem container)
                {
                    continue;
                }
                var bounds = container.TransformToVisual(QueueManagerListView).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
                if (pointerY < bounds.Y + (bounds.Height / 2))
                {
                    return index;
                }
            }
            return fallback;
        }

        private void QueueDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e) =>
            CompleteQueuePointerDrag(e.Pointer.PointerId, cancelled: false);

        private void QueueDragHandle_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
            CompleteQueuePointerDrag(e.Pointer.PointerId, cancelled: true);

        private void QueueDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (queuePointerDragJobKey is not null)
            {
                CompleteQueuePointerDrag(e.Pointer.PointerId, cancelled: false, releaseCapture: false);
            }
        }

        private void CompleteQueuePointerDrag(
            uint pointerId,
            bool cancelled,
            bool releaseCapture = true)
        {
            if (pointerId != queuePointerId || queuePointerDragJobKey is null)
            {
                return;
            }

            var handle = queuePointerDragHandle;
            var container = queuePointerDragContainer;
            var visual = queuePointerDragVisual;
            var transform = queuePointerDragTransform;
            var originIndex = queuePointerDragOriginIndex;
            var targetIndex = cancelled ? originIndex : queuePointerDragTargetIndex;
            var workingQueue = queueManagerWorkingQueue;
            var settleOffset = targetIndex >= 0
                ? GetQueueSlotOffset(originIndex, targetIndex)
                : 0;
            queuePointerDragJobKey = null;
            queuePointerDragHandle = null;
            queuePointerDragContainer = null;
            queuePointerDragVisual = null;
            queuePointerDragTransform = null;
            queuePointerDragOriginIndex = -1;
            queuePointerDragTargetIndex = -1;
            queuePointerId = 0;
            if (releaseCapture)
            {
                handle?.ReleasePointerCaptures();
            }
            if (container is not null && visual is not null && transform is not null)
            {
                if (cancelled)
                {
                    foreach (var siblingState in queuePointerSiblingTransforms.Values)
                    {
                        AnimateQueueTranslate(siblingState.Transform, 0, 150);
                    }
                }
                var settleAnimation = AnimateQueueDragVisual(
                    visual,
                    transform,
                    engaged: false,
                    settleTranslateY: settleOffset,
                    begin: false);
                settleAnimation.Completed += (_, _) =>
                {
                    visual.Opacity = 1;
                    visual.RenderTransform = null;
                    Canvas.SetZIndex(container, 0);
                    foreach (var siblingState in queuePointerSiblingTransforms.Values)
                    {
                        siblingState.Visual.RenderTransform = null;
                    }
                    queuePointerSiblingTransforms.Clear();

                    if (!cancelled &&
                        workingQueue is not null &&
                        originIndex >= 0 &&
                        targetIndex >= 0 &&
                        originIndex < workingQueue.Count &&
                        targetIndex < workingQueue.Count &&
                        originIndex != targetIndex)
                    {
                        var job = workingQueue[originIndex];
                        workingQueue.RemoveAt(originIndex);
                        workingQueue.Insert(targetIndex, job);
                    }
                    if (workingQueue is { Count: > 0 })
                    {
                        RefreshQueueManager(Math.Clamp(targetIndex, 0, workingQueue.Count - 1));
                    }
                };
                settleAnimation.Begin();
            }
            UpdateQueueManagerActions();
        }

        private double GetQueueSlotOffset(int originIndex, int targetIndex)
        {
            if (originIndex < 0 || targetIndex < 0 || originIndex == targetIndex ||
                QueueManagerListView.ContainerFromIndex(originIndex) is not ListViewItem origin ||
                QueueManagerListView.ContainerFromIndex(targetIndex) is not ListViewItem target)
            {
                return 0;
            }
            var originTop = origin.TransformToVisual(QueueManagerListView).TransformPoint(
                new Windows.Foundation.Point()).Y;
            var targetTop = target.TransformToVisual(QueueManagerListView).TransformPoint(
                new Windows.Foundation.Point()).Y;
            return targetTop - originTop;
        }

        private static void AnimateQueueTranslate(
            CompositeTransform transform,
            double target,
            int durationMilliseconds)
        {
            var storyboard = new Storyboard();
            AddAnimation(
                transform,
                "TranslateY",
                target,
                TimeSpan.FromMilliseconds(durationMilliseconds),
                new QuadraticEase { EasingMode = EasingMode.EaseOut },
                storyboard);
            storyboard.Begin();
        }

        private static Storyboard AnimateQueueDragVisual(
            FrameworkElement visual,
            CompositeTransform transform,
            bool engaged,
            double settleTranslateY = 0,
            bool begin = true)
        {
            var duration = TimeSpan.FromMilliseconds(engaged ? 110 : 170);
            var easing = new QuadraticEase
            {
                EasingMode = engaged ? EasingMode.EaseOut : EasingMode.EaseInOut
            };
            var storyboard = new Storyboard();
            AddAnimation(visual, "Opacity", engaged ? 0.9 : 1, duration, easing, storyboard);
            AddAnimation(transform, "ScaleX", engaged ? 1.018 : 1, duration, easing, storyboard);
            AddAnimation(transform, "ScaleY", engaged ? 1.018 : 1, duration, easing, storyboard);
            if (!engaged)
            {
                AddAnimation(transform, "TranslateY", settleTranslateY, duration, easing, storyboard);
            }
            if (begin)
            {
                storyboard.Begin();
            }
            return storyboard;
        }

        private static void AddAnimation(
            DependencyObject target,
            string property,
            double value,
            TimeSpan duration,
            EasingFunctionBase easing,
            Storyboard storyboard)
        {
            var animation = new DoubleAnimation
            {
                To = value,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        private void MoveQueueManagerJob(int offset)
        {
            if (queueManagerWorkingQueue is not { } workingQueue)
            {
                return;
            }
            var index = QueueManagerListView.SelectedIndex;
            var target = index + offset;
            if (index < 0 || target < 0 || target >= workingQueue.Count)
            {
                return;
            }
            (workingQueue[index], workingQueue[target]) =
                (workingQueue[target], workingQueue[index]);
            RefreshQueueManager(target);
        }

        private void QueueMoveUpButton_Click(object sender, RoutedEventArgs e) => MoveQueueManagerJob(-1);

        private void QueueMoveDownButton_Click(object sender, RoutedEventArgs e) => MoveQueueManagerJob(1);

        private void QueueMergeButton_Click(object sender, RoutedEventArgs e)
        {
            if (queueManagerWorkingQueue is not { } workingQueue)
            {
                return;
            }
            var index = QueueManagerListView.SelectedIndex;
            if (index <= 0)
            {
                return;
            }
            workingQueue[index - 1].Sources.AddRange(workingQueue[index].Sources);
            workingQueue.RemoveAt(index);
            RefreshQueueManager(index - 1);
        }

        private void QueueSplitButton_Click(object sender, RoutedEventArgs e)
        {
            if (queueManagerWorkingQueue is not { } workingQueue)
            {
                return;
            }
            var index = QueueManagerListView.SelectedIndex;
            if (index < 0 || workingQueue[index].Sources.Count <= 1)
            {
                return;
            }
            var singles = workingQueue[index].Sources
                .Select(source => new TranscriptionQueueJob(new[] { source }))
                .ToArray();
            workingQueue.RemoveAt(index);
            workingQueue.InsertRange(index, singles);
            RefreshQueueManager(index);
        }

        private void QueueRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (queueManagerWorkingQueue is not { Count: > 1 } workingQueue)
            {
                return;
            }
            var index = QueueManagerListView.SelectedIndex;
            if (index < 0)
            {
                return;
            }
            workingQueue.RemoveAt(index);
            RefreshQueueManager(Math.Min(index, workingQueue.Count - 1));
        }

        private void CloseQueueManagerButton_Click(object sender, RoutedEventArgs e) =>
            CloseQueueManager();

        private void QueueManagerOverlay_Tapped(object sender, TappedRoutedEventArgs e) =>
            CloseQueueManager();

        private void QueueManagerPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void CloseQueueManager()
        {
            queueManagerWorkingQueue = null;
            AnimateQueueManager(open: false);
        }

        private async void ApplyQueueManagerButton_Click(object sender, RoutedEventArgs e)
        {
            if (queueManagerWorkingQueue is not { Count: > 0 } workingQueue)
            {
                return;
            }

            try
            {
                foreach (var job in workingQueue)
                {
                    _ = GetOutputNameForJob(job);
                }
            }
            catch (Exception exception)
            {
                QueueManagerValidationText.Text = exception.Message;
                QueueManagerValidationText.Visibility = Visibility.Visible;
                return;
            }

            transcriptionQueue.Clear();
            transcriptionQueue.AddRange(workingQueue.Select(job => job.Clone()));
            queueManagerWorkingQueue = null;
            SynchronizeSelectedFilesFromQueue();
            selectedAudioFile = selectedAudioFiles[0];
            FileNameText.Text = selectedAudioFiles.Count == 1
                ? Path.GetFileName(selectedAudioFile)
                : $"{selectedAudioFiles.Count} tracce · {Path.GetFileName(selectedAudioFile)}";
            ToolTipService.SetToolTip(
                FileNameText,
                string.Join(Environment.NewLine, selectedAudioFiles));
            MultiFileControls.Visibility = Visibility.Visible;
            UpdateManageTracksButton();
            UpdateSelectedFilesPresentation();
            UpdateRecoverableCheckpoint();
            UpdateSettingsSummary();
            PrimaryActionButton.Content = selectedModelIsDownloaded
                ? GetIdlePrimaryActionText()
                : "Scarica modello";
            AnimateQueueManager(open: false);
            await AnalyzeSelectionAsync();
        }

        private void SynchronizeSelectedFilesFromQueue()
        {
            selectedAudioFiles.Clear();
            selectedAudioFiles.AddRange(transcriptionQueue.SelectMany(job => job.Sources));
            selectedAudioFile = selectedAudioFiles.FirstOrDefault();
        }

        private void LimitIntervalCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ClipStartTextBox is null || ClipEndTextBox is null)
            {
                return;
            }
            var enabled = LimitIntervalCheckBox.IsChecked == true;
            ClipStartTextBox.IsEnabled = enabled;
            ClipEndTextBox.IsEnabled = enabled;
            UpdateSettingsSummary();
        }

        private void ClipTimeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSettingsSummary();
        }

        private bool IsBatchMode => transcriptionQueue.Count > 1;

        private void UpdateSelectedFilesPresentation(
            int activeBatchIndex = -1,
            IReadOnlyList<string>? batchStates = null)
        {
            if (selectedAudioFiles.Count <= 1)
            {
                SelectedPartsText.Text = string.Empty;
                SelectedPartsText.Visibility = Visibility.Collapsed;
                return;
            }

            if (!IsBatchMode || batchStates is null)
            {
                SelectedPartsText.Text = string.Empty;
                SelectedPartsText.Visibility = Visibility.Collapsed;
                return;
            }

            SelectedPartsText.Visibility = Visibility.Visible;
            var states = batchStates;
            var queueText = string.Join(
                Environment.NewLine,
                transcriptionQueue.Select((job, index) =>
                    $"{index + 1}. {Path.GetFileName(job.Sources[0])}" +
                    (job.Sources.Count > 1 ? $" (+{job.Sources.Count - 1} parti)" : string.Empty) +
                    $" — {states[index]}"));
            SelectedPartsText.Text = queueText;
            ToolTipService.SetToolTip(SelectedPartsText, queueText);
        }

        private string GetIdlePrimaryActionText()
        {
            return IsBatchMode
                ? $"Avvia coda ({transcriptionQueue.Count})"
                : "Avvia trascrizione";
        }

        private static string BuildCheckpointPath(string sourcePath, string outputDirectory)
        {
            return Path.Combine(
                outputDirectory,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}_trascrizione.whisperls.json");
        }

        private static string GetAutomaticOutputName(TranscriptionQueueJob job)
        {
            return job.Sources.Count > 1
                ? "LavoroMultiparte"
                : Path.GetFileNameWithoutExtension(job.Sources[0]);
        }

        private static string GetOutputNameForJob(TranscriptionQueueJob job)
        {
            var expanded = string.IsNullOrWhiteSpace(job.OutputName)
                ? GetAutomaticOutputName(job)
                : job.OutputName.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            if (expanded.Length == 0 || expanded.IndexOfAny(invalid) >= 0 || expanded.EndsWith(' ') || expanded.EndsWith('.'))
            {
                throw new InvalidOperationException("Il nome dell'output non è valido per Windows.");
            }
            var stem = expanded.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.StartsWith("COM") && int.TryParse(stem[3..], out var com) && com is >= 1 and <= 9) ||
                (stem.StartsWith("LPT") && int.TryParse(stem[3..], out var lpt) && lpt is >= 1 and <= 9))
            {
                throw new InvalidOperationException("Il nome dell'output è riservato da Windows.");
            }
            return expanded;
        }

        private void UpdateRecoverableCheckpoint()
        {
            recoverableCheckpointPath = null;
            if (selectedAudioFile is not null && !IsBatchMode)
            {
                var outputDirectory = selectedOutputDirectory ??
                    Path.GetDirectoryName(selectedAudioFile);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    string candidate;
                    try
                    {
                        candidate = Path.Combine(
                            outputDirectory,
                            $"{GetOutputNameForJob(transcriptionQueue[0])}.whisperls.json");
                    }
                    catch
                    {
                        candidate = BuildCheckpointPath(selectedAudioFile, outputDirectory);
                    }
                    if (File.Exists(candidate))
                    {
                        recoverableCheckpointPath = candidate;
                    }
                }
            }
            ResumeCheckpointButton.Visibility = recoverableCheckpointPath is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void Settings_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateModelSelectorBorderState();
            UpdateSettingsSummary();
            if (initialized && selectedAudioFile is not null)
            {
                await AnalyzeSelectionAsync();
            }
        }

        private async void BeamSizeNumberBox_ValueChanged(
            NumberBox sender,
            NumberBoxValueChangedEventArgs args)
        {
            if (double.IsNaN(args.NewValue) || args.NewValue < 1 || args.NewValue > 20)
            {
                sender.Value = 5;
            }

            UpdateSettingsSummary();
            if (initialized && selectedAudioFile is not null)
            {
                await AnalyzeSelectionAsync();
            }
        }

        private void SettingsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            UpdateSettingsSummary();
        }

        private void OutputFormatCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtOutputCheckBox is null ||
                SrtOutputCheckBox is null ||
                DocxOutputCheckBox is null)
            {
                return;
            }

            if (GetSelectedOutputFormats().Length == 0 && sender is CheckBox checkBox)
            {
                checkBox.IsChecked = true;
                if (initialized)
                {
                    ShowStatus("Seleziona almeno un formato di output.", InfoBarSeverity.Warning);
                }
                return;
            }

            UpdateSettingsSummary();
        }

        private void UpdateSettingsSummary()
        {
            if (ModelComboBox is null ||
                LanguageComboBox is null ||
                DeviceComboBox is null ||
                BeamSizeNumberBox is null ||
                VadToggle is null ||
                WordTimestampsToggle is null ||
                LimitIntervalCheckBox is null ||
                ClipStartTextBox is null ||
                ClipEndTextBox is null ||
                TxtOutputCheckBox is null ||
                SrtOutputCheckBox is null ||
                DocxOutputCheckBox is null)
            {
                return;
            }

            var model = GetSelectedModel();
            var language = GetSelectedLabel(LanguageComboBox, "Italiano");
            var device = GetSelectedLabel(DeviceComboBox, "Automatico");
            SettingsSummaryText.Text = $"{model} · {language} · {device}";
            AdvancedSettingsSummaryText.Text =
                $"Beam {Math.Max(1, (int)BeamSizeNumberBox.Value)} · " +
                $"VAD {(VadToggle.IsOn ? "attivo" : "disattivo")} · " +
                $"Timestamp {(WordTimestampsToggle.IsOn ? "attivi" : "disattivati")}" +
                (LimitIntervalCheckBox.IsChecked == true
                    ? $" · Intervallo {ClipStartTextBox.Text.Trim()}–{ClipEndTextBox.Text.Trim()}"
                    : string.Empty);
            var formats = string.Join(", ", GetSelectedOutputFormats().Select(format => format.ToUpperInvariant()));
            OutputSummaryText.Text = selectedOutputDirectory is null
                ? $"Output: cartella del file sorgente · {formats}"
                : $"Output: {selectedOutputDirectory} · {formats}";
            ToolTipService.SetToolTip(OutputSummaryText, selectedOutputDirectory);
        }

        private string[] GetSelectedOutputFormats()
        {
            var formats = new List<string>(3);
            if (TxtOutputCheckBox?.IsChecked == true)
            {
                formats.Add("txt");
            }
            if (SrtOutputCheckBox?.IsChecked == true)
            {
                formats.Add("srt");
            }
            if (DocxOutputCheckBox?.IsChecked == true)
            {
                formats.Add("docx");
            }
            return formats.ToArray();
        }

        private TimeInterval? GetSelectedInterval()
        {
            if (LimitIntervalCheckBox.IsChecked != true)
            {
                return null;
            }
            if (!TryParseTimeOffset(ClipStartTextBox.Text, out var start) ||
                !TryParseTimeOffset(ClipEndTextBox.Text, out var end))
            {
                throw new InvalidOperationException(
                    "Inserisci l'intervallo nel formato HH:MM:SS, MM:SS oppure in secondi.");
            }
            if (end <= start)
            {
                throw new InvalidOperationException(
                    "La fine dell'intervallo deve essere successiva all'inizio.");
            }
            if (transcriptionQueue.Any(job => job.Sources.Count > 1))
            {
                throw new InvalidOperationException(
                    "L'intervallo temporale non è disponibile quando la coda contiene un lavoro multiparte. " +
                    "Separa quel lavoro in tracce singole per usare l'intervallo.");
            }
            return new TimeInterval(start, end);
        }

        private static bool TryParseTimeOffset(string text, out double seconds)
        {
            seconds = 0;
            var parts = text.Trim().Split(':');
            if (parts.Length is < 1 or > 3)
            {
                return false;
            }
            var values = new double[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!double.TryParse(
                        parts[index],
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out values[index]) ||
                    values[index] < 0)
                {
                    return false;
                }
            }
            seconds = parts.Length switch
            {
                1 => values[0],
                2 => values[0] * 60 + values[1],
                _ => values[0] * 3600 + values[1] * 60 + values[2]
            };
            return double.IsFinite(seconds);
        }

        private static string GetSelectedLabel(ComboBox comboBox, string fallback)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
        }

        private string GetSelectedModel()
        {
            return (ModelComboBox.SelectedItem as ModelOption)?.Name ?? "medium";
        }

        private void UpdateModelSelectorBorderState()
        {
            if (ModelComboBox?.SelectedItem is not ModelOption option)
            {
                return;
            }

            SelectedModelReadyBorder.BorderThickness = option.ReadyBorderThickness;
        }

        private async Task AnalyzeSelectionAsync()
        {
            analysisCts?.Cancel();
            analysisCts?.Dispose();
            var localCts = new CancellationTokenSource();
            analysisCts = localCts;
            var token = localCts.Token;

            var model = GetSelectedModel();
            if (selectedAudioFile is null)
            {
                return;
            }

            try
            {
                selectedModelIsDownloaded = false;
                AnalysisStatusText.Text = "Analisi del file e dell'hardware...";
                OperationProgressBar.IsIndeterminate = true;
                SourceDurationText.Text = "Calcolo in corso";
                EstimatedTimeText.Text = "Calcolo in corso";
                PrimaryActionButton.IsEnabled = false;

                var durationTask = Task.WhenAll(
                    selectedAudioFiles.Select(TryGetMediaDurationAsync));
                var hardwareTask = DetectHardwareProfileAsync(token);
                await Task.WhenAll(durationTask, hardwareTask);
                token.ThrowIfCancellationRequested();

                var sourceDurations = await durationTask;
                var durationsWereDetected = sourceDurations.All(duration => duration > TimeSpan.Zero);
                var sourceDuration = sourceDurations.Aggregate(TimeSpan.Zero, (total, duration) => total + duration);
                SourceDurationText.Text = durationsWereDetected
                    ? FormatMediaDuration(sourceDuration)
                    : "Non rilevata";
                var duration = sourceDuration;
                if (!durationsWereDetected)
                {
                    duration = TimeSpan.FromMinutes(10 * Math.Max(1, selectedAudioFiles.Count));
                }
                if (LimitIntervalCheckBox.IsChecked == true &&
                    TryParseTimeOffset(ClipStartTextBox.Text, out var clipStart) &&
                    TryParseTimeOffset(ClipEndTextBox.Text, out var clipEnd) &&
                    clipEnd > clipStart &&
                    transcriptionQueue.All(job => job.Sources.Count == 1))
                {
                    var intervalDuration = TimeSpan.FromSeconds(clipEnd - clipStart);
                    duration = IsBatchMode
                        ? TimeSpan.FromTicks(intervalDuration.Ticks * transcriptionQueue.Count)
                        : intervalDuration;
                }

                var estimate = BuildEstimate(
                    model,
                    duration,
                    await hardwareTask,
                    GetSelectedTag(DeviceComboBox));
                EstimatedTimeText.Text =
                    $"Circa {FormatDuration(estimate.EstimatedDuration)}" +
                    (durationsWereDetected ? string.Empty : " (durata di una parte non rilevata)");

                AnalysisStatusText.Text = "Controllo del modello locale...";
                selectedModelIsDownloaded = await CheckModelDownloadedAsync(model, token);
                token.ThrowIfCancellationRequested();
                AnalysisStatusText.Text = selectedModelIsDownloaded
                    ? "Modello pronto. La stima è indicativa e non include l'avvio iniziale."
                    : "Il modello deve essere scaricato prima della trascrizione.";
                PrimaryActionButton.Content = selectedModelIsDownloaded
                    ? GetIdlePrimaryActionText()
                    : "Scarica modello";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                AnalysisStatusText.Text = "Analisi non completata.";
                ShowStatus($"Errore durante l'analisi: {exception.Message}", InfoBarSeverity.Warning);
            }
            finally
            {
                if (ReferenceEquals(analysisCts, localCts))
                {
                    OperationProgressBar.IsIndeterminate = false;
                    PrimaryActionButton.IsEnabled = true;
                }
            }
        }

        private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
        {
            var model = GetSelectedModel();
            if (selectedAudioFile is null)
            {
                ShowStatus("Seleziona un file e un modello.", InfoBarSeverity.Warning);
                return;
            }

            analysisCts?.Cancel();
            operationCts?.Dispose();
            var localCts = new CancellationTokenSource();
            operationCts = localCts;
            var isTranscription = selectedModelIsDownloaded;

            try
            {
                SetBusy(true);
                ClearStatus();
                if (!isTranscription)
                {
                    await DownloadModelAsync(model, localCts.Token);
                }
                else
                {
                    PrimaryActionButton.Content = IsBatchMode
                        ? "Coda in elaborazione..."
                        : "Trascrizione in corso...";
                    if (IsBatchMode)
                    {
                        await TranscribeBatchAsync(model, localCts.Token);
                    }
                    else
                    {
                        var onlyJob = transcriptionQueue.Count == 1
                            ? transcriptionQueue[0]
                            : new TranscriptionQueueJob(selectedAudioFiles);
                        await TranscribeSourcesAsync(
                            model,
                            onlyJob.Sources,
                            localCts.Token,
                            outputName: GetOutputNameForJob(onlyJob));
                        ShowStatus(
                            onlyJob.Sources.Count > 1
                                ? "Le tracce audio selezionate sono state unite e trascritte con successo!"
                                : "La traccia audio selezionata è stata trascritta con successo!",
                            InfoBarSeverity.Informational);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Operazione annullata.", InfoBarSeverity.Warning);
            }
            catch (Exception exception)
            {
                ShowStatus($"Operazione non completata: {exception.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                if (ReferenceEquals(operationCts, localCts))
                {
                    SetBusy(false);
                    PrimaryActionButton.Content = selectedModelIsDownloaded
                        ? GetIdlePrimaryActionText()
                        : "Scarica modello";
                    operationCts.Dispose();
                    operationCts = null;
                    UpdateRecoverableCheckpoint();
                }
            }
        }

        private async void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBatchMode || currentBatchJobId is null)
            {
                CancelActiveOperation();
                return;
            }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Interrompere la coda?",
                Content = "Puoi saltare soltanto il lavoro corrente oppure annullare tutti i lavori rimanenti.",
                PrimaryButtonText = "Solo lavoro corrente",
                SecondaryButtonText = "Intera coda",
                CloseButtonText = "Continua",
                DefaultButton = ContentDialogButton.Close
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                Directory.CreateDirectory(AppDataDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(AppDataDirectory, "queue-control.json"),
                    JsonSerializer.Serialize(new { action = "cancel-current", jobId = currentBatchJobId }));
                ShowStatus("La traccia corrente verrà interrotta; la coda proseguirà.", InfoBarSeverity.Warning);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                CancelActiveOperation();
            }
        }

        private async void RestoreQueueButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(PendingQueuePath));
                var root = document.RootElement;
                var jobs = root.GetProperty("jobs").EnumerateArray().ToArray();
                var restoredQueue = jobs
                    .Select(job => new TranscriptionQueueJob(
                        job.GetProperty("sources")
                            .EnumerateArray()
                            .Select(source => source.GetString())
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .Select(path => path!),
                        outputName: job.TryGetProperty("outputName", out var outputName)
                            ? outputName.GetString()
                            : null))
                    .Where(job => job.Sources.Count > 0)
                    .ToArray();
                if (restoredQueue.Length == 0 ||
                    restoredQueue.SelectMany(job => job.Sources).Any(path => !File.Exists(path)))
                {
                    throw new InvalidOperationException("I file della coda non sono più disponibili.");
                }

                transcriptionQueue.Clear();
                transcriptionQueue.AddRange(restoredQueue);
                SynchronizeSelectedFilesFromQueue();
                FileNameText.Text = selectedAudioFiles.Count == 1
                    ? Path.GetFileName(selectedAudioFile)
                    : $"{selectedAudioFiles.Count} tracce · {Path.GetFileName(selectedAudioFile)}";
                MultiFileControls.Visibility = Visibility.Visible;
                UpdateManageTracksButton();
                SettingsSummaryPanel.Visibility = Visibility.Visible;
                AnalysisPanel.Visibility = Visibility.Visible;
                ActionPanel.Visibility = Visibility.Visible;
                PrimaryActionButton.Visibility = Visibility.Visible;
                RestoreQueueButton.Visibility = Visibility.Collapsed;
                UpdateSelectedFilesPresentation();
                UpdateSettingsSummary();
                await RefreshModelOptionStatusesAsync();
                await AnalyzeSelectionAsync();
                ShowStatus(
                    "Coda interrotta ripristinata. Puoi riprendere l'elaborazione.",
                    InfoBarSeverity.Informational);
            }
            catch (Exception exception)
            {
                ShowStatus($"Impossibile ripristinare la coda: {exception.Message}", InfoBarSeverity.Error);
            }
        }

        private async void ResumeCheckpointButton_Click(object sender, RoutedEventArgs e)
        {
            var checkpointPath = recoverableCheckpointPath;
            if (checkpointPath is null || !File.Exists(checkpointPath))
            {
                UpdateRecoverableCheckpoint();
                ShowStatus("Il checkpoint non è più disponibile.", InfoBarSeverity.Warning);
                return;
            }

            analysisCts?.Cancel();
            operationCts?.Dispose();
            var localCts = new CancellationTokenSource();
            operationCts = localCts;
            try
            {
                SetBusy(true);
                ClearStatus();
                PrimaryActionButton.Content = "Ripresa in corso...";
                await TranscribeSourcesAsync(
                    GetSelectedModel(),
                    selectedAudioFiles,
                    localCts.Token,
                    checkpointPath: checkpointPath);
                ShowStatus(
                    "La trascrizione interrotta è stata ripresa e completata con successo!",
                    InfoBarSeverity.Informational);
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Ripresa annullata. Il checkpoint è stato conservato.", InfoBarSeverity.Warning);
            }
            catch (Exception exception)
            {
                ShowStatus($"Ripresa non completata: {exception.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                if (ReferenceEquals(operationCts, localCts))
                {
                    SetBusy(false);
                    PrimaryActionButton.Content = selectedModelIsDownloaded
                        ? GetIdlePrimaryActionText()
                        : "Scarica modello";
                    operationCts.Dispose();
                    operationCts = null;
                    UpdateRecoverableCheckpoint();
                }
            }
        }

        private async Task DownloadModelAsync(string model, CancellationToken token)
        {
            AnalysisStatusText.Text = "Download del modello...";
            OperationProgressBar.IsIndeterminate = true;
            var result = await RunWhisperCommandAsync(
                new[] { "--download-model", model },
                AppendBackendLog,
                progress =>
                {
                    OperationProgressBar.IsIndeterminate = false;
                    OperationProgressBar.Value = progress.Percent;
                    AnalysisStatusText.Text =
                        $"Download: {progress.Percent:0}% · " +
                        FormatProgressBytes(progress.DownloadedBytes, progress.TotalBytes);
                },
                token);

            EnsureSuccess(result, "Download");
            selectedModelIsDownloaded = true;
            modelOptions.FirstOrDefault(option => option.Name == model)?.SetInstalled(true);
            UpdateModelSelectorBorderState();
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = 100;
            AnalysisStatusText.Text = "Modello scaricato e verificato.";
            PrimaryActionButton.Content = GetIdlePrimaryActionText();
            ShowStatus("Modello pronto. Puoi avviare la trascrizione.", InfoBarSeverity.Success);
        }

        private async Task TranscribeBatchAsync(string model, CancellationToken token)
        {
            var states = transcriptionQueue.Select(_ => "in attesa").ToArray();
            Directory.CreateDirectory(AppDataDirectory);
            var controlPath = Path.Combine(AppDataDirectory, "queue-control.json");
            File.Delete(controlPath);
            var formats = GetSelectedOutputFormats();
            if (formats.Length == 0)
            {
                throw new InvalidOperationException("Seleziona almeno un formato di output.");
            }
            var interval = GetSelectedInterval();
            var jobs = transcriptionQueue.Select((queueJob, index) =>
            {
                var source = queueJob.Sources[0];
                var outputDirectory = selectedOutputDirectory ??
                    Path.GetDirectoryName(source) ??
                    throw new InvalidOperationException("Cartella del file non disponibile.");
                var outputName = GetOutputNameForJob(queueJob);
                var checkpoint = Path.Combine(outputDirectory, $"{outputName}.whisperls.json");
                return new
                {
                    id = $"job-{index + 1}",
                    sources = queueJob.Sources.ToArray(),
                    model,
                    device = GetSelectedTag(DeviceComboBox),
                    language = GetSelectedTag(LanguageComboBox),
                    beamSize = (int)Math.Round(BeamSizeNumberBox.Value),
                    vad = VadToggle.IsOn,
                    wordTimestamps = WordTimestampsToggle.IsOn,
                    outputFormats = formats,
                    outputDirectory,
                    outputName,
                    clipStart = interval?.Start,
                    clipEnd = interval?.End,
                    resumeCheckpoint = File.Exists(checkpoint) ? checkpoint : null,
                    autoRename = true
                };
            }).ToArray();
            await File.WriteAllTextAsync(
                PendingQueuePath,
                JsonSerializer.Serialize(
                    new { version = 3, controlPath, jobs },
                    new JsonSerializerOptions { WriteIndented = true }),
                token);

            AppendConsoleEvent("CODA", $"{jobs.Length} lavori · modello persistente {model}", "LogInfoBrush");
            BeginConsoleProgress("Lavoro");
            WhisperCommandResult result;
            try
            {
                result = await RunWhisperCommandAsync(
                    new[] { "--batch-manifest", PendingQueuePath },
                    AppendBackendLog,
                    progress =>
                    {
                        var jobIndex = Math.Clamp(progress.JobIndex - 1, 0, states.Length - 1);
                        currentBatchJobId = progress.JobId;
                        if (string.Equals(progress.Phase, "batch-job", StringComparison.OrdinalIgnoreCase))
                        {
                            states[jobIndex] = progress.Status switch
                            {
                                "success" => "completata",
                                "cancelled" => "annullata",
                                "error" => "fallita",
                                _ => "in corso"
                            };
                        }
                        if (string.Equals(progress.Phase, "transcription", StringComparison.OrdinalIgnoreCase))
                        {
                            states[jobIndex] = "in corso";
                            var overall = ((jobIndex + progress.Percent / 100) / jobs.Length) * 100;
                            OperationProgressBar.Value = overall;
                            AnalysisStatusText.Text = $"Coda {jobIndex + 1}/{jobs.Length}: {progress.Percent:0}%";
                            UpdateConsoleProgress(progress with { Part = jobIndex + 1, Parts = jobs.Length });
                        }
                        UpdateSelectedFilesPresentation(jobIndex, states);
                    },
                    token);
            }
            catch
            {
                CompleteConsoleProgress(succeeded: false);
                RestoreQueueButton.Visibility = Visibility.Visible;
                throw;
            }

            var failed = states.Count(state => state == "fallita");
            var completed = states.Count(state => state == "completata");
            var cancelled = states.Count(state => state == "annullata");
            if (result.ExitCode == 0 || failed == 0)
            {
                File.Delete(PendingQueuePath);
                File.Delete(controlPath);
                RestoreQueueButton.Visibility = Visibility.Collapsed;
                CompleteConsoleProgress(succeeded: true);
            }
            else
            {
                CompleteConsoleProgress(succeeded: false);
            }
            lastOutputDirectory = selectedOutputDirectory ?? Path.GetDirectoryName(selectedAudioFile!);
            OpenOutputButton.Visibility = Visibility.Visible;
            OperationProgressBar.Value = 100;
            AnalysisStatusText.Text = failed == 0
                ? cancelled == 0
                    ? $"Coda completata: {completed}/{jobs.Length}."
                    : $"Coda terminata: {completed} completati · {cancelled} annullati."
                : $"Coda terminata: {completed} completati · {failed} falliti · {cancelled} annullati.";
            ShowStatus(
                failed == 0 && cancelled == 0
                    ? $"Tutti i {completed} lavori della coda sono stati completati."
                    : failed == 0
                        ? $"Coda conclusa: {completed} completati e {cancelled} annullati."
                        : $"Coda terminata: {completed} completati, {failed} falliti e {cancelled} annullati.",
                failed == 0 && cancelled == 0
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Warning);
            currentBatchJobId = null;
        }

        private async Task TranscribeSourcesAsync(
            string model,
            IReadOnlyList<string> sourceFiles,
            CancellationToken token,
            int batchIndex = 0,
            int batchCount = 1,
            string? checkpointPath = null,
            string? outputName = null)
        {
            if (sourceFiles.Count == 0)
            {
                throw new InvalidOperationException("Nessun file selezionato.");
            }

            var primarySource = sourceFiles[0];
            var outputDirectory = checkpointPath is not null
                ? Path.GetDirectoryName(checkpointPath) ??
                    throw new InvalidOperationException("Cartella del checkpoint non disponibile.")
                : selectedOutputDirectory ?? Path.GetDirectoryName(primarySource) ??
                throw new InvalidOperationException("Cartella del file non disponibile.");
            var outputFormats = GetSelectedOutputFormats();
            if (outputFormats.Length == 0)
            {
                throw new InvalidOperationException("Seleziona almeno un formato di output.");
            }

            var arguments = new List<string>();
            if (checkpointPath is not null)
            {
                arguments.Add("--resume-checkpoint");
                arguments.Add(checkpointPath);
            }
            else
            {
                arguments.Add(primarySource);
                arguments.Add(model);
                arguments.AddRange(new[]
                {
                    "--device", GetSelectedTag(DeviceComboBox),
                    "--language", GetSelectedTag(LanguageComboBox),
                    "--beam-size", ((int)Math.Round(BeamSizeNumberBox.Value)).ToString(CultureInfo.InvariantCulture),
                    "--output-dir", outputDirectory,
                    "--output-name", outputName ?? Path.GetFileNameWithoutExtension(primarySource),
                    "--auto-rename"
                });
                foreach (var additionalAudioFile in sourceFiles.Skip(1))
                {
                    arguments.Add("--input-file");
                    arguments.Add(additionalAudioFile);
                }
                if (!VadToggle.IsOn)
                {
                    arguments.Add("--no-vad");
                }
                if (WordTimestampsToggle.IsOn)
                {
                    arguments.Add("--word-timestamps");
                }
                var interval = GetSelectedInterval();
                if (interval is not null)
                {
                    arguments.Add("--clip-start");
                    arguments.Add(interval.Start.ToString(CultureInfo.InvariantCulture));
                    arguments.Add("--clip-end");
                    arguments.Add(interval.End.ToString(CultureInfo.InvariantCulture));
                }
                foreach (var format in outputFormats)
                {
                    arguments.Add("--output-format");
                    arguments.Add(format);
                }
            }

            AnalysisStatusText.Text = batchCount > 1
                ? $"Coda {batchIndex + 1}/{batchCount}: {Path.GetFileName(primarySource)}"
                : checkpointPath is not null
                    ? "Ripresa della trascrizione..."
                    : "Trascrizione in corso...";
            if (batchCount == 1)
            {
                OperationProgressBar.Value = 0;
            }
            OperationProgressBar.IsIndeterminate = false;
            AppendConsoleEvent(
                checkpointPath is not null ? "RIPRESA" : "AVVIO",
                checkpointPath is not null
                    ? Path.GetFileName(checkpointPath)
                    : $"{sourceFiles.Count} " +
                      (sourceFiles.Count == 1 ? "traccia selezionata" : "tracce selezionate") +
                      $" · modello {model}",
                "LogInfoBrush");
            BeginConsoleProgress(batchCount > 1 ? "Lavoro" : "Parte");
            WhisperCommandResult result;
            try
            {
                result = await RunWhisperCommandAsync(
                    arguments,
                    AppendBackendLog,
                    progress =>
                    {
                        if (!string.Equals(progress.Phase, "transcription", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                        var overallPercent = batchCount > 1
                            ? ((batchIndex + progress.Percent / 100) / batchCount) * 100
                            : progress.Percent;
                        OperationProgressBar.Value = overallPercent;
                        AnalysisStatusText.Text = batchCount > 1
                            ? $"Coda {batchIndex + 1}/{batchCount}: {progress.Percent:0}%"
                            : $"Trascrizione: {progress.Percent:0}%";
                        UpdateConsoleProgress(
                            batchCount > 1
                                ? progress with
                                {
                                    Percent = progress.Percent,
                                    Part = batchIndex + 1,
                                    Parts = batchCount
                                }
                                : progress);
                    },
                    token);

                EnsureSuccess(result, "Trascrizione", "transcription");
                CompleteConsoleProgress(succeeded: true);
            }
            catch
            {
                CompleteConsoleProgress(succeeded: false);
                throw;
            }
            if (result.ExitCode != 0 && result.BackendReportedSuccess)
            {
                AppendConsoleEvent(
                    "AVVISO",
                    $"Output verificati; chiusura runtime non pulita ({result.ExitCode}).",
                    "LogWarningBrush");
            }
            if (result.Segments is not null || result.Paragraphs is not null)
            {
                var summaryParts = new List<string>();
                if (result.Segments is not null)
                {
                    summaryParts.Add($"{result.Segments} segmenti");
                }
                if (result.Paragraphs is not null)
                {
                    summaryParts.Add($"{result.Paragraphs} paragrafi");
                }
                AppendConsoleEvent("RISULTATO", string.Join(" · ", summaryParts), "LogMetadataBrush");
            }
            foreach (var outputPath in result.Outputs)
            {
                AppendConsoleEvent("OUTPUT", Path.GetFileName(outputPath), "LogOutputBrush");
            }
            AppendConsoleEvent("SUCCESSO", "Trascrizione completata", "LogSuccessBrush");
            lastOutputDirectory = outputDirectory;
            OperationProgressBar.Value = batchCount > 1
                ? ((batchIndex + 1d) / batchCount) * 100
                : 100;
            AnalysisStatusText.Text = batchCount > 1
                ? $"Lavoro {batchIndex + 1}/{batchCount} completato."
                : "Trascrizione completata.";
            OpenOutputButton.Visibility = Visibility.Visible;
            UpdateRecoverableCheckpoint();
        }

        private async Task<bool?> ConfirmOverwriteAsync(
            string sourcePath,
            string outputDirectory,
            IReadOnlyCollection<string> outputFormats)
        {
            var baseName = Path.GetFileNameWithoutExtension(sourcePath) + "_trascrizione";
            var existing = outputFormats
                .Select(format => Path.Combine(outputDirectory, $"{baseName}.{format}"))
                .Where(File.Exists)
                .Select(Path.GetFileName)
                .ToArray();
            if (existing.Length == 0)
            {
                return false;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Sostituire la trascrizione esistente?",
                Content = $"Sono già presenti: {string.Join(", ", existing)}. I file verranno sostituiti.",
                PrimaryButtonText = "Sostituisci",
                CloseButtonText = "Annulla",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? true : null;
        }

        private async Task<bool> CheckModelDownloadedAsync(string model, CancellationToken token)
        {
            var result = await RunWhisperCommandAsync(
                new[] { "--check-model", model },
                _ => { },
                null,
                token);
            return result.ExitCode == 0;
        }

        private async Task<WhisperCommandResult> RunWhisperCommandAsync(
            IEnumerable<string> arguments,
            Action<string> onOutputLine,
            Action<ProgressUpdate>? onProgress,
            CancellationToken token)
        {
            var backend = FindBackend();
            if (backend is null)
            {
                return new WhisperCommandResult(
                    1,
                    "Backend/Whisper.py non trovato. Ricompila o pubblica includendo la cartella Backend.");
            }

            await processGate.WaitAsync(token);
            try
            {
                var pythonExecutable = FindPythonExecutable(backend.RootDirectory);
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExecutable,
                    WorkingDirectory = backend.RootDirectory,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                startInfo.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
                startInfo.Environment["WHISPERLS_PROGRESS"] = "1";
                var runtimeRoot = Path.GetDirectoryName(pythonExecutable);
                if (!string.IsNullOrWhiteSpace(runtimeRoot) &&
                    pythonExecutable.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var nativeDirectories = new[]
                    {
                        runtimeRoot,
                        Path.Combine(runtimeRoot, "Lib", "site-packages", "nvidia", "cublas", "bin"),
                        Path.Combine(runtimeRoot, "Lib", "site-packages", "nvidia", "cudnn", "bin")
                    }.Where(Directory.Exists);
                    startInfo.Environment["PATH"] = string.Join(
                        Path.PathSeparator,
                        nativeDirectories.Append(startInfo.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty));
                }
                startInfo.ArgumentList.Add(backend.ScriptPath);
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return new WhisperCommandResult(1, "Processo Python non avviato.");
                }

                lock (processSync)
                {
                    activeProcess = process;
                }

                var lastLine = string.Empty;
                var lastErrorLine = string.Empty;
                var backendResult = new BackendResultUpdate(null, false, null, null, 0, Array.Empty<string>());
                var outputSync = new object();
                async Task PumpAsync(StreamReader reader, bool isErrorStream)
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (TryReadProgressLine(line, out var progress))
                        {
                            DispatcherQueue.TryEnqueue(() => onProgress?.Invoke(progress));
                            continue;
                        }

                        if (TryReadResultLine(line, out var parsedResult))
                        {
                            lock (outputSync)
                            {
                                backendResult = parsedResult;
                            }
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }
                        lock (outputSync)
                        {
                            lastLine = line;
                            if (isErrorStream && LooksLikeErrorLine(line))
                            {
                                lastErrorLine = line;
                            }
                        }
                        DispatcherQueue.TryEnqueue(() => onOutputLine(line));
                    }
                }

                var stdoutTask = PumpAsync(process.StandardOutput, isErrorStream: false);
                var stderrTask = PumpAsync(process.StandardError, isErrorStream: true);
                try
                {
                    await process.WaitForExitAsync(token);
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    await Task.WhenAll(
                        IgnoreFailureAsync(stdoutTask),
                        IgnoreFailureAsync(stderrTask));
                    throw;
                }

                lock (outputSync)
                {
                    return new WhisperCommandResult(
                        process.ExitCode,
                        string.IsNullOrWhiteSpace(lastErrorLine) ? lastLine : lastErrorLine,
                        backendResult.Succeeded,
                        backendResult.Operation,
                        backendResult.Segments,
                        backendResult.Paragraphs,
                        backendResult.Parts,
                        backendResult.Outputs);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new WhisperCommandResult(1, exception.Message);
            }
            finally
            {
                lock (processSync)
                {
                    activeProcess = null;
                }
                processGate.Release();
            }
        }

        private static async Task IgnoreFailureAsync(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }

        private static bool TryReadProgressLine(string line, out ProgressUpdate progress)
        {
            progress = new ProgressUpdate(string.Empty, 0, 0, 0, 0, 0);
            if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(line[ProgressPrefix.Length..]);
                var root = document.RootElement;
                var phase = root.TryGetProperty("phase", out var phaseProperty)
                    ? phaseProperty.GetString() ?? string.Empty
                    : string.Empty;
                var percent = root.TryGetProperty("percent", out var percentProperty)
                    ? Math.Clamp(percentProperty.GetDouble(), 0, 100)
                    : 0;
                progress = new ProgressUpdate(
                    phase,
                    percent,
                    GetJsonDouble(root, "downloadedBytes"),
                    GetJsonDouble(root, "totalBytes"),
                    GetJsonInt(root, "part"),
                    GetJsonInt(root, "parts"),
                    root.TryGetProperty("status", out var statusProperty)
                        ? statusProperty.GetString() ?? string.Empty
                        : string.Empty,
                    GetJsonInt(root, "jobIndex"),
                    GetJsonInt(root, "jobCount"),
                    root.TryGetProperty("jobId", out var jobIdProperty)
                        ? jobIdProperty.GetString()
                        : null,
                    root.TryGetProperty("jobSource", out var jobSourceProperty)
                        ? jobSourceProperty.GetString()
                        : null);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadResultLine(
            string line,
            out BackendResultUpdate result)
        {
            result = new BackendResultUpdate(null, false, null, null, 0, Array.Empty<string>());
            if (!line.StartsWith(ResultPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(line[ResultPrefix.Length..]);
                var root = document.RootElement;
                var operation = root.TryGetProperty("operation", out var operationProperty)
                    ? operationProperty.GetString()
                    : null;
                var succeeded = root.TryGetProperty("status", out var statusProperty) &&
                    string.Equals(
                        statusProperty.GetString(),
                        "success",
                        StringComparison.OrdinalIgnoreCase);
                var outputs = root.TryGetProperty("outputs", out var outputsProperty) &&
                    outputsProperty.ValueKind == JsonValueKind.Array
                    ? outputsProperty.EnumerateArray()
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!)
                        .ToArray()
                    : Array.Empty<string>();
                result = new BackendResultUpdate(
                    operation,
                    succeeded,
                    GetJsonNullableInt(root, "segments"),
                    GetJsonNullableInt(root, "paragraphs"),
                    GetJsonInt(root, "parts"),
                    outputs);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool LooksLikeErrorLine(string line)
        {
            return line.Contains("errore", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("traceback", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("fatal", StringComparison.OrdinalIgnoreCase);
        }

        private static double GetJsonDouble(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) &&
                   property.TryGetDouble(out var value)
                ? Math.Max(0, value)
                : 0;
        }

        private static int GetJsonInt(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) &&
                   property.TryGetInt32(out var value)
                ? Math.Max(0, value)
                : 0;
        }

        private static int? GetJsonNullableInt(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) &&
                   property.TryGetInt32(out var value)
                ? Math.Max(0, value)
                : null;
        }

        private static void EnsureSuccess(
            WhisperCommandResult result,
            string operation,
            string? expectedBackendOperation = null)
        {
            var confirmedSuccess = result.BackendReportedSuccess &&
                expectedBackendOperation is not null &&
                string.Equals(
                    result.BackendOperation,
                    expectedBackendOperation,
                    StringComparison.OrdinalIgnoreCase);
            if (result.ExitCode != 0 && !confirmedSuccess)
            {
                throw new InvalidOperationException(
                    $"{operation} non completato. {result.LastLine}".Trim());
            }
        }

        private void SetBusy(bool busy)
        {
            BrowseFileButton.IsEnabled = !busy;
            MultiFileControls.IsEnabled = !busy;
            OpenModelManagerButton.IsEnabled = !busy;
            ModelComboBox.IsEnabled = !busy;
            LanguageComboBox.IsEnabled = !busy;
            DeviceComboBox.IsEnabled = !busy;
            BeamSizeNumberBox.IsEnabled = !busy;
            VadToggle.IsEnabled = !busy;
            WordTimestampsToggle.IsEnabled = !busy;
            LimitIntervalCheckBox.IsEnabled = !busy;
            ClipStartTextBox.IsEnabled = !busy && LimitIntervalCheckBox.IsChecked == true;
            ClipEndTextBox.IsEnabled = !busy && LimitIntervalCheckBox.IsChecked == true;
            TxtOutputCheckBox.IsEnabled = !busy;
            SrtOutputCheckBox.IsEnabled = !busy;
            DocxOutputCheckBox.IsEnabled = !busy;
            ChooseOutputFolderButton.IsEnabled = !busy;
            PrimaryActionButton.IsEnabled = !busy;
            ResumeCheckpointButton.IsEnabled = !busy;
            ResumeCheckpointButton.Visibility = !busy && recoverableCheckpointPath is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
            CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            RestoreQueueButton.IsEnabled = !busy;
        }

        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            StatusInfoBar.Message = message;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;
        }

        private void ClearStatus()
        {
            StatusInfoBar.IsOpen = false;
            StatusInfoBar.Message = string.Empty;
        }

        private void ConsoleScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            const double tailTolerance = 10;
            consoleShouldFollowTail =
                ConsoleScrollViewer.ScrollableHeight <= tailTolerance ||
                ConsoleScrollViewer.VerticalOffset >=
                    ConsoleScrollViewer.ScrollableHeight - tailTolerance;
        }

        private void BeginConsoleProgress(string unitLabel = "Parte")
        {
            consoleProgressUnitLabel = unitLabel;
            ConsoleLiveProgressPanel.Visibility = Visibility.Visible;
            UpdateConsoleProgress(new ProgressUpdate("transcription", 0, 0, 0, 1, selectedAudioFiles.Count));
        }

        private void UpdateConsoleProgress(ProgressUpdate progress)
        {
            const int barWidth = 18;
            var normalizedPercent = Math.Clamp(progress.Percent, 0, 100);
            var completedCells = Math.Clamp(
                (int)Math.Round(normalizedPercent / 100 * barWidth),
                0,
                barWidth);
            var partPrefix = progress.Parts > 1 && progress.Part > 0
                ? $"{consoleProgressUnitLabel} {progress.Part}/{progress.Parts} · "
                : string.Empty;
            ConsoleLiveProgressText.Text =
                $"{partPrefix}Sbobinatura {normalizedPercent,3:0}% |" +
                new string('█', completedCells) +
                new string('░', barWidth - completedCells) +
                "|";
        }

        private void CompleteConsoleProgress(bool succeeded)
        {
            ConsoleLiveProgressPanel.Visibility = Visibility.Collapsed;
            ConsoleLiveProgressText.Text = string.Empty;
            if (!succeeded)
            {
                AppendConsoleEvent("ERRORE", "Trascrizione interrotta", "LogErrorBrush");
            }
        }

        private void AppendLog(string line)
        {
            line = line.Replace("\r", string.Empty).Replace("\b", string.Empty).TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            var brushKey = GetLogBrushKey(line);
            var category = brushKey switch
            {
                "LogErrorBrush" => "ERRORE",
                "LogWarningBrush" => "AVVISO",
                "LogSuccessBrush" => "SUCCESSO",
                "LogProgressBrush" => "PROGRESSO",
                "LogModelBrush" => "MODELLO",
                "LogMetadataBrush" => "DETTAGLI",
                "LogOutputBrush" => "OUTPUT",
                _ => "INFO"
            };
            AppendConsoleEvent(category, line, brushKey);
        }

        private void AppendBackendLog(string line)
        {
            var normalized = line.Trim();
            if (normalized.StartsWith("Controllo modello locale:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Modello non presente in cache:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Modello disponibile:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Uso modello locale:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Tento inizializzazione modello", StringComparison.OrdinalIgnoreCase) ||
                (normalized.StartsWith("Parte ", StringComparison.OrdinalIgnoreCase) &&
                 !normalized.StartsWith("Parte completata ", StringComparison.OrdinalIgnoreCase) &&
                 !normalized.StartsWith("Parte recuperata ", StringComparison.OrdinalIgnoreCase)) ||
                normalized.StartsWith("Dispositivo usato:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Compute type usato:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Segmenti:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Paragrafi:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Creato:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (normalized.StartsWith("Modello inizializzato su ", StringComparison.OrdinalIgnoreCase))
            {
                var details = normalized["Modello inizializzato su ".Length..].TrimEnd('.');
                details = details.Replace(" con compute_type=", " · ", StringComparison.OrdinalIgnoreCase);
                AppendConsoleEvent(
                    "MODELLO",
                    $"{GetSelectedModel()} pronto · {details.ToUpperInvariant()}",
                    "LogModelBrush");
                return;
            }
            if (normalized.StartsWith("Parte completata ", StringComparison.OrdinalIgnoreCase))
            {
                var details = normalized["Parte completata ".Length..];
                var separatorIndex = details.IndexOf(':');
                var message = separatorIndex > 0
                    ? $"{details[..separatorIndex]} completata · {details[(separatorIndex + 1)..].Trim()}"
                    : $"{details} · completata";
                AppendConsoleEvent("PARTE", message, "LogSuccessBrush");
                return;
            }
            if (normalized.StartsWith("Parte recuperata ", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsoleEvent(
                    "RIPRESA",
                    normalized["Parte recuperata ".Length..],
                    "LogMetadataBrush");
                return;
            }
            if (normalized.StartsWith("Download modello in corso:", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsoleEvent("MODELLO", "Preparazione dei file del modello", "LogModelBrush");
                return;
            }
            if (normalized.Equals("Download modello completato.", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsoleEvent("MODELLO", "File del modello pronti", "LogSuccessBrush");
                return;
            }
            if (normalized.StartsWith("Lingua parte ", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsoleEvent("LINGUA", normalized["Lingua ".Length..], "LogMetadataBrush");
                return;
            }
            if (normalized.Equals("Checkpoint di recupero attivo.", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsoleEvent("PROGETTO", "Recupero automatico attivo", "LogInfoBrush");
                return;
            }
            if (normalized.StartsWith("Ripresa checkpoint:", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsoleEvent(
                    "PROGETTO",
                    normalized["Ripresa checkpoint:".Length..].Trim(),
                    "LogInfoBrush");
                return;
            }
            if (normalized.Equals("Checkpoint temporaneo eliminato.", StringComparison.OrdinalIgnoreCase))
            {
                AppendConsoleEvent("PROGETTO", "Checkpoint temporaneo rimosso", "LogSuccessBrush");
                return;
            }

            AppendLog(normalized);
        }

        private void AppendConsoleEvent(string category, string message, string brushKey)
        {
            const int maxCharacters = 30_000;
            ConsoleEmptyText.Visibility = Visibility.Collapsed;

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 3)
            };
            paragraph.Inlines.Add(new Run
            {
                Text = $"[{DateTime.Now:HH:mm:ss}] ",
                Foreground = GetLogBrush("LogTimestampBrush")
            });
            paragraph.Inlines.Add(new Run
            {
                Text = $"{category,-10}",
                Foreground = GetLogBrush(brushKey),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            paragraph.Inlines.Add(new Run
            {
                Text = message,
                Foreground = GetLogBrush(brushKey)
            });
            LogRichTextBlock.Blocks.Add(paragraph);

            var entryLength = category.Length + message.Length + 21;
            logEntryLengths.Enqueue(entryLength);
            logCharacterCount += entryLength;
            while (logCharacterCount > maxCharacters &&
                   logEntryLengths.TryDequeue(out var removedLength) &&
                   LogRichTextBlock.Blocks.Count > 0)
            {
                logCharacterCount -= removedLength;
                LogRichTextBlock.Blocks.RemoveAt(0);
            }

            ScrollConsoleToTailIfFollowing();
        }

        private void ScrollConsoleToTailIfFollowing()
        {
            if (!consoleShouldFollowTail)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!consoleShouldFollowTail)
                {
                    return;
                }
                ConsoleScrollViewer.UpdateLayout();
                ConsoleScrollViewer.ChangeView(
                    null,
                    ConsoleScrollViewer.ScrollableHeight,
                    null,
                    disableAnimation: true);
            });
        }

        private static string GetLogBrushKey(string line)
        {
            var normalized = line.ToLowerInvariant();
            if (ContainsAny(
                normalized,
                "errore", "error", "failed", "fallito", "eccezione", "exception"))
            {
                return "LogErrorBrush";
            }
            if (ContainsAny(normalized, "warning", "avviso", "attenzione", "warn"))
            {
                return "LogWarningBrush";
            }
            if (ContainsAny(
                normalized,
                "completato", "completata", "pronto", "scaricato", "salvato",
                "generato", "success", "riuscito", "creato:"))
            {
                return "LogSuccessBrush";
            }
            if (ContainsAny(normalized, "sbobinatura", "download:", "%"))
            {
                return "LogProgressBrush";
            }
            if (ContainsAny(
                normalized,
                "modello", "cuda", "cpu", "compute type", "compute_type", "dispositivo"))
            {
                return "LogModelBrush";
            }
            if (ContainsAny(
                normalized,
                "lingua", "segmenti:", "paragrafi:", "durata", "prob="))
            {
                return "LogMetadataBrush";
            }
            if (ContainsAny(normalized, "output", "cartella", "percorso"))
            {
                return "LogOutputBrush";
            }
            if (ContainsAny(normalized, "inizializzato", "inizializzazione", "rilevato"))
            {
                return "LogInfoBrush";
            }
            return "LogNeutralBrush";
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            return values.Any(value => text.Contains(value, StringComparison.Ordinal));
        }

        private static Brush GetLogBrush(string resourceKey)
        {
            return (Brush)Application.Current.Resources[resourceKey];
        }

        private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (lastOutputDirectory is null || !Directory.Exists(lastOutputDirectory))
            {
                ShowStatus("La cartella di output non è disponibile.", InfoBarSeverity.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = lastOutputDirectory,
                UseShellExecute = true
            });
        }

        private static bool InitializePicker(object picker)
        {
            if (App.MainWindow is null)
            {
                return false;
            }

            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(App.MainWindow));
            return true;
        }

        private static string GetSelectedTag(ComboBox comboBox)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        }

        private static BackendLocation? FindBackend()
        {
            var bundledScript = Path.Combine(AppContext.BaseDirectory, "Backend", "Whisper.py");
            if (File.Exists(bundledScript))
            {
                return new BackendLocation(bundledScript, Path.GetDirectoryName(bundledScript)!);
            }

            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var script = Path.Combine(current.FullName, "Whisper.py");
                if (File.Exists(script))
                {
                    return new BackendLocation(script, current.FullName);
                }
                current = current.Parent;
            }
            return null;
        }

        private static string FindPythonExecutable(string backendDirectory)
        {
            var configured = Environment.GetEnvironmentVariable("WHISPERLS_PYTHON");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            var bundledPython = Path.Combine(AppContext.BaseDirectory, "Runtime", "python.exe");
            if (File.Exists(bundledPython))
            {
                return bundledPython;
            }

            var current = new DirectoryInfo(backendDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, ".venv", "Scripts", "python.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                current = current.Parent;
            }
            return "python";
        }

        private static async Task<TimeSpan> TryGetMediaDurationAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (VideoExtensions.Contains(extension))
                {
                    var video = await file.Properties.GetVideoPropertiesAsync();
                    return video.Duration;
                }
                var music = await file.Properties.GetMusicPropertiesAsync();
                return music.Duration;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private static async Task<HardwareProfile> DetectHardwareProfileAsync(CancellationToken token)
        {
            var systemMemoryGb = Math.Max(
                0,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
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
                    return new HardwareProfile(
                        false,
                        0,
                        Environment.ProcessorCount,
                        systemMemoryGb);
                }

                string output;
                try
                {
                    var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                    output = await outputTask;
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    throw;
                }
                var firstLine = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                return process.ExitCode == 0 &&
                       double.TryParse(firstLine, NumberStyles.Any, CultureInfo.InvariantCulture, out var memoryMiB)
                    ? new HardwareProfile(
                        true,
                        memoryMiB / 1024d,
                        Environment.ProcessorCount,
                        systemMemoryGb)
                    : new HardwareProfile(
                        false,
                        0,
                        Environment.ProcessorCount,
                        systemMemoryGb);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new HardwareProfile(
                    false,
                    0,
                    Environment.ProcessorCount,
                    systemMemoryGb);
            }
            catch
            {
                return new HardwareProfile(
                    false,
                    0,
                    Environment.ProcessorCount,
                    systemMemoryGb);
            }
        }

        private static AnalysisEstimate BuildEstimate(
            string model,
            TimeSpan duration,
            HardwareProfile hardware,
            string requestedDevice)
        {
            var canEstimateGpu = requestedDevice != "cpu" && hardware.HasNvidiaGpu;
            var realTimeFactor = canEstimateGpu
                ? model switch
                {
                    "tiny" or "tiny.en" => 0.05,
                    "base" or "base.en" => 0.08,
                    "small" or "small.en" => 0.15,
                    "medium" or "medium.en" => hardware.GpuMemoryGb >= 10 ? 0.20 : 0.34,
                    _ => hardware.GpuMemoryGb >= 10 ? 0.30 : 0.48
                }
                : model switch
                {
                    "tiny" or "tiny.en" => 0.45,
                    "base" or "base.en" => 0.70,
                    "small" or "small.en" => 1.20,
                    "medium" or "medium.en" => 1.85,
                    _ => 2.40
                };
            var cpuAdjustment = canEstimateGpu
                ? 1.0
                : Environment.ProcessorCount switch
                {
                    >= 16 => 0.75,
                    >= 12 => 0.88,
                    >= 8 => 1.0,
                    >= 6 => 1.20,
                    _ => 1.45
                };
            return new AnalysisEstimate(
                TimeSpan.FromSeconds(Math.Max(8, duration.TotalSeconds * realTimeFactor * cpuAdjustment)));
        }

        private static string FormatMediaDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours} h {duration.Minutes} min";
            }
            if (duration.TotalMinutes >= 1)
            {
                return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))} min";
            }
            return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))} s";
        }

        private static string FormatProgressBytes(double downloaded, double total)
        {
            return total > 0
                ? $"{FormatBytes(downloaded)} / {FormatBytes(total)}"
                : $"{FormatBytes(downloaded)} scaricati";
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            var value = Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value:0.0} {units[unit]}";
        }

        private static void TryKill(Process? process)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        private sealed record BackendLocation(string ScriptPath, string RootDirectory);
        private sealed record ProgressUpdate(
            string Phase,
            double Percent,
            double DownloadedBytes,
            double TotalBytes,
            int Part,
            int Parts,
            string Status = "",
            int JobIndex = 0,
            int JobCount = 0,
            string? JobId = null,
            string? JobSource = null);
        private sealed record BackendResultUpdate(
            string? Operation,
            bool Succeeded,
            int? Segments,
            int? Paragraphs,
            int Parts,
            string[] Outputs);
        private sealed record WhisperCommandResult(
            int ExitCode,
            string LastLine,
            bool BackendReportedSuccess = false,
            string? BackendOperation = null,
            int? Segments = null,
            int? Paragraphs = null,
            int Parts = 0,
            string[]? OutputFiles = null)
        {
            public IReadOnlyList<string> Outputs => OutputFiles ?? Array.Empty<string>();
        }
        private sealed record HardwareProfile(
            bool HasNvidiaGpu,
            double GpuMemoryGb,
            int LogicalProcessors,
            double SystemMemoryGb);
        private sealed class TranscriptionQueueJob
        {
            public TranscriptionQueueJob(
                IEnumerable<string> sources,
                string? key = null,
                string? outputName = null)
            {
                Sources = sources.ToList();
                Key = key ?? Guid.NewGuid().ToString("N");
                OutputName = outputName;
            }

            public List<string> Sources { get; }
            public string Key { get; }
            public string? OutputName { get; set; }

            public TranscriptionQueueJob Clone() => new(Sources, Key, OutputName);
        }
        public sealed class QueueManagerItem
        {
            public required string JobKey { get; init; }
            public required string Title { get; init; }
            public required string KindLabel { get; init; }
            public required string SourcesLabel { get; init; }
            public string OutputName { get; set; } = string.Empty;
            public required string DefaultOutputName { get; init; }
        }
        public sealed class ModelManagerItem
        {
            public required string ModelName { get; init; }
            public required string ClassLabel { get; init; }
            public required string StatusLabel { get; init; }
            public required Brush StatusBrush { get; init; }
            public required Brush StatusBackgroundBrush { get; init; }
            public required string ScoreLabel { get; init; }
            public required Brush ScoreBrush { get; init; }
            public required int Score { get; init; }
            public required bool IsInstalled { get; init; }
            public required Visibility DownloadActionVisibility { get; init; }
            public required Visibility UninstallActionVisibility { get; init; }
            public required string UninstallAutomationName { get; init; }
        }
        public sealed class ModelOption : INotifyPropertyChanged
        {
            private bool isInstalled;

            public ModelOption(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public Thickness ReadyBorderThickness =>
                isInstalled ? new Thickness(1) : new Thickness(0);
            public Brush ItemBorderBrush => isInstalled
                ? GetResourceBrush("StatusSuccessBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            public Visibility InstalledLabelVisibility =>
                isInstalled ? Visibility.Visible : Visibility.Collapsed;
            public string AutomationName =>
                isInstalled ? $"{Name}, scaricato" : $"{Name}, non scaricato";

            public event PropertyChangedEventHandler? PropertyChanged;

            public void SetInstalled(bool installed)
            {
                if (isInstalled == installed)
                {
                    return;
                }

                isInstalled = installed;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(ReadyBorderThickness)));
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(ItemBorderBrush)));
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(InstalledLabelVisibility)));
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(AutomationName)));
            }

            public override string ToString()
            {
                return Name;
            }
        }
        private sealed record AnalysisEstimate(TimeSpan EstimatedDuration);
        private sealed record TimeInterval(double Start, double End);
    }
}
