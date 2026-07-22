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
    private readonly UiLogBuffer _uiLogs = new();
    private readonly DispatcherTimer _logFlushTimer;
    private bool _isBusy;
    private string? _lastScannedDirectory;
    private bool _profileManuallySet;
    private bool _updatingProfile;
    private bool _selectingLanguage;
    private (int Total, int Valid, long Bytes)? _scanSummary;
    private readonly ICollectionView _pakView;

    public ObservableCollection<PakEntry> PakEntries { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
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
        _pakView.Filter = entry => ShowUnsupportedCheck.IsChecked == true || ((PakEntry)entry).IsValid;

        _selectingLanguage = true;
        LanguageBox.ItemsSource = Instance.AvailableLanguages;
        LanguageBox.SelectedItem = Instance.AvailableLanguages.FirstOrDefault(l => l.Code == Instance.CurrentCode);
        _selectingLanguage = false;
        Instance.LanguageChanged += (_, _) => OnLanguageChanged();

        VersionText.Text = GetDisplayVersion();

        UpdateOptionState();
    }

    // 显示 InformationalVersion：正式版为 v0.2.0；预发布版本附带短提交号，如 v0.2.0-alpha.0.3+abc1234
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
        {
            var name = new DirectoryInfo(path).Name + "_Assets";
            OutputDirectoryBox.Text = Path.Combine(Directory.GetParent(path)?.FullName ?? path, name);
        }
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
        PakEntries.Clear();
        AppendLog(TextFormat("Log_Scanning", root), UiLogLevel.Stage);
        _cancellation = new CancellationTokenSource();

        try
        {
            var entries = await _pakToolService.ScanAsync(root, EmptyToNull(AesKeyBox.Password), (done, total) =>
            {
                TaskProgress.Value = total == 0 ? 0 : done * 100d / total;
                StageText.Text = TextFormat("Status_ScanProgress", done, total);
            }, _cancellation.Token);
            foreach (var entry in entries) PakEntries.Add(entry);
            _lastScannedDirectory = Path.GetFullPath(root);

            var valid = entries.Count(entry => entry.IsValid);
            var bytes = entries.Where(entry => entry.IsValid).Sum(entry => entry.SizeBytes);
            _scanSummary = (entries.Count, valid, bytes);
            UpdateScanSummaryText();
            AppendLog(TextFormat("Log_ScanDone", valid, entries.Count), UiLogLevel.Stage);
            ApplyDetectedProfile(entries);

            if (entries.Count == 0 && showNoPakMessage)
                MessageBox.Show(Text("Dialog_NoPaks"), Text("Dialog_ScanDoneTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            else if (valid == 0 && showNoPakMessage)
                MessageBox.Show(Text("Dialog_NoValidPaks"), Text("Dialog_CannotReadTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return valid > 0;
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
            SetBusy(false, Text("App_Ready"));
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
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
        var normalizedGame = Path.GetFullPath(gameDirectory);
        var normalizedOutput = Path.GetFullPath(outputDirectory);
        if (IsSameOrChild(normalizedOutput, normalizedGame))
        {
            MessageBox.Show(Text("Dialog_UnsafeOutput"), Text("Dialog_UnsafeOutputTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_lastScannedDirectory is null || !Path.GetFullPath(gameDirectory).Equals(_lastScannedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (!await ScanPaksAsync(showNoPakMessage: true)) return;
        }

        var options = BuildOptions(gameDirectory, outputDirectory);
        if (!options.ExtractPaks && !options.ExportModels && !options.ExportTextures && !options.ConvertToFbx)
        {
            MessageBox.Show(Text("Dialog_NoSteps"), Text("Dialog_NoStepsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var (required, free) = WorkflowService.EstimateDiskSpace(PakEntries, options);
            if (free < required)
            {
                var answer = MessageBox.Show(
                    TextFormat("Dialog_DiskSpace", FormatBytes(required), FormatBytes(free)),
                    Text("Dialog_DiskSpaceTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(TextFormat("Dialog_DiskCheckFailed", ex.Message), Text("Dialog_DiskCheckTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _uiLogs.Clear();
        LogBox.Document.Blocks.Clear();
        SetBusy(true, Text("Status_Preparing"));
        _cancellation = new CancellationTokenSource();
        try
        {
            await _workflowService.RunAsync(PakEntries.ToList(), options, AppendLog, UpdateProgress, _cancellation.Token);
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
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
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
            OutputDirectoryBox.Text = Path.Combine(Path.GetDirectoryName(GameDirectoryBox.Text) ?? GameDirectoryBox.Text, "ExtractedAssets");
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, GameDirectoryBox))
        {
            _lastScannedDirectory = null;
            _profileManuallySet = false;
        }
    }

    private void GameProfileBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingProfile) return;
        _profileManuallySet = true;
        ProfileAutoHint.Visibility = Visibility.Collapsed;
    }

    private void ApplyDetectedProfile(IReadOnlyList<PakEntry> entries)
    {
        var detected = Ue4ProfileDetector.Detect(entries);
        if (detected is null) return;
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
        if (entries.Any(entry => entry.IsValid && entry.Version.Trim().Equals("V11", StringComparison.OrdinalIgnoreCase)))
            AppendLog(Text("Log_ProfileAmbiguous"), UiLogLevel.Warning);
    }

    private bool _applyingPreset;

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 阶段（IsLoaded 为 false）及程序化切换预设时不重复套用
        if (_applyingPreset || !IsLoaded || ExtractCheck is null) return;
        _applyingPreset = true;
        switch (PresetBox.SelectedIndex)
        {
            case 0: // 完整导出
                SetSteps(extract: true, models: true, textures: true, fbx: true,
                    merge: true, keepGltf: false, deleteCooked: true, overwrite: false);
                break;
            case 1: // 仅解包 PAK：cooked 本身就是产物，不做清理
                SetSteps(extract: true, models: false, textures: false, fbx: false,
                    merge: false, keepGltf: false, deleteCooked: false, overwrite: false);
                break;
            case 2: // 仅导出资源（不转 FBX）
                SetSteps(extract: true, models: true, textures: true, fbx: false,
                    merge: true, keepGltf: false, deleteCooked: true, overwrite: false);
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
        WorkspaceSummaryText.Text = TextFormat("Summary_ScanResult", summary.Total, summary.Valid, FormatBytes(summary.Bytes));
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
        _pakView.Refresh();
        if (!_isBusy)
        {
            StageText.Text = Text("Stage_Idle");
            HeaderStatusText.Text = Text("App_Ready");
        }
    }

    private void UpdateOptionState()
    {
        if (FbxCheck is null || KeepGltfCheck is null) return;
        KeepGltfCheck.IsEnabled = FbxCheck.IsChecked == true;
        if (MergeCheck is not null)
            MergeCheck.IsEnabled = ModelsCheck.IsChecked == true;
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
            GameProfile = string.IsNullOrWhiteSpace(profile) ? "ue4.26" : profile,
            AesKey = EmptyToNull(AesKeyBox.Password),
            ExtractPaks = ExtractCheck.IsChecked == true,
            ExportModels = ModelsCheck.IsChecked == true,
            ExportTextures = TexturesCheck.IsChecked == true,
            ConvertToFbx = FbxCheck.IsChecked == true,
            KeepGltf = KeepGltfCheck.IsChecked == true,
            MergeModels = MergeCheck.IsChecked == true,
            DeleteCooked = DeleteCookedCheck.IsChecked == true,
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

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var parentWithSeparator = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return Path.TrimEndingDirectorySeparator(candidate).Equals(Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.00} TiB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.00} GiB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MiB";
        return $"{bytes:N0} B";
    }
}
