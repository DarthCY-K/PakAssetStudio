using PakAssetStudio.Models;

namespace PakAssetStudio.Services;

/// <summary>
/// 根据 repak 报告的传统 PAK 格式版本推测最接近的 UE4 版本标签。
/// 映射依据 repak README 兼容性表（V2→4.0-4.2 … V11→4.26+）；
/// 同一 PAK 版本可能跨越多个 UE4 版本，取区间内的最高版本（UModel 的版本标签向下兼容旧格式）。
/// </summary>
public static class Ue4ProfileDetector
{
    /// <summary>从扫描结果中推断 UE4 版本标签；无法判断时返回 null。</summary>
    public static string? Detect(IEnumerable<PakEntry> entries)
    {
        var detected = entries
            .Where(entry => entry.IsValid)
            .Select(entry => MapVersion(entry.Version))
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .ToList();
        if (detected.Count == 0) return null;

        // 取出现次数最多的标签，平票时取更高版本
        return detected
            .GroupBy(profile => profile)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => int.Parse(group.Key["ue4.".Length..]))
            .First().Key;
    }

    private static string? MapVersion(string? version) => version?.Trim().ToUpperInvariant() switch
    {
        "V1" => "ue4.0",
        "V2" => "ue4.2",
        "V3" => "ue4.15",
        "V4" => "ue4.19",
        "V5" => "ue4.20",
        "V6" => "ue4.21",
        "V7" => "ue4.21",
        "V8A" => "ue4.22",
        "V8B" => "ue4.24",
        "V9" => "ue4.25",
        "V10" => "ue4.26",
        // V11 同时见于 UE4.26–5.3；本工具只面向 UE4，取 ue4.26 更稳妥，界面会提示可手动切到 ue4.27
        "V11" => "ue4.26",
        _ => null
    };
}
