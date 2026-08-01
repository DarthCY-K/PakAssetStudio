using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PakAssetStudio.Models;
using PakAssetStudio.Services;
using static PakAssetStudio.Services.LocalizationService;

namespace PakAssetStudio;

public partial class MainWindow : Window
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
    // Avalonia 12 移除了 ICollectionView/CollectionViewSource，过滤集合自行增量维护
    private ObservableCollection<PakEntry> _filteredEntries = [];
    private ObservableCollection<LogLine> _logLines = [];
    private readonly LogBrushSet _logBrushes = new();

    public ObservableCollection<PakEntry> PakEntries { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            var scale = Math.Max(screen.Scaling, 1.0);
            var area = screen.WorkingArea;
            Width = Math.Min(Width, Math.Max(MinWidth, area.Width / scale - 16));
            Height = Math.Min(Height, Math.Max(MinHeight, area.Height / scale - 16));
        }
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
        GameProfileBox.AddHandler(TextBox.TextChangedEvent,
            new EventHandler<TextChangedEventArgs>(GameProfileBox_Changed));
        PakEntries.CollectionChanged += PakEntries_CollectionChanged;
        LogBox.ItemsSource = _logLines;
        // 默认隐藏不支持的 PAK，由“显示不支持的包”开关控制；过滤集合增量维护
        PakGrid.ItemsSource = _filteredEntries;

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

    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var path = await ChooseFolderAsync(Text("Choose_GameDir"), GameDirectoryBox.Text ?? string.Empty);
        if (path is null) return;
        GameDirectoryBox.Text = path;
        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
            OutputDirectoryBox.Text = SuggestOutputDirectory(path);
    }

    private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = await ChooseFolderAsync(Text("Choose_OutputDir"), OutputDirectoryBox.Text ?? string.Empty);
        if (path is not null) OutputDirectoryBox.Text = path;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await ScanPaksAsync(showNoPakMessage: true);
    }

    private async Task<bool> ScanPaksAsync(bool showNoPakMessage)
    {
        var root = (GameDirectoryBox.Text ?? string.Empty).Trim();
        if (!Directory.Exists(root))
        {
            await SimpleDialog.WarnAsync(this, Text("Dialog_InvalidPath"), Text("Dialog_InvalidPathTitle"));
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
            var entries = await _pakToolService.ScanAsync(root, ParseKeys(AesKeyBox.Text), (done, total) =>
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
                await SimpleDialog.InfoAsync(this, Text("Dialog_NoPaks"), Text("Dialog_ScanDoneTitle"));
            else if (eligible == 0 && showNoPakMessage)
                await SimpleDialog.WarnAsync(this, Text("Dialog_NoValidPaks"), Text("Dialog_CannotReadTitle"));
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
            await SimpleDialog.ErrorAsync(this, ex.Message, Text("Dialog_ScanFailedTitle"));
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
        var gameDirectory = (GameDirectoryBox.Text ?? string.Empty).Trim();
        var outputDirectory = (OutputDirectoryBox.Text ?? string.Empty).Trim();
        if (!Directory.Exists(gameDirectory))
        {
            await SimpleDialog.WarnAsync(this, Text("Dialog_InvalidPath"), Text("Dialog_InvalidPathTitle"));
            return;
        }
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            await SimpleDialog.WarnAsync(this, Text("Dialog_ChooseOutput"), Text("Dialog_InvalidPathTitle"));
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
            await SimpleDialog.WarnAsync(this, TextFormat("Dialog_InvalidPathDetail", ex.Message), Text("Dialog_InvalidPathTitle"));
            return;
        }
        if (PathSafety.IsSameOrChild(normalizedOutput, normalizedSelectedGame) ||
            PathSafety.IsSameOrChild(normalizedOutput, normalizedProtectedGame))
        {
            await SimpleDialog.WarnAsync(this, Text("Dialog_UnsafeOutput"), Text("Dialog_UnsafeOutputTitle"));
            return;
        }

        var options = BuildOptions(gameDirectory, outputDirectory);
        if (!options.ExtractPaks && !options.ExportModels && !options.ExportTextures && !options.ConvertToFbx)
        {
            await SimpleDialog.WarnAsync(this, Text("Dialog_NoSteps"), Text("Dialog_NoStepsTitle"));
            return;
        }
        if (options.ConvertToFbx && options.ExportTextures && !options.ExportModels)
        {
            await SimpleDialog.WarnAsync(this, Text("Error_FbxRequiresModels"), Text("Dialog_InvalidStepsTitle"));
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
                await SimpleDialog.WarnAsync(this, Text("Dialog_NoValidPaks"), Text("Dialog_CannotReadTitle"));
                return;
            }
            var skipped = PakEntries.Count(entry => !entry.CanAttemptExtraction);
            if (skipped > 0)
            {
                var answer = await SimpleDialog.ConfirmAsync(this,
                    TextFormat("Dialog_ConfirmSkippedPaks", skipped), Text("Dialog_ConfirmSkippedPaksTitle"));
                if (!answer) return;
            }
        }

        // 扫描可能刚刚自动填写了唯一可确定的 profile，因此在这里重新读取界面选项。
        options = BuildOptions(gameDirectory, outputDirectory);
        if ((options.ExportModels || options.ExportTextures) && string.IsNullOrWhiteSpace(options.GameProfile))
        {
            await SimpleDialog.WarnAsync(this, Text("Dialog_ChooseProfile"), Text("Dialog_ChooseProfileTitle"));
            return;
        }
        if (options.Overwrite)
        {
            var answer = await SimpleDialog.ConfirmAsync(this,
                TextFormat("Dialog_ConfirmOverwrite", options.OutputDirectory),
                Text("Dialog_ConfirmOverwriteTitle"));
            if (!answer) return;
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
                    var answer = await SimpleDialog.ConfirmAsync(this,
                        TextFormat("Dialog_DiskSpace", FormatBytes(required), FormatBytes(free)),
                        Text("Dialog_DiskSpaceTitle"));
                    if (!answer) return;
                }
            }
            catch (OperationCanceledException)
            {
                StageText.Text = Text("Status_TaskCancelled");
                return;
            }
            catch (Exception ex)
            {
                var answer = await SimpleDialog.ConfirmAsync(this,
                    TextFormat("Dialog_DiskCheckFailedContinue", ex.Message),
                    Text("Dialog_DiskCheckTitle"));
                if (!answer) return;
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
        _logLines.Clear();
        SetBusy(true, Text("Status_Preparing"));
        _cancellation?.Dispose();
        var workflowCancellation = new CancellationTokenSource();
        _cancellation = workflowCancellation;
        try
        {
            await _workflowService.RunAsync(PakEntries.ToList(), options, AppendLog, UpdateProgress, workflowCancellation.Token);
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(99, 199, 168));
            await SimpleDialog.InfoAsync(this, Text("Dialog_TaskDone"), Text("Dialog_TaskDoneTitle"));
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
            await SimpleDialog.ErrorAsync(this, ex.Message, Text("Dialog_TaskFailedTitle"));
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

    private async void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = (OutputDirectoryBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            await SimpleDialog.InfoAsync(this, Text("Dialog_OutputMissing"), Text("Dialog_OutputMissingTitle"));
            return;
        }
        try
        {
            var startInfo = new ProcessStartInfo();
            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = "explorer.exe";
                startInfo.UseShellExecute = true;
            }
            else if (OperatingSystem.IsMacOS())
            {
                startInfo.FileName = "open";
            }
            else
            {
                startInfo.FileName = "xdg-open";
            }
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch
        {
            // 外部文件管理器启动失败时静默忽略。
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;
        var paths = new List<string>();
        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.File) is IStorageItem storage &&
                storage.TryGetLocalPath() is { } local)
            {
                paths.Add(local);
            }
        }
        if (paths.Count == 0) return;
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
                ProfileAutoHint.IsVisible = false;
            }
        }
    }

    private void AesKeyBox_TextChanged(object sender, TextChangedEventArgs e)
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
        ProfileAutoHint.IsVisible = false;
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

    private void GameProfileBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingProfile) return;
        _profileManuallySet = true;
        ProfileAutoHint.IsVisible = false;
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
            ProfileAutoHint.IsVisible = false;
            AppendLog(TextFormat("Log_ProfileRange", detection.Range), UiLogLevel.Warning);
            return;
        }

        var detected = detection.SuggestedProfile;
        if (_profileManuallySet)
        {
            if (!(GameProfileBox.Text ?? string.Empty).Trim().Equals(detected, StringComparison.OrdinalIgnoreCase))
                AppendLog(TextFormat("Log_ProfileHint", detected));
            return;
        }

        _updatingProfile = true;
        GameProfileBox.Text = detected;
        _updatingProfile = false;
        ProfileAutoHint.IsVisible = true;
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
            ProfileRangeHint.IsVisible = false;
            return;
        }

        ProfileRangeHint.Text = TextFormat("Profile_RangeHint", _detectedProfileRange);
        ProfileRangeHint.IsVisible = true;
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
                SetSteps(extract: true, models: true, textures: true, audio: false, animations: false, fbx: true,
                    merge: false, keepGltf: false, deleteCooked: true, overwrite: false);
                break;
            case 1: // 仅解包 PAK：cooked 本身就是产物，不做清理
                SetSteps(extract: true, models: false, textures: false, audio: false, animations: false, fbx: false,
                    merge: false, keepGltf: false, deleteCooked: false, overwrite: false);
                break;
            case 2: // 仅导出资源（不转 FBX）
                SetSteps(extract: true, models: true, textures: true, audio: false, animations: false, fbx: false,
                    merge: false, keepGltf: false, deleteCooked: true, overwrite: false);
                break;
                // 3 = 自定义，不改动各开关
        }
        _applyingPreset = false;
        UpdateOptionState();
    }

    private void SetSteps(bool extract, bool models, bool textures, bool audio, bool animations, bool fbx,
        bool merge, bool keepGltf, bool deleteCooked, bool overwrite)
    {
        ExtractCheck.IsChecked = extract;
        ModelsCheck.IsChecked = models;
        TexturesCheck.IsChecked = textures;
        AudioCheck.IsChecked = audio;
        AnimationsCheck.IsChecked = animations;
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
        RefreshPakFilter();
        UpdatePakEmptyState();
    }

    /// <summary>根据“显示不支持的包”开关整体重建过滤集合（开关或语言切换时调用）。</summary>
    private void RefreshPakFilter()
    {
        var showUnsupported = ShowUnsupportedCheck.IsChecked == true;
        _filteredEntries = new ObservableCollection<PakEntry>(
            showUnsupported ? PakEntries : PakEntries.Where(entry => entry.CanAttemptExtraction));
        PakGrid.ItemsSource = _filteredEntries;
    }

    private void PakEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                _filteredEntries.Clear();
                break;
            case NotifyCollectionChangedAction.Add:
                var showUnsupported = ShowUnsupportedCheck.IsChecked == true;
                foreach (PakEntry entry in e.NewItems!)
                {
                    if (showUnsupported || entry.CanAttemptExtraction)
                        _filteredEntries.Add(entry);
                }
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (PakEntry entry in e.OldItems!)
                    _filteredEntries.Remove(entry);
                break;
            default:
                RefreshPakFilter();
                break;
        }
        UpdatePakEmptyState();
    }

    private void UpdatePakEmptyState()
    {
        var filteredEmpty = PakEntries.Count > 0 && _filteredEntries.Count == 0;
        PakEmptyState.IsVisible = _filteredEntries.Count == 0;
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
        // 语言切换后 PakEntry.Status 等计算属性需要重新渲染
        RefreshPakFilter();
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
            DeleteCookedCheck.IsEnabled = ModelsCheck.IsChecked == true || TexturesCheck.IsChecked == true ||
                AudioCheck?.IsChecked == true || AnimationsCheck?.IsChecked == true;
    }

    private WorkflowOptions BuildOptions(string gameDirectory, string outputDirectory)
    {
        var profile = (GameProfileBox.Text ?? string.Empty).Trim();
        var workersText = WorkersBox.SelectedItem is ComboBoxItem workerItem
            ? workerItem.Content?.ToString()
            : WorkersBox.Text ?? string.Empty;
        _ = int.TryParse(workersText, out var workers);

        return new WorkflowOptions
        {
            GameDirectory = Path.GetFullPath(gameDirectory),
            OutputDirectory = Path.GetFullPath(outputDirectory),
            GameProfile = profile,
            AesKeys = ParseKeys(AesKeyBox.Text),
            ExtractPaks = ExtractCheck.IsChecked == true,
            ExportModels = ModelsCheck.IsChecked == true,
            ExportTextures = TexturesCheck.IsChecked == true,
            ExportAudio = AudioCheck.IsChecked == true,
            ExportAnimations = AnimationsCheck.IsChecked == true,
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
        AudioCheck.IsEnabled = !busy;
        AnimationsCheck.IsEnabled = !busy;
        FbxCheck.IsEnabled = !busy && !(TexturesCheck.IsChecked == true && ModelsCheck.IsChecked != true);
        MergeCheck.IsEnabled = !busy && ModelsCheck.IsChecked == true;
        KeepGltfCheck.IsEnabled = !busy && FbxCheck.IsChecked == true && MergeCheck.IsChecked != true;
        DeleteCookedCheck.IsEnabled = !busy &&
            (ModelsCheck.IsChecked == true || TexturesCheck.IsChecked == true ||
             AudioCheck.IsChecked == true || AnimationsCheck.IsChecked == true);
        OverwriteCheck.IsEnabled = !busy;
        HeaderStatusText.Text = status;
        if (!busy && TaskProgress.Value < 100) TaskProgress.Value = 0;
    }

    private void UpdateProgress(double value, string stage)
    {
        Dispatcher.UIThread.Post(() =>
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

    private void FlushPendingLogs()
    {
        const int maximumLines = 6_000;
        const int retainedLines = 4_500;

        var batch = _uiLogs.Drain();
        if (batch.Lines.Count == 0) return;

        // 用户上翻查看时不强制回到底部
        var stickToBottom = LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - 4;

        foreach (var line in batch.Lines)
        {
            _logLines.Add(new LogLine(line.Text, _logBrushes[line.Level],
                line.Level is UiLogLevel.Success or UiLogLevel.Error ? FontWeight.SemiBold : FontWeight.Normal));
        }

        if (_logLines.Count > maximumLines)
        {
            var keepFrom = _logLines.Count - retainedLines;
            _logLines = new ObservableCollection<LogLine>(_logLines.Skip(keepFrom));
            LogBox.ItemsSource = _logLines;
        }

        if (stickToBottom) LogScroll.ScrollToEnd();
    }

    /// <summary>日志行视图模型：文本 + 分级颜色 + 字重。</summary>
    private sealed record LogLine(string Text, IBrush Brush, FontWeight Weight);

    /// <summary>日志级别画刷（UI 线程创建后只读复用）。</summary>
    private sealed class LogBrushSet
    {
        private readonly IBrush _info = new SolidColorBrush(Color.FromRgb(0xA8, 0xBB, 0xB5));
        private readonly IBrush _stage = new SolidColorBrush(Color.FromRgb(0x35, 0xD0, 0xA5));
        private readonly IBrush _success = new SolidColorBrush(Color.FromRgb(0x7E, 0xE0, 0xB8));
        private readonly IBrush _warning = new SolidColorBrush(Color.FromRgb(0xE3, 0xB3, 0x41));
        private readonly IBrush _error = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x5B));

        public IBrush this[UiLogLevel level] => level switch
        {
            UiLogLevel.Stage => _stage,
            UiLogLevel.Success => _success,
            UiLogLevel.Warning => _warning,
            UiLogLevel.Error => _error,
            _ => _info
        };
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!_isBusy)
        {
            _logFlushTimer.Stop();
            return;
        }
        e.Cancel = true;
        var answer = await SimpleDialog.ConfirmAsync(this, Text("Dialog_CloseWhileBusy"), Text("Dialog_BusyTitle"));
        if (answer)
        {
            _cancellation?.Cancel();
            _logFlushTimer.Stop();
            Close();
        }
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

    private async Task<string?> ChooseFolderAsync(string description, string current)
    {
        IStorageFolder? suggested = null;
        if (Directory.Exists(current))
        {
            try
            {
                suggested = await StorageProvider.TryGetFolderFromPathAsync(current);
            }
            catch
            {
                // 起始路径解析失败时退回默认位置。
            }
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = description,
            SuggestedStartLocation = suggested,
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>解析 AES 输入：每行一个密钥，去首尾空白、空行与重复项。</summary>
    private static IReadOnlyList<string> ParseKeys(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();
        foreach (var line in value.Split('\n'))
        {
            var key = line.Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;
            keys.Add(key);
        }
        return keys;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.00} TiB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.00} GiB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MiB";
        return $"{bytes:N0} B";
    }
}
