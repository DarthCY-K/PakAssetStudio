using System.Text.RegularExpressions;

namespace PakAssetStudio.Models;

public sealed class PakEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long SizeBytes { get; set; }
    public string SizeText => SizeBytes >= 1L << 30
        ? $"{SizeBytes / (double)(1L << 30):0.00} GiB"
        : SizeBytes >= 1L << 20
            ? $"{SizeBytes / (double)(1L << 20):0.0} MiB"
            : $"{SizeBytes / 1024d:0.0} KiB";
    public string Version { get; set; } = "-";
    public string Compression { get; set; } = "-";
    public string MountPoint { get; set; } = "-";
    public int FileCount { get; set; }
    public bool IsIndexEncrypted { get; set; }
    /// <summary>repak 能读取 PAK 索引；这不代表所有文件数据都一定能解压。</summary>
    public bool IsValid { get; set; }
    public bool IsCompressionSupported { get; set; } = true;
    public string? ScanError { get; set; }
    public bool CanAttemptExtraction => IsValid && IsCompressionSupported;
    public string Status => !IsValid
        ? Services.LocalizationService.Text("Pak_StatusUnsupported")
        : !IsCompressionSupported
            ? Services.LocalizationService.Text("Pak_StatusCompressionUnsupported")
            : IsIndexEncrypted
                ? Services.LocalizationService.Text("Pak_StatusEncryptedIndex")
                : Services.LocalizationService.Text("Pak_StatusIndexReadable");
    public bool IsPatch => Name.Contains("_P.", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(Name, @"(?:^|[_\-.])patch\d*(?:[_\-.]|$)", RegexOptions.IgnoreCase);
    public bool IsOptional => Regex.IsMatch(Name, @"optional(?:[_\-.]|$)", RegexOptions.IgnoreCase);
}
