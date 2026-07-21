using PakAssetStudio.Models;
using PakAssetStudio.Services;

var sampleEntries = new List<PakEntry>
{
    new() { Name = "pakchunk0optional-WindowsNoEditor.pak", FullPath = "optional.pak", IsValid = true },
    new() { Name = "pakchunk0-WindowsNoEditor_P.pak", FullPath = "patch.pak", IsValid = true },
    new() { Name = "pakchunk0-WindowsNoEditor.pak", FullPath = "base.pak", IsValid = true }
};
var ordered = PakToolService.GetExtractionOrder(sampleEntries);
Assert(!ordered[0].IsOptional, "The base PAK must be extracted before the optional PAK");
Assert(ordered[0].Name == "pakchunk0-WindowsNoEditor.pak", "The base PAK must be extracted first");
Assert(ordered[^1].IsPatch, "The patch PAK must be extracted last");

var logBuffer = new UiLogBuffer();
for (var index = 0; index < 50_000; index++) logBuffer.Enqueue($"stress-line-{index}");
var firstBatch = logBuffer.Drain();
Assert(firstBatch.Dropped >= 45_000, "A large UI log backlog should discard old display lines");
Assert(firstBatch.Lines.Any(line => line.Text.Contains("已省略")), "The UI log should report omitted display lines");
Assert(firstBatch.Lines[0].Level == UiLogLevel.Warning, "The omitted-lines notice should be a warning");
Assert(firstBatch.Remaining <= 1_000, "The retained UI backlog should be bounded");

var versionEntries = new List<PakEntry>
{
    new() { Name = "base.pak", FullPath = "base.pak", IsValid = true, Version = "V8B" },
    new() { Name = "patch.pak", FullPath = "patch.pak", IsValid = true, Version = "V8A" },
    new() { Name = "broken.pak", FullPath = "broken.pak", IsValid = false, Version = "V11" }
};
Assert(Ue4ProfileDetector.Detect(versionEntries) == "ue4.24", "V8B should map to the highest UE4 version of its range");
Assert(Ue4ProfileDetector.Detect([new PakEntry { Name = "a.pak", FullPath = "a.pak", IsValid = true, Version = "V11" }]) == "ue4.26", "V11 should map to ue4.26 (the lower of its UE4 range)");
Assert(Ue4ProfileDetector.Detect([new PakEntry { Name = "a.pak", FullPath = "a.pak", IsValid = false, Version = "V11" }]) is null, "Invalid PAKs must not affect detection");
Assert(Ue4ProfileDetector.Detect([new PakEntry { Name = "a.pak", FullPath = "a.pak", IsValid = true, Version = "-" }]) is null, "Unknown versions must not affect detection");
Console.WriteLine("PASS: UE4 profile detection maps PAK versions and ignores invalid entries");

var normalOptions = new WorkflowOptions { GameDirectory = ".", OutputDirectory = ".", GameProfile = "ue4.26", Workers = 8 };
var lowOptions = new WorkflowOptions { GameDirectory = ".", OutputDirectory = ".", GameProfile = "ue4.26", Workers = 8, LowResource = true };
Assert(WorkflowService.GetEffectiveWorkers(normalOptions) == 8, "Normal mode must keep the configured worker count");
Assert(WorkflowService.GetEffectiveWorkers(lowOptions) == 2, "Low-resource mode must clamp workers to 2");
Console.WriteLine("PASS: low-resource mode clamps worker parallelism");

var languageDir = Directory.CreateTempSubdirectory("pakassetstudio-lang-");
try
{
    File.WriteAllText(Path.Combine(languageDir.FullName, "zh-CN.json"), """
        { "code": "zh-CN", "language": "简体中文", "strings": { "Greeting": "你好", "OnlyZh": "仅中文" } }
        """);
    File.WriteAllText(Path.Combine(languageDir.FullName, "en-US.json"), """
        { "code": "en-US", "language": "English", "strings": { "Greeting": "Hello" } }
        """);
    var localization = new LocalizationService(languageDir.FullName);
    localization.Initialize();
    Assert(localization.AvailableLanguages.Count == 2, "Both shipped language files should be discovered");
    Assert(localization.Get("Greeting") == "你好", "Default language should be Simplified Chinese");
    localization.SetLanguage("en-US");
    Assert(localization.Get("Greeting") == "Hello", "Switching to English should return the English text");
    Assert(localization.Get("OnlyZh") == "仅中文", "Missing entries must fall back to Simplified Chinese");
    Assert(localization.Get("Unknown_Key") == "Unknown_Key", "Completely missing keys should render the key itself");
    Console.WriteLine("PASS: localization loads external files, falls back to zh-CN and renders unknown keys");
}
finally
{
    languageDir.Delete(recursive: true);
}

Console.WriteLine($"PASS: extraction order={string.Join(" -> ", ordered.Select(entry => entry.Name))}");
Console.WriteLine($"PASS: throttled 50,000 UI log lines; dropped={firstBatch.Dropped}; remaining={firstBatch.Remaining}");

if (args.Length == 1)
{
    Assert(Directory.Exists(args[0]), $"PAK test directory does not exist: {args[0]}");
    var service = new PakToolService(new ProcessRunner());
    var entries = await service.ScanAsync(args[0], null, null, CancellationToken.None);
    Assert(entries.Count > 0, "No PAK files were found");
    Assert(entries.All(entry => entry.IsValid), "Every test PAK should be readable");
    foreach (var entry in entries)
        Console.WriteLine($"SCAN: {entry.Name} valid={entry.IsValid} version={entry.Version} files={entry.FileCount} status={entry.Status}");
}
else if (args.Length > 1)
{
    Console.Error.WriteLine("Usage: PakAssetStudio.Tests [paks-directory]");
    return 2;
}

return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
