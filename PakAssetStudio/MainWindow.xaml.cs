using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PakAssetStudio.Models;
using PakAssetStudio.Services;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;

namespace PakAssetStudio;

public partial class MainWindow : Window
{
    private readonly ProcessRunner _processRunner = new();
    private readonly PakToolService _pakToolService;
    private readonly WorkflowService _workflowService;
    private CancellationTokenSource? _cancellation;
    private readonly UiLogBuffer _uiLogs = new();
    private readonly DispatcherTimer _logFlushTimer;
    private bool _isBusy;
    private string? _lastScannedDirectory;

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
        UpdateOptionState();
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("选择游戏根目录或 Paks 目录", GameDirectoryBox.Text);
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
        var path = ChooseFolder("选择输出目录", OutputDirectoryBox.Text);
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
            MessageBox.Show("请选择有效的游戏目录。", "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        SetBusy(true, "正在扫描 PAK");
        TaskProgress.Value = 0;
        PakEntries.Clear();
        AppendLog($"扫描目录：{root}");
        _cancellation = new CancellationTokenSource();

        try
        {
            var entries = await _pakToolService.ScanAsync(root, EmptyToNull(AesKeyBox.Password), (done, total) =>
            {
                TaskProgress.Value = total == 0 ? 0 : done * 100d / total;
                StageText.Text = $"读取 PAK 信息 {done}/{total}";
            }, _cancellation.Token);
            foreach (var entry in entries) PakEntries.Add(entry);
            _lastScannedDirectory = Path.GetFullPath(root);

            var valid = entries.Count(entry => entry.IsValid);
            var bytes = entries.Where(entry => entry.IsValid).Sum(entry => entry.SizeBytes);
            WorkspaceSummaryText.Text = $"发现 {entries.Count} 个 .pak；{valid} 个 Unreal PAK；总计 {FormatBytes(bytes)}";
            AppendLog($"扫描完成：{valid}/{entries.Count} 个 PAK 可读取。");

            if (entries.Count == 0 && showNoPakMessage)
                MessageBox.Show("目录中没有找到 .pak 文件。", "扫描完成", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (valid == 0 && showNoPakMessage)
                MessageBox.Show("找到了 .pak，但没有可读取的 Unreal PAK。它们可能是 Chromium 数据包、IoStore 辅助包或需要 AES 密钥。", "无法读取", MessageBoxButton.OK, MessageBoxImage.Warning);
            return valid > 0;
        }
        catch (OperationCanceledException)
        {
            AppendLog("扫描已取消。");
            return false;
        }
        catch (Exception ex)
        {
            AppendLog("扫描失败：" + ex.Message);
            MessageBox.Show(ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var gameDirectory = GameDirectoryBox.Text.Trim();
        var outputDirectory = OutputDirectoryBox.Text.Trim();
        if (!Directory.Exists(gameDirectory))
        {
            MessageBox.Show("请选择有效的游戏目录。", "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show("请选择输出目录。", "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var normalizedGame = Path.GetFullPath(gameDirectory);
        var normalizedOutput = Path.GetFullPath(outputDirectory);
        if (IsSameOrChild(normalizedOutput, normalizedGame))
        {
            MessageBox.Show("输出目录必须位于游戏目录之外，避免向原游戏目录写入文件。", "输出位置不安全", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_lastScannedDirectory is null || !Path.GetFullPath(gameDirectory).Equals(_lastScannedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (!await ScanPaksAsync(showNoPakMessage: true)) return;
        }

        var options = BuildOptions(gameDirectory, outputDirectory);
        if (!options.ExtractPaks && !options.ExportModels && !options.ExportTextures && !options.ConvertToFbx)
        {
            MessageBox.Show("请至少选择一个处理步骤。", "没有任务", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var (required, free) = WorkflowService.EstimateDiskSpace(PakEntries, options);
            if (free < required)
            {
                var answer = MessageBox.Show(
                    $"预计需要约 {FormatBytes(required)}，当前磁盘可用 {FormatBytes(free)}。继续可能导致任务中断。\n\n仍要继续吗？",
                    "磁盘空间可能不足", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法检查输出磁盘：" + ex.Message, "磁盘检查失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _uiLogs.Clear();
        LogBox.Clear();
        SetBusy(true, "准备任务");
        _cancellation = new CancellationTokenSource();
        try
        {
            await _workflowService.RunAsync(PakEntries.ToList(), options, AppendLog, UpdateProgress, _cancellation.Token);
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(99, 199, 168));
            MessageBox.Show("处理完成。详细结果和日志已写入输出目录。", "任务完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("任务已由用户取消。");
            StageText.Text = "任务已取消";
        }
        catch (Exception ex)
        {
            AppendLog("任务失败：" + ex.Message);
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromRgb(217, 111, 80));
            MessageBox.Show(ex.Message, "任务失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StageText.Text = "正在取消…";
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
        if (ReferenceEquals(sender, GameDirectoryBox)) _lastScannedDirectory = null;
    }

    private void Option_Changed(object sender, RoutedEventArgs e) => UpdateOptionState();

    private void UpdateOptionState()
    {
        if (FbxCheck is null || KeepGltfCheck is null) return;
        KeepGltfCheck.IsEnabled = FbxCheck.IsChecked == true;
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
            Overwrite = OverwriteCheck.IsChecked == true,
            Workers = workers > 0 ? workers : 8
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

    private void AppendLog(string line)
    {
        _uiLogs.Enqueue(line);
    }

    private void FlushPendingLogs()
    {
        const int maximumCharacters = 800_000;
        const int retainedCharacters = 550_000;

        var batch = _uiLogs.Drain();
        if (batch.Text.Length == 0) return;

        LogBox.AppendText(batch.Text);
        if (LogBox.Text.Length > maximumCharacters)
        {
            var start = LogBox.Text.Length - retainedCharacters;
            var newline = LogBox.Text.IndexOf('\n', start);
            LogBox.Text = newline >= 0 ? LogBox.Text[(newline + 1)..] : LogBox.Text[^retainedCharacters..];
        }
        LogBox.ScrollToEnd();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isBusy)
        {
            _logFlushTimer.Stop();
            return;
        }
        var answer = MessageBox.Show("任务仍在运行。关闭窗口会终止当前子进程，确定关闭吗？", "任务运行中", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
