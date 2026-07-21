using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PakAssetStudio.Models;

namespace PakAssetStudio.Services;

public sealed class WorkflowService(ProcessRunner processRunner, PakToolService pakToolService)
{
    private readonly string _umodelPath = Path.Combine(AppContext.BaseDirectory, "Tools", "umodel", "umodel_64.exe");
    private readonly string _assimpPath = Path.Combine(AppContext.BaseDirectory, "Tools", "assimp", "assimp-vc143-mt.dll");
    private readonly string _converterPath = Path.Combine(AppContext.BaseDirectory, "Tools", "convert_gltf_to_fbx.py");
    private readonly string _pythonPath = Path.Combine(AppContext.BaseDirectory, "Tools", "python", "python.exe");

    public async Task RunAsync(
        IReadOnlyList<PakEntry> entries,
        WorkflowOptions options,
        Action<string, UiLogLevel> log,
        Action<double, string> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var logPath = Path.Combine(options.OutputDirectory, "PakAssetStudio.log");
        var logLines = new ConcurrentQueue<string>();

        void WriteLog(string message, UiLogLevel level = UiLogLevel.Info)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            logLines.Enqueue(line);
            log(line, level);
        }

        // 低占用模式：限制并行度，子进程以低优先级运行，减少对其他程序的干扰
        var workers = GetEffectiveWorkers(options);
        var priority = options.LowResource ? ProcessPriorityClass.BelowNormal : (ProcessPriorityClass?)null;

        try
        {
            if (options.LowResource)
                WriteLog(LocalizationService.TextFormat("Log_LowResource", workers), UiLogLevel.Stage);
            var cookedDirectory = Path.Combine(options.OutputDirectory, "CookedAssets");
            var exportDirectory = Path.Combine(options.OutputDirectory, "ExportedAssets");

            if (options.ExtractPaks)
            {
                Directory.CreateDirectory(cookedDirectory);
                var ordered = PakToolService.GetExtractionOrder(entries);
                if (ordered.Count == 0) throw new InvalidOperationException("没有可解包的 Unreal PAK。请先扫描并检查密钥。");

                WriteLog($"开始解包 {ordered.Count} 个 PAK。输出：{cookedDirectory}", UiLogLevel.Stage);
                for (var index = 0; index < ordered.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pak = ordered[index];
                    progress(5 + 45d * index / ordered.Count, LocalizationService.TextFormat("Stage_Extracting", pak.Name));
                    WriteLog($"解包：{pak.FullPath}");

                    var command = new List<string> { "unpack", pak.FullPath, "-o", cookedDirectory, "-q" };
                    if (options.Overwrite) command.Add("-f");
                    var arguments = PakToolService.BuildArguments(options.AesKey, command.ToArray());
                    var result = await processRunner.RunAsync(
                        pakToolService.RepakPath,
                        arguments,
                        Path.GetDirectoryName(pakToolService.RepakPath),
                        line => WriteLog(line),
                        cancellationToken,
                        priority);
                    if (result.ExitCode != 0) throw new InvalidOperationException($"解包失败：{pak.Name}");
                }
            }

            if (options.ExportModels || options.ExportTextures)
            {
                if (!Directory.Exists(cookedDirectory))
                    throw new DirectoryNotFoundException($"找不到 cooked 目录：{cookedDirectory}");
                EnsureFile(_umodelPath, "缺少 UModel");
                Directory.CreateDirectory(exportDirectory);
                progress(52, LocalizationService.Text("Stage_Exporting"));
                WriteLog($"启动 UModel，profile={options.GameProfile}", UiLogLevel.Stage);

                var arguments = new List<string>
                {
                    $"-game={options.GameProfile}", "-export", "-gltf", "-png", "-lods", "-noanim"
                };
                if (!options.Overwrite) arguments.Add("-nooverwrite");
                if (!options.ExportModels) arguments.AddRange(["-nomesh", "-nostat"]);
                if (!options.ExportTextures) arguments.Add("-notex");
                arguments.Add($"-path={cookedDirectory}");
                arguments.Add($"-out={exportDirectory}");
                arguments.Add("*.uasset");

                var result = await processRunner.RunAsync(
                    _umodelPath,
                    arguments,
                    Path.GetDirectoryName(_umodelPath),
                    line => WriteLog(line),
                    cancellationToken,
                    priority);
                if (result.ExitCode != 0) throw new InvalidOperationException("UModel 导出失败，请查看日志。");
            }

            if (options.ConvertToFbx)
            {
                if (!Directory.Exists(exportDirectory))
                    throw new DirectoryNotFoundException($"找不到导出目录：{exportDirectory}");
                EnsureFile(_assimpPath, "缺少 Assimp DLL");
                EnsureFile(_converterPath, "缺少 FBX 转换脚本");
                EnsureFile(_pythonPath, "缺少内置 Python 运行时");

                var conversionDirectory = exportDirectory;
                if (options.KeepGltf)
                {
                    conversionDirectory = Path.Combine(options.OutputDirectory, "FbxAssets");
                    progress(76, LocalizationService.Text("Stage_CopyFbx"));
                    WriteLog($"复制 glTF 导出目录到：{conversionDirectory}", UiLogLevel.Stage);
                    await CopyDirectoryAsync(exportDirectory, conversionDirectory, options.Overwrite,
                        value => progress(76 + value * 8, LocalizationService.Text("Stage_CopyFbx")), cancellationToken);
                }

                progress(85, LocalizationService.Text("Stage_Converting"));
                WriteLog("启动 Assimp FBX 批量转换。", UiLogLevel.Stage);
                var arguments = new[]
                {
                    _converterPath, conversionDirectory, "--dll", _assimpPath,
                    "--workers", workers.ToString()
                };
                var result = await processRunner.RunAsync(_pythonPath, arguments, AppContext.BaseDirectory, line =>
                {
                    WriteLog(line);
                    var parsed = TryParseConverterProgress(line);
                    if (parsed.HasValue) progress(85 + parsed.Value * 14, LocalizationService.Text("Stage_Converting"));
                }, cancellationToken, priority);
                if (result.ExitCode != 0) throw new InvalidOperationException("部分 FBX 转换失败，原 glTF 已保留。请查看失败清单。");
            }

            progress(100, LocalizationService.Text("Stage_Done"));
            await WriteSummaryAsync(options.OutputDirectory, line => WriteLog(line), cancellationToken);
            WriteLog("全部任务完成。", UiLogLevel.Success);
        }
        finally
        {
            await File.WriteAllLinesAsync(logPath, logLines, CancellationToken.None);
        }
    }

    /// <summary>低占用模式下限制并行度，避免占满 CPU。</summary>
    public static int GetEffectiveWorkers(WorkflowOptions options) =>
        options.LowResource ? Math.Min(options.Workers, 2) : options.Workers;

    public static (long RequiredBytes, long FreeBytes) EstimateDiskSpace(IEnumerable<PakEntry> entries, WorkflowOptions options)
    {
        var pakBytes = entries.Where(entry => entry.IsValid).Sum(entry => entry.SizeBytes);
        var multiplier = options.ConvertToFbx && options.KeepGltf ? 6d
            : options.ConvertToFbx ? 4.5d
            : options.ExportModels || options.ExportTextures ? 3.5d
            : 2.5d;
        var required = (long)(pakBytes * multiplier);
        var root = Path.GetPathRoot(Path.GetFullPath(options.OutputDirectory))!;
        return (required, new DriveInfo(root).AvailableFreeSpace);
    }

    private static Task CopyDirectoryAsync(
        string source,
        string destination,
        bool overwrite,
        Action<double> onProgress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFile = files[index];
                var relative = Path.GetRelativePath(source, sourceFile);
                var destinationFile = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                if (overwrite || !File.Exists(destinationFile)) File.Copy(sourceFile, destinationFile, overwrite);
                if (index % 100 == 0 || index == files.Count - 1)
                    onProgress((index + 1d) / Math.Max(files.Count, 1));
            }
        }, cancellationToken);
    }

    private static double? TryParseConverterProgress(string line)
    {
        var marker = "Processed ";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var token = line[(start + marker.Length)..].Split(';', ' ')[0];
        var parts = token.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], out var done) && double.TryParse(parts[1], out var total) && total > 0
            ? done / total
            : null;
    }

    private static Task WriteSummaryAsync(string root, Action<string> log, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            foreach (var directoryName in new[] { "CookedAssets", "ExportedAssets", "FbxAssets" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.Combine(root, directoryName);
                if (!Directory.Exists(directory)) continue;
                long total = 0;
                long fbx = 0;
                long png = 0;
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total++;
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase)) fbx++;
                    else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) png++;
                }
                log($"{directoryName}: {total:N0} 个文件；FBX={fbx:N0}；PNG={png:N0}");
            }
        }, cancellationToken);
    }

    private static void EnsureFile(string path, string message)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(message, path);
    }
}
