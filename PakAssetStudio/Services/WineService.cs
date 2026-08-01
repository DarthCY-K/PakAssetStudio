using System.IO;

namespace PakAssetStudio.Services;

/// <summary>
/// macOS 上通过 Wine/CrossOver 运行 Windows 版 UModel 的辅助服务。
/// Windows 平台所有方法按“不需要 Wine”短路，行为与旧版完全一致。
/// Wine 的 Z: 盘默认映射到 macOS 根目录 /，Windows 路径即 Z:\ 前缀 + 反斜杠路径。
/// </summary>
public static class WineService
{
    // CrossOver 25.x 的 wine 位于应用包内；Homebrew 按芯片架构分两个前缀目录。
    private static readonly string[] CandidateWinePaths =
    {
        "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine",
        "/opt/homebrew/bin/wine", // Apple Silicon
        "/usr/local/bin/wine"     // Intel
    };

    /// <summary>非 Windows 平台需要 Wine（当前目标是 macOS；Linux 同样适用）。</summary>
    public static bool IsWineNeeded => !OperatingSystem.IsWindows();

    /// <summary>
    /// 探测可用的 wine 可执行文件；Windows 平台或未安装时返回 null。
    /// 探测顺序：CrossOver → Homebrew 常见目录 → PATH。
    /// </summary>
    public static string? FindWineExecutable()
    {
        if (!IsWineNeeded) return null;
        foreach (var candidate in CandidateWinePaths)
        {
            if (File.Exists(candidate)) return candidate;
        }
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(directory, "wine");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // 忽略损坏的 PATH 条目。
                }
            }
        }
        return null;
    }

    /// <summary>
    /// macOS 路径 → Wine 的 Z:\ 盘路径（Wine 默认 Z: 映射到 macOS 根目录 /）。
    /// Windows 平台、相对路径或空路径原样返回。
    /// </summary>
    public static string ToWindowsPath(string path) => ToWindowsPath(path, IsWineNeeded);

    /// <summary>平台无关的路径转换重载（供单元测试直接验证 mac 分支）。</summary>
    public static string ToWindowsPath(string path, bool useWineDrive)
    {
        if (!useWineDrive || string.IsNullOrEmpty(path) || path[0] != '/') return path;
        return "Z:" + path.Replace('/', '\\');
    }

    /// <summary>
    /// 组装 UModel 命令行：macOS 上在 UModel 参数前插入 Windows 路径形式的 exe
    /// （由 wine 解析为待运行程序）；Windows 平台原样返回，行为与旧版一致。
    /// </summary>
    public static IReadOnlyList<string> BuildUmodelArguments(
        string umodelExecutable, IReadOnlyList<string> arguments)
        => BuildUmodelArguments(umodelExecutable, arguments, IsWineNeeded);

    /// <summary>平台无关的命令行组装重载（供单元测试直接验证 mac 分支）。</summary>
    public static IReadOnlyList<string> BuildUmodelArguments(
        string umodelExecutable, IReadOnlyList<string> arguments, bool useWineDrive)
    {
        if (!useWineDrive) return arguments;
        return new[] { ToWindowsPath(umodelExecutable, useWineDrive: true) }
            .Concat(arguments)
            .ToList();
    }
}
