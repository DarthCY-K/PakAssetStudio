using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PakAssetStudio.Models;

namespace PakAssetStudio.Services;

public sealed class WorkflowService(IProcessRunner processRunner, PakToolService pakToolService)
{
    private const string OutputMarkerName = ".pakassetstudio-output.json";
    private const string OutputMarkerProduct = "PakAssetStudio";

    private readonly string _umodelPath = Path.Combine(AppContext.BaseDirectory, "Tools", "umodel", "umodel_64.exe");
    private readonly string _assimpPath = Path.Combine(AppContext.BaseDirectory, "Tools", "assimp", "assimp-vc143-mt.dll");
    private readonly string _converterPath = Path.Combine(AppContext.BaseDirectory, "Tools", "convert_gltf_to_fbx.py");
    private readonly string _mergePath = Path.Combine(AppContext.BaseDirectory, "Tools", "merge_gltf.py");
    private readonly string _pythonPath = Path.Combine(AppContext.BaseDirectory, "Tools", "python", "python.exe");

    public async Task RunAsync(
        IReadOnlyList<PakEntry> entries,
        WorkflowOptions options,
        Action<string, UiLogLevel> log,
        Action<double, string> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(options.GameDirectory))
            throw new DirectoryNotFoundException(LocalizationService.Text("Dialog_InvalidPath"));
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new InvalidOperationException(LocalizationService.Text("Dialog_ChooseOutput"));
        if ((options.ExportModels || options.ExportTextures) && string.IsNullOrWhiteSpace(options.GameProfile))
            throw new InvalidOperationException(LocalizationService.Text("Error_ProfileRequired"));
        if (options.ConvertToFbx && options.ExportTextures && !options.ExportModels)
            throw new InvalidOperationException(LocalizationService.Text("Error_FbxRequiresModels"));
        var preserveGltf = options.ConvertToFbx && (options.KeepGltf || options.MergeModels);
        PathSafety.EnsureOutputOutsideGame(options.GameDirectory, options.OutputDirectory);
        Directory.CreateDirectory(options.OutputDirectory);

        var lockPath = Path.Combine(options.OutputDirectory, ".pakassetstudio.lock");
        FileStream outputLock;
        try
        {
            EnsureRegularControlFile(lockPath);
            outputLock = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(LocalizationService.Text("Error_OutputInUse"), ex);
        }
        await using var outputLockLifetime = outputLock;
        EnsureOutputOwnership(options);

        var logPath = Path.Combine(options.OutputDirectory, "PakAssetStudio.log");
        EnsureRegularControlFile(logPath);
        FileStream logStream;
        try
        {
            logStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(LocalizationService.TextFormat("Log_DiskWriteFailed", ex.Message), ex);
        }
        await using var logStreamLifetime = logStream;
        await using var logWriter = new StreamWriter(logStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        var logLock = new object();
        var logWriteAvailable = true;

        void WriteLog(string message, UiLogLevel level = UiLogLevel.Info)
        {
            message = PakToolService.RedactSensitive(message, options.AesKeys);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            string? logFailure = null;
            lock (logLock)
            {
                if (logWriteAvailable)
                {
                    try
                    {
                        logWriter.WriteLine(line);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        logWriteAvailable = false;
                        logFailure = ex.Message;
                    }
                }
            }
            if (logFailure is not null)
                log($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {LocalizationService.TextFormat("Log_DiskWriteFailed", logFailure)}", UiLogLevel.Error);
            log(line, level);
        }

        var workers = GetEffectiveWorkers(options);
        var priority = options.LowResource ? ProcessPriorityClass.BelowNormal : (ProcessPriorityClass?)null;
        var backups = new List<string>();
        var completed = false;

        WriteLog(LocalizationService.Text("Log_SessionStart"), UiLogLevel.Stage);
        try
        {
            if (options.LowResource)
                WriteLog(LocalizationService.TextFormat("Log_LowResource", workers), UiLogLevel.Stage);

            var cookedDirectory = Path.Combine(options.OutputDirectory, "CookedAssets");
            var exportDirectory = Path.Combine(options.OutputDirectory, "ExportedAssets");
            var fbxDirectory = Path.Combine(options.OutputDirectory, "FbxAssets");
            ValidateManagedOutputPlan(options, preserveGltf, cookedDirectory, exportDirectory, fbxDirectory);
            EnsureRequiredTools(options);

            if (options.ExtractPaks)
            {
                EnsureFile(pakToolService.RepakPath, "Error_MissingRepak");
                PrepareManagedDirectory(cookedDirectory, options.OutputDirectory, options.Overwrite, backups, WriteLog);
                var ordered = PakToolService.GetExtractionOrder(entries);
                if (ordered.Count == 0)
                    throw new InvalidOperationException(LocalizationService.Text("Error_NoExtractablePaks"));

                WriteLog(LocalizationService.TextFormat("Log_ExtractStart", ordered.Count, cookedDirectory), UiLogLevel.Stage);
                for (var index = 0; index < ordered.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pak = ordered[index];
                    progress(5 + 45d * index / ordered.Count, LocalizationService.TextFormat("Stage_Extracting", pak.Name));
                    WriteLog(LocalizationService.TextFormat("Log_ExtractPak", pak.FullPath));

                    // 此目录在本次运行开始时已清空；-f 只用于让后解包的 patch 正确覆盖 base。
                    // 使用扫描阶段为该 pak 验证成功的密钥（多 KeyGuid 游戏各包密钥可能不同）。
                    var command = new[] { "unpack", pak.FullPath, "-o", cookedDirectory, "-q", "-f" };
                    var arguments = PakToolService.BuildArguments(pak.AesKeyUsed, command);
                    var result = await processRunner.RunAsync(
                        pakToolService.RepakPath,
                        arguments,
                        Path.GetDirectoryName(pakToolService.RepakPath),
                        line => WriteLog(line),
                        cancellationToken,
                        priority,
                        captureOutput: false);
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException(LocalizationService.TextFormat("Error_ExtractPak", pak.Name));
                }
            }

            if (options.ExportModels || options.ExportTextures || options.ExportAudio || options.ExportAnimations)
            {
                EnsureManagedChild(cookedDirectory, options.OutputDirectory);
                if (!Directory.Exists(cookedDirectory))
                    throw new DirectoryNotFoundException(LocalizationService.TextFormat("Error_CookedMissing", cookedDirectory));
                if (!options.ExtractPaks)
                    await Task.Run(() => ValidateManagedTree(cookedDirectory, cancellationToken), cancellationToken);
                EnsureFile(_umodelPath, "Error_MissingUmodel");
                if (!preserveGltf)
                    RetireManagedDirectory(fbxDirectory, options.OutputDirectory, options.Overwrite, backups, WriteLog);
                PrepareManagedDirectory(exportDirectory, options.OutputDirectory, options.Overwrite, backups, WriteLog);
                progress(52, LocalizationService.Text("Stage_Exporting"));
                WriteLog(LocalizationService.TextFormat("Log_UmodelStart", options.GameProfile), UiLogLevel.Stage);

                // 主导出：glTF 网格 + 纹理（+ 可选音频）。动画无法与 -gltf 同批，
                // 需单独一次 -psk 调用（见下），因此这里保留 -noanim 开关。
                var arguments = new List<string>
                {
                    $"-game={options.GameProfile}", "-export", "-gltf", "-png", "-lods"
                };
                if (!options.ExportAnimations) arguments.Add("-noanim");
                if (!options.ExportModels) arguments.AddRange(["-nomesh", "-nostat"]);
                if (!options.ExportTextures) arguments.Add("-notex");
                if (options.ExportAudio) arguments.Add("-sounds");
                arguments.Add($"-path={cookedDirectory}");
                arguments.Add($"-out={exportDirectory}");
                arguments.Add("*.uasset");

                var result = await processRunner.RunAsync(
                    _umodelPath,
                    arguments,
                    Path.GetDirectoryName(_umodelPath),
                    line => WriteLog(line),
                    cancellationToken,
                    priority,
                    captureOutput: false);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(LocalizationService.Text("Error_Umodel"));

                var exportedKinds = await Task.Run(
                    () => InspectExportedKinds(exportDirectory, cancellationToken), cancellationToken);
                if (options.ExportModels && !exportedKinds.HasModels)
                    throw new InvalidOperationException(LocalizationService.Text("Error_NoModelsExported"));
                if (options.ExportTextures && !exportedKinds.HasTextures)
                    throw new InvalidOperationException(LocalizationService.Text("Error_NoTexturesExported"));
                if (options.ExportAudio && !exportedKinds.HasAudio)
                    throw new InvalidOperationException(LocalizationService.Text("Error_NoAudioExported"));

                if (options.ExportAnimations)
                {
                    // 动画单独导出：格式开关全局生效，-gltf 不产出动画，必须用 -psk 再跑一次。
                    // -nomesh -nostat -notex 只注销网格/纹理类，不影响 Skeleton/AnimSet 导出。
                    progress(62, LocalizationService.Text("Stage_Animations"));
                    WriteLog(LocalizationService.Text("Log_AnimationsStart"), UiLogLevel.Stage);
                    var animationArguments = new List<string>
                    {
                        $"-game={options.GameProfile}", "-export", "-psk", "-nomesh", "-nostat", "-notex",
                        $"-path={cookedDirectory}", $"-out={exportDirectory}", "*.uasset"
                    };
                    var animationResult = await processRunner.RunAsync(
                        _umodelPath,
                        animationArguments,
                        Path.GetDirectoryName(_umodelPath),
                        line => WriteLog(line),
                        cancellationToken,
                        priority,
                        captureOutput: false);
                    if (animationResult.ExitCode != 0)
                        throw new InvalidOperationException(LocalizationService.Text("Error_Umodel"));

                    var animationKinds = await Task.Run(
                        () => InspectExportedKinds(exportDirectory, cancellationToken), cancellationToken);
                    if (!animationKinds.HasAnimations)
                        throw new InvalidOperationException(LocalizationService.Text("Error_NoAnimationsExported"));
                }
            }

            if (options.MergeModels && options.ExportModels)
            {
                EnsureManagedChild(exportDirectory, options.OutputDirectory);
                if (!Directory.Exists(exportDirectory))
                    throw new DirectoryNotFoundException(LocalizationService.TextFormat("Error_ExportMissing", exportDirectory));
                EnsureFile(_mergePath, "Error_MissingMergeScript");
                EnsureFile(_pythonPath, "Error_MissingPython");

                progress(70, LocalizationService.Text("Stage_Merging"));
                WriteLog(LocalizationService.TextFormat("Log_MergeStart", exportDirectory), UiLogLevel.Stage);
                var mergeArguments = new List<string>
                {
                    _mergePath, exportDirectory, "--mode", "explicit", "--keep-sources"
                };
                if (options.Overwrite) mergeArguments.Add("--overwrite");
                var result = await processRunner.RunAsync(
                    _pythonPath,
                    mergeArguments,
                    AppContext.BaseDirectory,
                    line => WriteLog(line),
                    cancellationToken,
                    priority,
                    captureOutput: false);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(LocalizationService.Text("Error_Merge"));
            }

            if (options.ConvertToFbx)
            {
                EnsureManagedChild(exportDirectory, options.OutputDirectory);
                if (!Directory.Exists(exportDirectory))
                    throw new DirectoryNotFoundException(LocalizationService.TextFormat("Error_ExportMissing", exportDirectory));
                if (!options.ExportModels && !options.ExportTextures)
                    await Task.Run(() => ValidateManagedTree(exportDirectory, cancellationToken), cancellationToken);
                EnsureFile(_assimpPath, "Error_MissingAssimp");
                EnsureFile(_converterPath, "Error_MissingConverter");
                EnsureFile(_pythonPath, "Error_MissingPython");

                if (!options.ExportModels && !options.ExportTextures && !preserveGltf && options.Overwrite)
                {
                    var backupCount = backups.Count;
                    PrepareManagedDirectory(
                        exportDirectory, options.OutputDirectory, true, backups, WriteLog);
                    if (backups.Count > backupCount)
                    {
                        var sourceBackup = backups[^1];
                        progress(76, LocalizationService.Text("Stage_CopyRecovery"));
                        WriteLog(LocalizationService.TextFormat("Log_CopyForOverwrite", sourceBackup), UiLogLevel.Stage);
                        await CopyDirectoryAsync(
                            sourceBackup,
                            exportDirectory,
                            overwrite: true,
                            value => progress(76 + value * 8, LocalizationService.Text("Stage_CopyRecovery")),
                            cancellationToken);
                    }
                }

                var conversionDirectory = exportDirectory;
                if (preserveGltf)
                {
                    PrepareManagedDirectory(fbxDirectory, options.OutputDirectory, options.Overwrite, backups, WriteLog);
                    conversionDirectory = fbxDirectory;
                    progress(76, LocalizationService.Text("Stage_CopyFbx"));
                    WriteLog(LocalizationService.TextFormat("Log_CopyFbx", conversionDirectory), UiLogLevel.Stage);
                    await CopyDirectoryAsync(
                        exportDirectory,
                        conversionDirectory,
                        overwrite: true,
                        value => progress(76 + value * 8, LocalizationService.Text("Stage_CopyFbx")),
                        cancellationToken);
                }

                progress(85, LocalizationService.Text("Stage_Converting"));
                WriteLog(LocalizationService.Text("Log_ConvertStart"), UiLogLevel.Stage);
                var arguments = new List<string>
                {
                    _converterPath, conversionDirectory, "--dll", _assimpPath,
                    "--workers", workers.ToString(), "--delete-source"
                };
                if (options.Overwrite) arguments.Add("--overwrite");
                var result = await processRunner.RunAsync(_pythonPath, arguments, AppContext.BaseDirectory, line =>
                {
                    WriteLog(line);
                    var parsed = TryParseConverterProgress(line);
                    if (parsed.HasValue)
                        progress(85 + parsed.Value * 14, LocalizationService.Text("Stage_Converting"));
                }, cancellationToken, priority, captureOutput: false);
                if (result.ExitCode == 3)
                    throw new InvalidOperationException(LocalizationService.Text("Error_ConvertSkipped"));
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(LocalizationService.Text("Error_Convert"));
            }

            if (options.DeleteCooked && (options.ExportModels || options.ExportTextures ||
                                         options.ExportAudio || options.ExportAnimations) &&
                Directory.Exists(cookedDirectory))
            {
                EnsureManagedChild(cookedDirectory, options.OutputDirectory);
                progress(97, LocalizationService.Text("Stage_DeletingCooked"));
                WriteLog(LocalizationService.Text("Log_DeleteCooked"), UiLogLevel.Stage);
                await DeleteManagedDirectoryAsync(
                    cookedDirectory, options.OutputDirectory, cancellationToken);
            }

            progress(98, LocalizationService.Text("Stage_Finalizing"));
            await WriteSummaryAsync(options.OutputDirectory, line => WriteLog(line), cancellationToken);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            WriteLog(LocalizationService.Text("Log_WorkflowCancelled"), UiLogLevel.Warning);
            throw;
        }
        catch (Exception ex)
        {
            WriteLog(LocalizationService.TextFormat("Log_WorkflowFailed", ex.Message), UiLogLevel.Error);
            throw;
        }
        finally
        {
            if (completed)
            {
                progress(99, LocalizationService.Text("Stage_Finalizing"));
                var cleanupCancelled = false;
                foreach (var backup in backups)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Directory.Exists(backup))
                            await DeleteManagedDirectoryAsync(
                                backup, options.OutputDirectory, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        cleanupCancelled = true;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or System.Security.SecurityException)
                    {
                        WriteLog(LocalizationService.TextFormat("Log_BackupCleanupFailed", backup), UiLogLevel.Warning);
                    }
                }
                var retained = backups.Where(Directory.Exists).ToList();
                if (cleanupCancelled)
                    WriteLog(LocalizationService.Text("Log_BackupCleanupCancelled"), UiLogLevel.Warning);
                if ((cleanupCancelled || retained.Count > 0) && retained.Count > 0)
                    WriteLog(LocalizationService.TextFormat("Log_BackupsRetained", string.Join("; ", retained)), UiLogLevel.Warning);
            }
            else if (backups.Count > 0)
            {
                WriteLog(LocalizationService.TextFormat("Log_BackupsRetained", string.Join("; ", backups)), UiLogLevel.Warning);
            }
        }

        if (completed)
        {
            progress(100, LocalizationService.Text("Stage_Done"));
            WriteLog(LocalizationService.Text("Log_Complete"), UiLogLevel.Success);
        }
    }

    public static int GetEffectiveWorkers(WorkflowOptions options) =>
        Math.Max(1, options.LowResource ? Math.Min(options.Workers, 2) : options.Workers);

    public static (long RequiredBytes, long FreeBytes) EstimateDiskSpace(
        IEnumerable<PakEntry> entries,
        WorkflowOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long sourceBytes;
        double multiplier;
        if (options.ExtractPaks)
        {
            sourceBytes = entries.Where(entry => entry.CanAttemptExtraction).Sum(entry => entry.SizeBytes);
            var exportsAssets = options.ExportModels || options.ExportTextures ||
                                options.ExportAudio || options.ExportAnimations;
            multiplier = options.ConvertToFbx && (options.KeepGltf || options.MergeModels) ? 6d
                : options.ConvertToFbx ? 4.5d
                : exportsAssets ? 3.5d
                : 2.5d;
        }
        else if (options.ExportModels || options.ExportTextures || options.ExportAudio || options.ExportAnimations)
        {
            sourceBytes = GetDirectorySize(Path.Combine(options.OutputDirectory, "CookedAssets"), cancellationToken);
            multiplier = options.ConvertToFbx && (options.KeepGltf || options.MergeModels) ? 3d
                : options.ConvertToFbx ? 2.2d
                : 1.8d;
        }
        else if (options.ConvertToFbx)
        {
            sourceBytes = GetDirectorySize(Path.Combine(options.OutputDirectory, "ExportedAssets"), cancellationToken);
            multiplier = options.KeepGltf || options.MergeModels || options.Overwrite ? 2.2d : 1.2d;
        }
        else
        {
            sourceBytes = 0;
            multiplier = 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var estimate = sourceBytes * multiplier;
        var scaledEstimate = estimate >= long.MaxValue ? long.MaxValue : (long)estimate;
        var required = Math.Max(512L << 20, scaledEstimate);
        var root = Path.GetPathRoot(Path.GetFullPath(options.OutputDirectory))!;
        return (required, new DriveInfo(root).AvailableFreeSpace);
    }

    private static long GetDirectorySize(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return 0;
        var physicalRoot = PathSafety.ResolvePhysicalPath(path);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(path);
        long total = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var physicalDirectory = PathSafety.ResolvePhysicalPath(directory);
            if (!PathSafety.IsSameOrChild(physicalDirectory, physicalRoot) || !visited.Add(physicalDirectory))
                continue;

            foreach (var file in Directory.GetFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                total = checked(total + info.Length);
            }
            foreach (var child in Directory.GetDirectories(directory))
                pending.Push(child);
        }
        return total;
    }

    private static void EnsureOutputOwnership(WorkflowOptions options)
    {
        var markerPath = Path.Combine(options.OutputDirectory, OutputMarkerName);
        EnsureRegularControlFile(markerPath);
        var normalizedGame = Path.TrimEndingDirectorySeparator(PathSafety.ResolvePhysicalPath(options.GameDirectory));
        if (File.Exists(markerPath))
        {
            OutputMarker? marker;
            try
            {
                marker = JsonSerializer.Deserialize<OutputMarker>(File.ReadAllText(markerPath));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(LocalizationService.Text("Error_OutputMarkerInvalid"), ex);
            }
            if (marker?.Product != OutputMarkerProduct || string.IsNullOrWhiteSpace(marker.GameDirectory))
                throw new InvalidOperationException(LocalizationService.Text("Error_OutputMarkerInvalid"));
            string markerGame;
            try
            {
                markerGame = Path.TrimEndingDirectorySeparator(PathSafety.ResolvePhysicalPath(marker.GameDirectory));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new InvalidOperationException(LocalizationService.Text("Error_OutputMarkerInvalid"), ex);
            }
            if (!markerGame.Equals(normalizedGame, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(LocalizationService.TextFormat("Error_OutputOwnedByOtherGame", marker.GameDirectory));
            return;
        }

        var temporary = markerPath + ".tmp";
        EnsureRegularControlFile(temporary);
        var lockPath = Path.Combine(options.OutputDirectory, ".pakassetstudio.lock");
        var existingEntries = Directory.EnumerateFileSystemEntries(options.OutputDirectory)
            .Where(path => !path.Equals(temporary, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Equals(lockPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (existingEntries.Count > 0)
            throw new InvalidOperationException(LocalizationService.Text("Error_OutputNotOwned"));
        if (File.Exists(temporary)) File.Delete(temporary);

        File.WriteAllText(temporary, JsonSerializer.Serialize(new OutputMarker
        {
            Product = OutputMarkerProduct,
            GameDirectory = normalizedGame
        }));
        File.Move(temporary, markerPath);
    }

    private static void EnsureRegularControlFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || Directory.Exists(path))
            throw new InvalidOperationException(LocalizationService.TextFormat("Error_ControlFileUnsafe", path));
    }

    private void EnsureRequiredTools(WorkflowOptions options)
    {
        if (options.ExtractPaks)
            EnsureFile(pakToolService.RepakPath, "Error_MissingRepak");
        if (options.ExportModels || options.ExportTextures)
            EnsureFile(_umodelPath, "Error_MissingUmodel");
        if (options.MergeModels && options.ExportModels)
        {
            EnsureFile(_mergePath, "Error_MissingMergeScript");
            EnsureFile(_pythonPath, "Error_MissingPython");
        }
        if (options.ConvertToFbx)
        {
            EnsureFile(_assimpPath, "Error_MissingAssimp");
            EnsureFile(_converterPath, "Error_MissingConverter");
            EnsureFile(_pythonPath, "Error_MissingPython");
        }
    }

    private static void ValidateManagedOutputPlan(
        WorkflowOptions options,
        bool preserveGltf,
        string cookedDirectory,
        string exportDirectory,
        string fbxDirectory)
    {
        var managedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.ExtractPaks)
        {
            managedPaths.Add(cookedDirectory);
            destinations.Add(cookedDirectory);
        }
        if (options.ExportModels || options.ExportTextures)
        {
            managedPaths.Add(cookedDirectory);
            managedPaths.Add(exportDirectory);
            managedPaths.Add(fbxDirectory);
            destinations.Add(exportDirectory);
            if (!preserveGltf) destinations.Add(fbxDirectory);
        }
        if (options.ConvertToFbx)
        {
            managedPaths.Add(exportDirectory);
            if (preserveGltf)
            {
                managedPaths.Add(fbxDirectory);
                destinations.Add(fbxDirectory);
            }
        }

        foreach (var path in managedPaths)
            EnsureManagedChild(path, options.OutputDirectory);
        if (options.Overwrite) return;
        foreach (var destination in destinations)
        {
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedOutputExists", destination));
        }
    }

    private static void PrepareManagedDirectory(
        string path,
        string outputRoot,
        bool overwrite,
        ICollection<string> backups,
        Action<string, UiLogLevel> log)
    {
        EnsureManagedChild(path, outputRoot);
        if (Directory.Exists(path))
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                if (!overwrite)
                    throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedOutputExists", path));
                var backup = Path.Combine(
                    outputRoot,
                    $".PakAssetStudio-backup-{Path.GetFileName(path)}-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
                Directory.Move(path, backup);
                backups.Add(backup);
                log(LocalizationService.TextFormat("Log_OutputBackedUp", path, backup), UiLogLevel.Warning);
            }
            else
            {
                Directory.Delete(path);
            }
        }
        Directory.CreateDirectory(path);
    }

    private static void RetireManagedDirectory(
        string path,
        string outputRoot,
        bool overwrite,
        ICollection<string> backups,
        Action<string, UiLogLevel> log)
    {
        EnsureManagedChild(path, outputRoot);
        if (!Directory.Exists(path)) return;
        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
            return;
        }
        if (!overwrite)
            throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedOutputExists", path));
        var backup = Path.Combine(
            outputRoot,
            $".PakAssetStudio-backup-{Path.GetFileName(path)}-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.Move(path, backup);
        backups.Add(backup);
        log(LocalizationService.TextFormat("Log_OutputBackedUp", path, backup), UiLogLevel.Warning);
    }

    private static void EnsureManagedChild(string path, string outputRoot)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedContentLink", path));
        if (File.Exists(path))
            throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedPathInvalid", path));

        var root = Path.TrimEndingDirectorySeparator(PathSafety.ResolvePhysicalPath(outputRoot));
        var candidate = Path.TrimEndingDirectorySeparator(PathSafety.ResolvePhysicalPath(path));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.Text("Error_ManagedPathOutsideOutput"));
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
            var total = EnumerateRegularFiles(source, cancellationToken).LongCount();
            if (total == 0)
            {
                onProgress(1);
                return;
            }
            long index = 0;
            foreach (var sourceFile in EnumerateRegularFiles(source, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, sourceFile);
                var destinationFile = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                if (overwrite || !File.Exists(destinationFile)) File.Copy(sourceFile, destinationFile, overwrite);
                index++;
                if (index % 100 == 0 || index == total)
                    onProgress(index / (double)total);
            }
        }, cancellationToken);
    }

    private static Task DeleteManagedDirectoryAsync(
        string path,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            EnsureManagedChild(path, outputRoot);
            if (!Directory.Exists(path)) return;

            // Validate the complete tree before deleting its first file. Cancellation during
            // deletion may leave a partial managed result, but never follows a directory link.
            ValidateManagedTree(path, cancellationToken);
            var directories = EnumerateManagedDirectories(path, cancellationToken)
                .OrderByDescending(directory => directory.Length)
                .ToList();
            foreach (var file in EnumerateRegularFiles(path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(file);
            }
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(directory);
            }
        }, cancellationToken);
    }

    private static IEnumerable<string> EnumerateManagedDirectories(
        string root,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedContentLink", directory));
            yield return directory;
            foreach (var child in Directory.GetDirectories(directory))
                pending.Push(child);
        }
    }

    private static void ValidateManagedTree(string root, CancellationToken cancellationToken)
    {
        foreach (var _ in EnumerateRegularFiles(root, cancellationToken))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static (bool HasModels, bool HasTextures, bool HasAudio, bool HasAnimations) InspectExportedKinds(
        string root,
        CancellationToken cancellationToken)
    {
        var hasModels = false;
        var hasTextures = false;
        var hasAudio = false;
        var hasAnimations = false;
        foreach (var path in EnumerateRegularFiles(root, cancellationToken))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)) hasModels = true;
            else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase)) hasTextures = true;
            else if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".ue4opus", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".fsb", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) hasAudio = true;
            else if (extension.Equals(".psa", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".md5anim", StringComparison.OrdinalIgnoreCase)) hasAnimations = true;
        }
        return (hasModels, hasTextures, hasAudio, hasAnimations);
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root, CancellationToken cancellationToken)
    {
        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.LinkTarget is not null || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedContentLink", root));
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var child in Directory.GetDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedContentLink", child));
                pending.Push(child);
            }
            foreach (var file in Directory.GetFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(LocalizationService.TextFormat("Error_ManagedContentLink", file));
                yield return file;
            }
        }
    }

    private static double? TryParseConverterProgress(string line)
    {
        var marker = "Processed ";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var token = line[(start + marker.Length)..].Split(';', ' ')[0];
        var parts = token.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], out var done) &&
               double.TryParse(parts[1], out var total) && total > 0
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
                foreach (var file in EnumerateRegularFiles(directory, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total++;
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase)) fbx++;
                    else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) png++;
                }
                log(LocalizationService.TextFormat("Log_Summary", directoryName, total, fbx, png));
            }
        }, cancellationToken);
    }

    private static void EnsureFile(string path, string messageKey)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(LocalizationService.Text(messageKey), path);
    }

    private sealed class OutputMarker
    {
        public OutputMarker() { }

        public string Product { get; init; } = string.Empty;
        public string GameDirectory { get; init; } = string.Empty;
    }
}
