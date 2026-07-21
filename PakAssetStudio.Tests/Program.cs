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
Assert(firstBatch.Text.Contains("已省略"), "The UI log should report omitted display lines");
Assert(firstBatch.Remaining <= 1_000, "The retained UI backlog should be bounded");

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
