using PakAssetStudio.Models;

namespace PakAssetStudio.Services;

public sealed record Ue4ProfileDetection(string? SuggestedProfile, string Range, bool IsAmbiguous);

/// <summary>
/// PAK 容器版本只能限定 UE 版本范围。只有范围唯一时才自动填写 UModel profile；
/// 宽范围和 repak 未确认的版本只给出提示，避免把容器版本误当成 cooked 资产版本。
/// </summary>
public static class Ue4ProfileDetector
{
    public static string? Detect(IEnumerable<PakEntry> entries) => DetectDetailed(entries)?.SuggestedProfile;

    public static Ue4ProfileDetection? DetectDetailed(IEnumerable<PakEntry> entries)
    {
        var candidates = entries
            .Where(entry => entry.IsValid)
            .Select(entry => MapVersion(entry.Version) ?? UnknownVersion(entry.Version))
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return null;

        if (candidates.Count == 1) return candidates[0];

        var exactProfiles = candidates
            .Where(candidate => !candidate.IsAmbiguous && candidate.SuggestedProfile is not null)
            .Select(candidate => candidate.SuggestedProfile!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exactProfiles.Count == 1 && candidates.All(candidate => !candidate.IsAmbiguous))
            return new Ue4ProfileDetection(exactProfiles[0], candidates[0].Range, false);

        return new Ue4ProfileDetection(
            null,
            string.Join(", ", candidates.Select(candidate => candidate.Range).Distinct(StringComparer.OrdinalIgnoreCase)),
            true);
    }

    private static Ue4ProfileDetection UnknownVersion(string? version)
    {
        var value = string.IsNullOrWhiteSpace(version) || version == "-" ? "?" : version.Trim();
        return new Ue4ProfileDetection(null, $"PAK {value}", true);
    }

    public static Ue4ProfileDetection? MapVersion(string? version) => version?.Trim().ToUpperInvariant() switch
    {
        // repak 将 V1、V6、V10 标记为版本归属或读取能力未确认，不能据此选择 UModel profile。
        "V1" => new(null, "PAK V1 (?)", true),
        "V2" => new(null, "UE4.0-UE4.2", true),
        "V3" => new(null, "UE4.3-UE4.15", true),
        "V4" => new(null, "UE4.16-UE4.19", true),
        "V5" => new("ue4.20", "UE4.20", false),
        "V6" => new(null, "PAK V6 (?)", true),
        "V7" => new("ue4.21", "UE4.21", false),
        "V8A" => new("ue4.22", "UE4.22", false),
        "V8B" => new(null, "UE4.23-UE4.24", true),
        "V9" => new("ue4.25", "UE4.25", false),
        "V10" => new(null, "PAK V10 (?)", true),
        "V11" => new(null, "UE4.26-UE4.27 / UE5", true),
        _ => null
    };
}
