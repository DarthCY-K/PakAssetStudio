using System.IO;

namespace PakAssetStudio.Services;

/// <summary>
/// 跨平台工具路径解析。Windows 使用随包发布的 .exe/.dll 与嵌入式 Python；
/// macOS/Linux 使用无扩展名二进制、.dylib 与系统 Python（工具脚本仅依赖标准库）。
/// </summary>
public static class PlatformPaths
{
    private static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>repak 可执行文件。macOS 上为无扩展名的 Mach-O 二进制（cargo 自编译）。</summary>
    public static string RepakExecutable => Path.Combine(
        AppContext.BaseDirectory, "Tools", "repak",
        IsWindows ? "repak.exe" : "repak");

    /// <summary>Assimp 原生库。macOS 上为 libassimp.dylib（官方 release 或 Homebrew）。</summary>
    public static string AssimpLibrary => Path.Combine(
        AppContext.BaseDirectory, "Tools", "assimp",
        IsWindows ? "assimp-vc143-mt.dll" : "libassimp.dylib");

    /// <summary>
    /// Python 解释器。Windows 使用随包发布的嵌入式运行时；
    /// macOS/Linux 直接调用系统 python3（merge_gltf.py / convert_gltf_to_fbx.py 仅用标准库 + ctypes）。
    /// </summary>
    public static string PythonExecutable => IsWindows
        ? Path.Combine(AppContext.BaseDirectory, "Tools", "python", "python.exe")
        : "python3";
}
