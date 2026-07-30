using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PakAssetStudio.Models;
using PakAssetStudio.Services;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using MessageBox = System.Windows.MessageBox;
using static PakAssetStudio.Services.LocalizationService;

namespace PakAssetStudio;

public partial class MainWindow : FluentWindow
{
    private readonly ProcessRunner _processRunner = new();
    private readonly PakToolService _pakToolService;
    private readonly WorkflowService _workflowService;
    private CancellationTokenSource? _cancellation;
    private readonly UiLogBuffer _uiLogs = new(count => TextFormat("Log_UiOmitted", count));
    private readonly DispatcherTimer _logFlushTimer;
    private bool _isBusy;
    private string? _lastScannedDirectory;
    private bool _profileManuallySet;
    private bool _updatingProfile;
    private bool _selectingLanguage;
    private string? _detectedProfileRange;
    private (int Total, int Eligible, long Bytes)? _scanSummary;
    private readonly ICollectionView _pakView;

    public ObservableCollection<PakEntry> PakEntries { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 16));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 16));
        DataContext = this;
        _pakToolService = new PakToolService(_processRunner);
        _workflowService = new WorkflowService(_processRunner, _pakToolService);
        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogs();
        _logFlushTimer.Start();
        Closing += MainWindow_Closing;
        // 可编辑 ComboBox 的内部 TextBox 输入通过冒泡的 TextChanged 事件捕获
        GameProfileBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(GameProfileBox_Changed));
        PakEntries.CollectionChanged += (_, _) => UpdatePakEmptyState();
        // 默认隐藏不支持的 PAK，由“显示不支持的包”开关控制
        _pakView = CollectionViewSource.GetDefaultView(PakEntries);
        _pakView.Filter = entry => ShowUnsupportedCheck.IsChecked == true || ((PakEntry)entry).CanAttemptExtraction;

        _selectingLanguage = true;
        LanguageBox.ItemsSource = Instance.AvailableLanguages;
        LanguageBox.SelectedItem = Instance.AvailableLanguages.FirstOrDefault(l => l.Code == Instance.CurrentCode);
        _selectingLanguage = false;
        Instance.LanguageChanged += (_, _) => OnLanguageChanged();

        VersionText.Text = GetDisplayVersion();

        UpdateOptionState();
    }

    // 显示 InformationalVersion；预发布版本附带短提交号。
    private static string GetDisplayVersion()
    {
        var info = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "v?";
        var plus = info.IndexOf('+');
        if (plus < 0) return "v" + info;
        var hash = info[(plus + 1)..];
        if (hash.Length > 7) hash = hash[..7];
        return $"v{info[..plus]}+{hash}";
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder(Text("Choose_GameDir"), GameDirectoryBox.Text);
        if (path is null) return;
        GameDirectoryBox.Text = path;
        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
            OutputDirectoryBox.Text = SuggestOutputDirectory(path);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder(Text("Choose_OutputDir"), OutputDirectoryBox.Text);
        if (path is not null) OutputDirectoryBox.Text = path;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await ScanPaksAsync(showNoPakMessage: true);
    }

    private async Task<bool> ScanPaksAsync(bool showNoPakMessage)
    {
        var root = GameDirectoryBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show(Text("Dialog_InvalidPath"), Text("Dialog_InvalidPathTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        SetBusy(true, Text("Status_Scanning"));
        TaskProgress.Value = 0;
        _lastScannedDirectory = null;
        _scanSummary = null;
        SetProfileRangeHint(null);
        WorkspaceSummaryText.Text = Text("Workspace_SummaryEmpty");
        if (!_profileManuallySet) ClearAutomaticProfile();
        PakEntries.Clear();
        AppendLog(TextFormat("Log_Scanning", root), UiLogLevel.Stage);
        _cancellation?.Dispose();
        var scanCancellation = new CancellationTokenSource();
        _cancellation = scanCancellation;

        try
        {
            var priority = LowResourceCheck.IsChecked == true ? ProcessPriorityClass.BelowNormal : (ProcessPriorityClass?)null;
            var entries = await _pakToolService.ScanAsync(root, EmptyToNull(AesKeyBox.Password), (done, total) =>
            {
                TaskProgress.Value = total == 0 ? 0 : done * 100d / total;
                StageText.Text = TextFormat("Status_ScanProgress", done, total);
            }, scanCancellation.Token, priority);
            foreach (var entry in entries) PakEntries.Add(entry);

            var eligible = entries.Count(entry => entry.CanAttemptExtraction);
            var bytes = entries.Where(entry => entry.CanAttemptExtraction).Sum(entry => entry.SizeBytes);
            _scanSummary = (entries.Count, eligible, bytes);
            UpdateScanSummaryText();
            AppendLog(TextFormat("Log_ScanDone", eligible, entries.Count), UiLogLevel.Stage);
            foreach (var entry in entries.Where(entry => !entry.CanAttemptExtraction).Take(20))
                AppendLog(TextFormat("Log_PakSkipped", entry.Name, entry.ScanError ?? entry.Status), UiLogLevel.Warning);
            if (entries.Count(entry => !entry.CanAttemptExtraction) > 20)
                AppendLog(TextFormat("Log_PakSkippedMore", entries.Count(entry => !entry.CanAttemptExtraction) - 20), UiLogLevel.Warning);
            ApplyDetectedProfile(entries);
            _lastScannedDirectory = Path.GetFullPath(root);

            if (entries.Count == 0 && showNoPakMessage)
                MessageBox.Show(Text("Dialog_NoPaks"), Text("Dialog_ScanDoneTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            else if (eligible == 0 && showNoPakMessage)
                MessageBox.Show(Text("Dialog_NoValidPaks"), Text("Dialog_CannotReadTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return eligible > 0;
        }
        catch (OperationCanceledException)
        {
            AppendLog(Text("Log_ScanCancelled"), UiLogLevel.Warning);
            return false;
        }
        catch (Exception ex)
        {
            AppendLog(TextFormat("Log_ScanFailed", ex.Message), UiLogLevel.Error);
            MessageBox.Show(ex.Message, Text("Dialog_ScanFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_cancellation, scanCancellation))
                _cancellation = null;
            scanCancellation.Dispose();
            SetBusy(false, Text("App_Ready"));
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var gameDirectory = GameDirectoryBox.Text.Trim();
        var outputDirectory = OutputDirectoryBox.Text.Trim();
        if (!Directory.Exists(gameDirectory))
        {
            MessageBox.Show(Text("Dialog_InvalidPath"), Text("Dialog_InvalidPathTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show(Text("Dialog_ChooseOutput"), Text("Dialog_InvalidPathTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string normalizedSelectedGame;
        string normalizedProtectedGame;
        string normalizedOutput;
        try
        {
            normalizedSelectedGame = PathSafety.ResolvePhysicalPath(gameDirectory);
            normalizedProtectedGame = PathSafety.ResolvePhysicalPath(PathSafety.GetProtectedGameDirectory(gameDirectory));
            normalizedOutput = PathSafety.ResolvePhysicalPath(outputDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(TextFormat("Dialog_InvalidPathDetail", ex.Message), Text("Dialog_InvalidPathTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (PathSafety.IsSameOrChild(normalizedOutput, normalizedSelectedGame) ||
            PathSafety.IsSameOrChild(normalizedOutput, normalizedProtectedGame))
        {
            MessageBox.Show(Text("Dialog_UnsafeOutput"), Text("Dialog_UnsafeOutputTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = BuildOptions(gameDirectory, outputDirectory);
        if (!options.ExtractPaks && !options.ExportModels && !options.ExportTextures && !options.ConvertToFbx)
        {
            MessageBox.Show(Text("Dialog_NoSteps"), Text("Dialog_NoStepsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (options.ConvertToFbx && options.ExportTextures && !options.ExportModels)
        {
            MessageBox.Show(Text("Error_FbxRequiresModels"), Text("Dialog_InvalidStepsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (options.ExtractPaks)
        {
            if (_lastScannedDirectory is null || !Path.GetFullPath(gameDirectory).Equals(_lastScannedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                if (!await ScanPaksAsync(showNoPakMessage: true)) return;
            }
            if (!PakEntries.Any(entry => entry.CanAttemptExtraction))
            {
                MessageBox.Show(Text("Dialog_NoValidPaks"), Text("Dialog_CannotReadTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var skipped = PakEntries.Count(entry => !entry.CanAttemptExtraction);
            if (skipped > 0)
            {
                var answer = MessageBox.Show(TextFormat("Dialog_ConfirmSkippedPaks", skipped), Text("Dialog_ConfirmSkippedPaksTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
        }

        // 扫描可能刚刚自动填写了唯一可确定的 profile，因此在这里重新读取界面选项。
        options = BuildOptions(gameDirectory, outputDirectory);
        if ((options.ExportModels || options.ExportTextures) && string.IsNullOrWhiteSpace(options.GameProfile))
        {
            MessageBox.Show(Text("Dialog_ChooseProfile"), Text("Dialog_ChooseProfileTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (options.Overwrite)
        {
            var answer = MessageBox.Show(
                TextFormat("Dialog_ConfirmOverwrite", options.OutputDirectory),
                Text("Dialog_ConfirmOverwriteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        _cancellation?.Dispose();
        var diskCheckCancellation = new CancellationTokenSource();
        _cancellation = diskCheckCancellation;
        SetBusy(true, Text("Status_CheckingDisk"));
        try
        {
            try
            {
                var (required, free) = await Task.Run(
                    () => WorkflowService.EstimateDiskSpace(PakEntries, options, diskCheckCancellation.Token),
                    diskCheckCancellation.Token);
                if (free < required)
                {
                    var answer = MessageBox.Show(
                        TextFormat("Dialog_DiskSpace", FormatBytes(required), FormatBytes(free)),
                        Text("Dialog_DiskSpaceTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return;
                }
            }
            catch (OperationCanceledException)
            {
                StageText.Text = Text("Status_TaskCancelled");
                return;
            }
            catch (Exception ex)
            {
                var answer = MessageBox.Show(
                    TextFormat("Dialog_DiskCheckFailedContinue", ex.Message),
                    Text("Dialog_DiskCheckTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, diskCheckCancellation))
                _cancellation = null;
            diskCheckCancellation.Dispose();
            SetBusy(false, Text("App_Ready"));
        }

        _uiLogs.Clear();
        LogBox.Document.Blocks.Clear();
        SetBusy(true, Text("Status_Preparing"));
        _cancellation?.Dispose();
        var workflowCancellation = new CancellationTokenSource();
        _cancellation = workflowCancellation;
        try
        {
            await _workflowService.RunAsync(PakEntries.ToList(), options, AppendLog, UpdateProgress, workflowCancellation.Token);
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(99, 199, 168));
            MessageBox.Show(Text("Dialog_TaskDone"), Text("Dialog_TaskDoneTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog(Text("Log_TaskCancelled"), UiLogLevel.Warning);
            StageText.Text = Text("Status_TaskCancelled");
        }
        catch (Exception ex)
        {
            AppendLog(TextFormat("Log_TaskFailed", ex.Message), UiLogLevel.Error);
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(217, 111, 80));
            MessageBox.Show(ex.Message, Text("Dialog_TaskFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, workflowCancellation))
                _cancellation = null;
            workflowCancellation.Dispose();
            SetBusy(false, Text("App_Ready"));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StageText.Text = Text("Status_Cancelling");
        _cancellation?.Cancel();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = OutputDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(Text("Dialog_OutputMissing"), Text("Dialog_OutputMissingTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (paths.Length == 0) return;
        var path = paths[0];
        GameDirectoryBox.Text = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
            OutputDirectoryBox.Text = SuggestOutputDirectory(GameDirectoryBox.Text);
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, GameDirectoryBox))
        {
            InvalidateScan();
            _profileManuallySet = false;
            if (GameProfileBox is not null)
            {
                _updatingProfile = true;
                GameProfileBox.Text = string.Empty;
                _updatingProfile = false;
                ProfileAutoHint.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void AesKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        InvalidateScan();
        if (!_profileManuallySet) ClearAutomaticProfile();
    }

    private void ClearAutomaticProfile()
    {
        _updatingProfile = true;
        GameProfileBox.Text = string.Empty;
        _updatingProfile = false;
        ProfileAutoHint.Visibility = Visibility.Collapsed;
    }

    private void InvalidateScan()
    {
        _lastScannedDirectory = null;
        _scanSummary = null;
        PakEntries.Clear();
        SetProfileRangeHint(null);
        if (WorkspaceSummaryText is not null)
            WorkspaceSummaryText.Text = Text("Workspace_SummaryEmpty");
    }

    private void GameProfileBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingProfile) return;
        _profileManuallySet = true;
        ProfileAutoHint.Visibility = Visibility.Collapsed;
    }

    private void ApplyDetectedProfile(IReadOnlyList<PakEntry> entries)
    {
        var detection = Ue4ProfileDetector.DetectDetailed(entries);
        if (detection is null)
        {
            SetProfileRangeHint(null);
            return;
        }
        SetProfileRangeHint(detection.Range);
        if (detection.IsAmbiguous || detection.SuggestedProfile is null)
        {
            if (!_profileManuallySet) ClearAutomaticProfile();
            ProfileAutoHint.Visibility = Visibility.Collapsed;
            AppendLog(TextFormat("Log_ProfileRange", detection.Range), UiLogLevel.Warning);
            return;
        }

        var detected = detection.SuggestedProfile;
        if (_profileManuallySet)
        {
            if (!GameProfileBox.Text.Trim().Equals(detected, StringComparison.OrdinalIgnoreCase))
                AppendLog(TextFormat("Log_ProfileHint", detected));
            return;
        }

        _updatingProfile = true;
        GameProfileBox.Text = detected;
        _updatingProfile = false;
        ProfileAutoHint.Visibility = Visibility.Visible;
        AppendLog(TextFormat("Log_ProfileDetected", detected), UiLogLevel.Stage);
    }

    private void SetProfileRangeHint(string? range)
    {
        _detectedProfileRange = string.IsNullOrWhiteSpace(range) ? null : range;
        UpdateProfileRangeHint();
    }

    private void UpdateProfileRangeHint()
    {
        if (ProfileRangeHint is null) return;
        if (_detectedProfileRange is null)
        {
            ProfileRangeHint.Text = string.Empty;
            ProfileRangeHint.Visibility = Visibility.Collapsed;
            return;
        }

        ProfileRangeHint.Text = TextFormat("Profile_RangeHint", _detectedProfileRange);
        ProfileRangeHint.Visibility = Visibility.Visible;
    }

    private bool _applyingPreset;

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 阶段（IsLoaded 为 false）及程序化切换预设时不重复套用
        if (_applyingPreset || !IsLoaded || ExtractCheck is null) return;
        _applyingPreset = true;
        switch (PresetBox.SelectedIndex)
        {
            case 0: // 完整导出：自动合并不具备可靠的资产关系信息，默认关闭
                SetSteps(extract: true, models: true, textures: true, fbx: true,
                    merge: false, keepGltf: false, deleteCooked: true, overwrite: false);
                break;
            case 1: // 仅解包 PAK：cooked 本身就是产物，不做清理
                SetSteps(extract: true, models: false, textures: false, fbx: false,
                    merge: false, keepGltf: false, deleteCooked: false, overwrite: false);
                break;
            case 2: // 仅导出资源（不转 FBX）
                SetSteps(extract: true, models: true, textures: true, fbx: false,
                    merge: false, keepGltf: false, deleteCooked: true, overwrite: false);
                break;
                // 3 = 自定义，不改动各开关
        }
        _applyingPreset = false;
        UpdateOptionState();
    }

    private void SetSteps(bool extract, bool models, bool textures, bool fbx,
        bool merge, bool keepGltf, bool deleteCooked, bool overwrite)
    {
        ExtractCheck.IsChecked = extract;
        ModelsCheck.IsChecked = models;
        TexturesCheck.IsChecked = textures;
        FbxCheck.IsChecked = fbx;
        MergeCheck.IsChecked = merge;
        KeepGltfCheck.IsChecked = keepGltf;
        DeleteCookedCheck.IsChecked = deleteCooked;
        OverwriteCheck.IsChecked = overwrite;
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOptionState();
        // 用户手动改动任一开关即视为自定义模式
        if (!_applyingPreset && IsLoaded && PresetBox is not null)
            PresetBox.SelectedIndex = 3;
    }

    private void UnsupportedFilter_Changed(object sender, RoutedEventArgs e)
    {
        _pakView.Refresh();
        UpdatePakEmptyState();
    }

    private void UpdatePakEmptyState()
    {
        var filteredEmpty = PakEntries.Count > 0 && _pakView.IsEmpty;
        PakEmptyState.Visibility = _pakView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        PakEmptyTitle.Text = Text(filteredEmpty ? "Empty_FilteredTitle" : "Empty_Title");
        PakEmptyHint.Text = Text(filteredEmpty ? "Empty_FilteredHint" : "Empty_Hint");
    }

    private void UpdateScanSummaryText()
    {
        if (_scanSummary is not { } summary) return;
        WorkspaceSummaryText.Text = TextFormat("Summary_ScanResult", summary.Total, summary.Eligible, FormatBytes(summary.Bytes));
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingLanguage || LanguageBox.SelectedItem is not LanguageInfo language) return;
        Instance.SetLanguage(language.Code);
    }

    private void OnLanguageChanged()
    {
        UpdatePakEmptyState();
        UpdateScanSummaryText();
        UpdateProfileRangeHint();
        _pakView.Refresh();
        if (!_isBusy)
        {
            StageText.Text = Text("Stage_Idle");
            HeaderStatusText.Text = Text("App_Ready");
        }
    }

    private void UpdateOptionState()
    {
        if (FbxCheck is null || KeepGltfCheck is null || ModelsCheck is null || TexturesCheck is null) return;
        var exportsTexturesWithoutModels = TexturesCheck.IsChecked == true && ModelsCheck.IsChecked != true;
        if (exportsTexturesWithoutModels && FbxCheck.IsChecked == true)
            FbxCheck.IsChecked = false;
        FbxCheck.IsEnabled = !exportsTexturesWithoutModels;
        var mergeWithFbx = FbxCheck.IsChecked == true && MergeCheck?.IsChecked == true;
        if (mergeWithFbx && KeepGltfCheck.IsChecked != true)
            KeepGltfCheck.IsChecked = true;
        KeepGltfCheck.IsEnabled = FbxCheck.IsChecked == true && !mergeWithFbx;
        if (MergeCheck is not null)
            MergeCheck.IsEnabled = ModelsCheck.IsChecked == true;
        if (DeleteCookedCheck is not null)
            DeleteCookedCheck.IsEnabled = ModelsCheck.IsChecked == true || TexturesCheck.IsChecked == true;
    }

    private WorkflowOptions BuildOptions(string gameDirectory, string outputDirectory)
    {
        var profile = GameProfileBox.Text.Trim();
        var workersText = WorkersBox.SelectedItem is ComboBoxItem workerItem
            ? workerItem.Content?.ToString()
            : WorkersBox.Text;
        _ = int.TryParse(workersText, out var workers);

        return new WorkflowOptions
        {
            GameDirectory = Path.GetFullPath(gameDirectory),
            OutputDirectory = Path.GetFullPath(outputDirectory),
            GameProfile = profile,
            AesKey = EmptyToNull(AesKeyBox.Password),
            ExtractPaks = ExtractCheck.IsChecked == true,
            ExportModels = ModelsCheck.IsChecked == true,
            ExportTextures = TexturesCheck.IsChecked == true,
            ConvertToFbx = FbxCheck.IsChecked == true,
            KeepGltf = FbxCheck.IsChecked == true &&
                (KeepGltfCheck.IsChecked == true || (ModelsCheck.IsChecked == true && MergeCheck.IsChecked == true)),
            MergeModels = ModelsCheck.IsChecked == true && MergeCheck.IsChecked == true,
            DeleteCooked = (ModelsCheck.IsChecked == true || TexturesCheck.IsChecked == true) &&
                DeleteCookedCheck.IsChecked == true,
            Overwrite = OverwriteCheck.IsChecked == true,
            Workers = workers > 0 ? workers : 8,
            LowResource = LowResourceCheck.IsChecked == true
        };
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        StartButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        GameDirectoryBox.IsEnabled = !busy;
        OutputDirectoryBox.IsEnabled = !busy;
        GameProfileBox.IsEnabled = !busy;
        WorkersBox.IsEnabled = !busy;
        AesKeyBox.IsEnabled = !busy;
        LowResourceCheck.IsEnabled = !busy;
        PresetBox.IsEnabled = !busy;
        ExtractCheck.IsEnabled = !busy;
        ModelsCheck.IsEnabled = !busy;
        TexturesCheck.IsEnabled = !busy;
        FbxCheck.IsEnabled = !busy && !(TexturesCheck.IsChecked == true && ModelsCheck.IsChecked != true);
        MergeCheck.IsEnabled = !busy && ModelsCheck.IsChecked == true;
        KeepGltfCheck.IsEnabled = !busy && FbxCheck.IsChecked == true && MergeCheck.IsChecked != true;
        DeleteCookedCheck.IsEnabled = !busy &&
            (ModelsCheck.IsChecked == true || TexturesCheck.IsChecked == true);
        OverwriteCheck.IsEnabled = !busy;
        HeaderStatusText.Text = status;
        if (!busy && TaskProgress.Value < 100) TaskProgress.Value = 0;
    }

    private void UpdateProgress(double value, string stage)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TaskProgress.Value = Math.Clamp(value, 0, 100);
            StageText.Text = stage;
            HeaderStatusText.Text = stage;
        });
    }

    private void AppendLog(string line, UiLogLevel level = UiLogLevel.Info)
    {
        if (level == UiLogLevel.Info)
        {
            // 工具输出按内容自动分级
            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) level = UiLogLevel.Error;
            else if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) level = UiLogLevel.Warning;
        }
        _uiLogs.Enqueue(line, level);
    }

    private static readonly FrozenBrushSet LogBrushes = new();

    private void FlushPendingLogs()
    {
        const int maximumLines = 6_000;
        const int retainedLines = 4_500;

        var batch = _uiLogs.Drain();
        if (batch.Lines.Count == 0) return;

        // 用户上翻查看时不强制回到底部
        var stickToBottom = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;

        var document = LogBox.Document;
        foreach (var line in batch.Lines)
        {
            var run = new Run(line.Text) { Foreground = LogBrushes[line.Level] };
            if (line.Level is UiLogLevel.Success or UiLogLevel.Error) run.FontWeight = FontWeights.SemiBold;
            document.Blocks.Add(new Paragraph(run) { Margin = new Thickness(0), LineHeight = 16 });
        }

        if (document.Blocks.Count > maximumLines)
        {
            var toRemove = document.Blocks.Count - retainedLines;
            for (var index = 0; index < toRemove; index++)
                document.Blocks.Remove(document.Blocks.FirstBlock);
        }

        if (stickToBottom) LogBox.ScrollToEnd();
    }

    /// <summary>日志级别画刷，提前冻结避免跨线程问题。</summary>
    private sealed class FrozenBrushSet
    {
        private readonly SolidColorBrush _info = Freeze(Color.FromRgb(0xA8, 0xBB, 0xB5));
        private readonly SolidColorBrush _stage = Freeze(Color.FromRgb(0x35, 0xD0, 0xA5));
        private readonly SolidColorBrush _success = Freeze(Color.FromRgb(0x7E, 0xE0, 0xB8));
        private readonly SolidColorBrush _warning = Freeze(Color.FromRgb(0xE3, 0xB3, 0x41));
        private readonly SolidColorBrush _error = Freeze(Color.FromRgb(0xE0, 0x6C, 0x5B));

        public SolidColorBrush this[UiLogLevel level] => level switch
        {
            UiLogLevel.Stage => _stage,
            UiLogLevel.Success => _success,
            UiLogLevel.Warning => _warning,
            UiLogLevel.Error => _error,
            _ => _info
        };

        private static SolidColorBrush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isBusy)
        {
            _logFlushTimer.Stop();
            return;
        }
        var answer = MessageBox.Show(Text("Dialog_CloseWhileBusy"), Text("Dialog_BusyTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _cancellation?.Cancel();
        _logFlushTimer.Stop();
    }

    private static string SuggestOutputDirectory(string gameDirectory)
    {
        var protectedRoot = PathSafety.GetProtectedGameDirectory(gameDirectory);
        var gameName = new DirectoryInfo(protectedRoot).Name;
        if (string.IsNullOrWhiteSpace(gameName)) gameName = "Game";
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PakAssetStudio");
        return Path.Combine(baseDirectory, gameName + "_Assets");
    }

    private static string? ChooseFolder(string description, string current)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(current) ? current : string.Empty
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.00} TiB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.00} GiB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MiB";
        return $"{bytes:N0} B";
    }
}
