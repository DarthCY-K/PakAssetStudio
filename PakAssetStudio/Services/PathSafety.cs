using System.IO;

namespace PakAssetStudio.Services;

public static class PathSafety
{
    public static void EnsureOutputOutsideGame(string gameDirectory, string outputDirectory)
    {
        var selected = ResolvePhysicalPath(gameDirectory);
        var game = ResolvePhysicalPath(GetProtectedGameDirectory(gameDirectory));
        var output = ResolvePhysicalPath(outputDirectory);
        if (IsSameOrChild(output, selected) || IsSameOrChild(output, game))
            throw new InvalidOperationException(LocalizationService.Text("Error_OutputInsideGame"));
    }

    public static string GetProtectedGameDirectory(string selectedDirectory)
    {
        var selected = new DirectoryInfo(ResolvePhysicalPath(selectedDirectory));
        for (var current = selected; current is not null; current = current.Parent)
        {
            if (!current.Name.Equals("Paks", StringComparison.OrdinalIgnoreCase)) continue;
            var content = current.Parent;
            if (content?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true && content.Parent is not null)
                return content.Parent.FullName;
            return current.FullName;
        }
        return selected.FullName;
    }

    public static bool IsSameOrChild(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        var normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        var parentWithSeparator = normalizedParent + Path.DirectorySeparatorChar;
        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(LocalizationService.Text("Dialog_InvalidPath"));
        var relative = fullPath[root.Length..];
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Where(segment => !string.IsNullOrWhiteSpace(segment)))
        {
            var candidate = Path.Combine(current, segment);
            if (Directory.Exists(candidate))
            {
                var target = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
                current = target?.FullName ?? candidate;
            }
            else
            {
                current = candidate;
            }
        }
        return Path.GetFullPath(current);
    }
}
