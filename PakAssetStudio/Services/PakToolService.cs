using System.IO;
using System.Text.RegularExpressions;
using PakAssetStudio.Models;

namespace PakAssetStudio.Services;

public sealed class PakToolService(ProcessRunner processRunner)
{
    private readonly string _repakPath = Path.Combine(AppContext.BaseDirectory, "Tools", "repak", "repak.exe");

    public async Task<List<PakEntry>> ScanAsync(
        string root,
        string? aesKey,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken)
    {
        EnsureToolExists();
        var normalizedRoot = Path.GetFullPath(root);
        var files = EnumeratePakFiles(normalizedRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<PakEntry>(files.Count);

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = files[index];
            var info = new FileInfo(path);
            var entry = new PakEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                SizeBytes = info.Length
            };

            var arguments = BuildArguments(aesKey, "info", path);
            try
            {
                var process = await processRunner.RunAsync(
                    _repakPath, arguments, Path.GetDirectoryName(_repakPath), null, cancellationToken);
                if (process.ExitCode == 0)
                {
                    ParseInfo(entry, process.Output);
                    entry.IsValid = true;
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                entry.IsValid = false;
            }

            result.Add(entry);
            onProgress?.Invoke(index + 1, files.Count);
        }

        return result;
    }

    public string RepakPath => _repakPath;

    public static IReadOnlyList<PakEntry> GetExtractionOrder(IEnumerable<PakEntry> entries)
    {
        return entries
            .Where(entry => entry.IsValid)
            .OrderBy(entry => entry.IsPatch ? 2 : entry.IsOptional ? 1 : 0)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        {
            throw new FileNotFoundException("缺少 repak.exe。请重新解压完整软件包。", _repakPath);
        }
    }

    private static IEnumerable<string> EnumeratePakFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory, "*.pak", SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files) yield return file;
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
                entry.IsEncrypted = Value(line).Equals("true", StringComparison.OrdinalIgnoreCase);
            else
            {
                var match = Regex.Match(line, @"^(\d+)\s+file entries$", RegexOptions.IgnoreCase);
                if (match.Success) entry.FileCount = int.Parse(match.Groups[1].Value);
            }
        }

        static string Value(string line) => line[(line.IndexOf(':') + 1)..].Trim();
    }
}
