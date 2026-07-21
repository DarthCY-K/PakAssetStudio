namespace PakAssetStudio.Models;

public sealed class PakEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long SizeBytes { get; init; }
    public string SizeText => SizeBytes >= 1L << 30
        ? $"{SizeBytes / (double)(1L << 30):0.00} GiB"
        : $"{SizeBytes / (double)(1L << 20):0.0} MiB";
    public string Version { get; set; } = "-";
    public string Compression { get; set; } = "-";
    public string MountPoint { get; set; } = "-";
    public int FileCount { get; set; }
    public bool IsEncrypted { get; set; }
    public bool IsValid { get; set; }
    public string Status => IsValid
        ? (IsEncrypted ? Services.LocalizationService.Text("Pak_StatusEncrypted") : Services.LocalizationService.Text("Pak_StatusReadable"))
        : Services.LocalizationService.Text("Pak_StatusUnsupported");
    public bool IsPatch => Name.Contains("_P.", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("patch", StringComparison.OrdinalIgnoreCase);
    public bool IsOptional => Name.Contains("optional", StringComparison.OrdinalIgnoreCase);
}
