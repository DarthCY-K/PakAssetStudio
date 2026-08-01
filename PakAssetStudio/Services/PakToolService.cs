using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using PakAssetStudio.Models;

namespace PakAssetStudio.Services;

public sealed class PakToolService(IProcessRunner processRunner)
{
    private static readonly HashSet<string> SupportedCompressionMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "zlib", "gzip", "zstd", "oodle"
    };

    private readonly string _repakPath = PlatformPaths.RepakExecutable;

    public async Task<List<PakEntry>> ScanAsync(
        string root,
        IReadOnlyList<string>? aesKeys,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken,
        ProcessPriorityClass? priority = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureToolExists();
        var normalizedRoot = Path.GetFullPath(root);
        var files = EnumeratePakFiles(normalizedRoot, cancellationToken).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<PakEntry>(files.Count);

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = files[index];
            var entry = new PakEntry
            {
                Name = Path.GetFileName(path),
                FullPath = Path.GetFullPath(path)
            };

            try
            {
                entry.SizeBytes = new FileInfo(path).Length;
                // 先不带密钥读取索引；失败后逐个密钥重试，记录第一个成功的密钥供解包复用。
                // 多 KeyGuid 游戏的 pak 可能各自匹配不同密钥。
                foreach (var key in TryKeys(aesKeys))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var process = await processRunner.RunAsync(
                        _repakPath, BuildArguments(key, "info", path), Path.GetDirectoryName(_repakPath), null, cancellationToken, priority);
                    if (process.ExitCode == 0)
                    {
                        ParseInfo(entry, process.Output);
                        entry.IsValid = true;
                        entry.IsCompressionSupported = IsCompressionSupported(entry.Compression);
                        if (!entry.IsCompressionSupported)
                            entry.ScanError = LocalizationService.TextFormat("Pak_ErrorCompression", entry.Compression);
                        entry.AesKeyUsed = key;
                        break;
                    }

                    entry.ScanError = BuildDiagnostic(process.Output, aesKeys, process.ExitCode);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProcessLaunchException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entry.IsValid = false;
                entry.ScanError = RedactSensitive(ex.Message, aesKeys);
            }

            result.Add(entry);
            onProgress?.Invoke(index + 1, files.Count);
        }

        return result;
    }

    private static IEnumerable<string?> TryKeys(IReadOnlyList<string>? aesKeys)
    {
        yield return null;
        if (aesKeys is null) yield break;
        foreach (var key in aesKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                yield return key.Trim();
        }
    }

    public string RepakPath => _repakPath;

    public static IReadOnlyList<PakEntry> GetExtractionOrder(IEnumerable<PakEntry> entries)
    {
        return entries
            .Where(entry => entry.CanAttemptExtraction)
            .OrderBy(entry => entry.IsPatch ? 2 : entry.IsOptional ? 1 : 0)
            .ThenBy(entry => entry.IsPatch ? GetPatchKey(entry.Name) : default)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsCompressionSupported(string? compression)
    {
        if (string.IsNullOrWhiteSpace(compression)) return false;

        var value = compression.Trim();
        var hasOpeningBracket = value.StartsWith('[');
        var hasClosingBracket = value.EndsWith(']');
        if (hasOpeningBracket != hasClosingBracket) return false;
        if (hasOpeningBracket) value = value[1..^1].Trim();
        if (value.Length == 0) return false;

        var methods = value.Split(',', StringSplitOptions.TrimEntries);
        return methods.All(method => method.Length > 0 && SupportedCompressionMethods.Contains(method));
    }

    public static List<string> BuildArguments(string? aesKey, params string[] command)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(aesKey))
        {
            arguments.Add("--aes-key");
            arguments.Add(aesKey.Trim());
        }
        arguments.AddRange(command);
        return arguments;
    }

    private void EnsureToolExists()
    {
        if (!File.Exists(_repakPath))
            throw new FileNotFoundException(LocalizationService.Text("Error_MissingRepak"), _repakPath);
    }

    /// <summary>
    /// 补丁排序键：(chunk, patch) 二元组。
    /// 标准 UE 命名 <c>pakchunk&lt;N&gt;_&lt;M&gt;_P.pak</c> 按 (N, M) 排序，使同一 chunk 的
    /// patch 链不被其他 chunk 打断；老式 <c>pakchunk&lt;N&gt;_P.pak</c> 视为该 chunk 的
    /// 第 0 号 patch；<c>patch&lt;N&gt;.pak</c> 与无编号 <c>xxx_P.pak</c> 缺少 chunk 信息，
    /// 统一排在最后（前者按 N 递增，后者用 MaxValue 兜底）。
    /// </summary>
    private static (long Chunk, long Patch) GetPatchKey(string name)
    {
        var match = Regex.Match(name, @"pakchunk(\d+)_(\d+)_P(?:\.|$)", RegexOptions.IgnoreCase);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var chunk) &&
            long.TryParse(match.Groups[2].Value, out var patch))
            return (chunk, patch);

        match = Regex.Match(name, @"pakchunk(\d+)_P(?:\.|$)", RegexOptions.IgnoreCase);
        if (match.Success && long.TryParse(match.Groups[1].Value, out chunk))
            return (chunk, 0);

        match = Regex.Match(name, @"(?:^|[_\-\.])patch(\d+)(?:[_\-\.]|$)", RegexOptions.IgnoreCase);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var number))
            return (long.MaxValue, number);

        return (long.MaxValue, long.MaxValue);
    }

    private static string BuildDiagnostic(string output, IReadOnlyList<string>? aesKeys, int exitCode)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        var diagnostic = string.IsNullOrWhiteSpace(line)
            ? LocalizationService.TextFormat("Pak_ErrorExitCode", exitCode)
            : line;
        diagnostic = RedactSensitive(diagnostic, aesKeys);
        return diagnostic.Length <= 320 ? diagnostic : diagnostic[..320] + "...";
    }

    public static string RedactSensitive(string value, string? secret)
        => RedactSensitive(value, secret is null ? null : new[] { secret });

    public static string RedactSensitive(string value, IEnumerable<string>? secrets)
    {
        if (secrets is null) return value;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrWhiteSpace(secret)) continue;
            var normalized = secret.Trim();
            var redacted = value.Replace(normalized, "***", StringComparison.OrdinalIgnoreCase);
            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && normalized.Length > 2)
                redacted = redacted.Replace(normalized[2..], "***", StringComparison.OrdinalIgnoreCase);
            value = redacted;
        }
        return value;
    }

    private static IEnumerable<string> EnumeratePakFiles(string root, CancellationToken cancellationToken)
    {
        var physicalRoot = PathSafety.ResolvePhysicalPath(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string physicalDirectory;
            string[] files;
            string[] directories;
            try
            {
                physicalDirectory = PathSafety.ResolvePhysicalPath(directory);
                if (!PathSafety.IsSameOrChild(physicalDirectory, physicalRoot) || !visited.Add(physicalDirectory))
                    continue;
                files = Directory.GetFiles(directory, "*.pak", SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }
            foreach (var child in directories) pending.Push(child);
        }
    }

    private static void ParseInfo(PakEntry entry, string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("mount point:", StringComparison.OrdinalIgnoreCase))
                entry.MountPoint = Value(line);
            else if (line.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                entry.Version = Value(line);
            else if (line.StartsWith("compression:", StringComparison.OrdinalIgnoreCase))
                entry.Compression = Value(line);
            else if (line.StartsWith("encrypted index:", StringComparison.OrdinalIgnoreCase))
                entry.IsIndexEncrypted = Value(line).Equals("true", StringComparison.OrdinalIgnoreCase);
            else
            {
                var match = Regex.Match(line, @"^(\d+)\s+file entries$", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
                    entry.FileCount = count;
            }
        }

        static string Value(string line) => line[(line.IndexOf(':') + 1)..].Trim();
    }
}
