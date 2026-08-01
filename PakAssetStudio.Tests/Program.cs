using System.Diagnostics;
using PakAssetStudio.Models;
using PakAssetStudio.Services;
using Xunit;

namespace PakAssetStudio.Tests;

public sealed class PakToolTests
{
    [Fact]
    public void ExtractionOrderUsesNaturalPatchOrderAndSkipsUnsupportedCompression()
    {
        var entries = new List<PakEntry>
        {
            Entry("pakchunk0_10_P.pak", valid: true),
            Entry("Dispatch.pak", valid: true),
            Entry("pakchunk0optional.pak", valid: true),
            Entry("pakchunk0_2_P.pak", valid: true),
            Entry("pakchunk0.pak", valid: true),
            new()
            {
                Name = "pakchunk1.pak", FullPath = "pakchunk1.pak", IsValid = true,
                IsCompressionSupported = false
            }
        };

        var ordered = PakToolService.GetExtractionOrder(entries);

        Assert.Equal(
            new[] { "Dispatch.pak", "pakchunk0.pak", "pakchunk0optional.pak", "pakchunk0_2_P.pak", "pakchunk0_10_P.pak" },
            ordered.Select(entry => entry.Name));
    }

    [Fact]
    public void PatchOrderGroupsByChunkAndKeepsUnknownPatchesLast()
    {
        var entries = new List<PakEntry>
        {
            Entry("pakchunk1_1_P.pak", valid: true),
            Entry("pakchunk0_2_P.pak", valid: true),
            Entry("pakchunk0_10_P.pak", valid: true),
            Entry("pakchunk1_P.pak", valid: true),
            Entry("patch2.pak", valid: true),
            Entry("patch1.pak", valid: true),
            Entry("Content_P.pak", valid: true),
        };

        var ordered = PakToolService.GetExtractionOrder(entries);

        // 同一 chunk 的 patch 链连续且按自然序号；缺少 chunk 信息的 patch 系列与
        // 无编号 patch 整体排在标准命名之后，避免打断 pakchunkN 链。
        Assert.Equal(
            new[]
            {
                "pakchunk0_2_P.pak", "pakchunk0_10_P.pak",
                "pakchunk1_P.pak", "pakchunk1_1_P.pak",
                "patch1.pak", "patch2.pak",
                "Content_P.pak"
            },
            ordered.Select(entry => entry.Name));
    }

    [Theory]
    [InlineData("None", true)]
    [InlineData("Zlib", true)]
    [InlineData("[Zlib, Gzip, Zstd]", true)]
    [InlineData("Oodle", true)]
    [InlineData("Zlib, Oodle", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("-", false)]
    [InlineData("[]", false)]
    [InlineData("123", false)]
    [InlineData("[Zlib, ???]", false)]
    [InlineData("[Zlib", false)]
    public void CompressionCapabilityIsExplicit(string? value, bool expected)
    {
        Assert.Equal(expected, PakToolService.IsCompressionSupported(value));
    }

    [Fact]
    public async Task ScanDoesNotFollowDirectoryLinksOutsideTheSelectedRoot()
    {
        using var directory = new TemporaryDirectory();
        var game = Directory.CreateDirectory(Path.Combine(directory.Path, "Game")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(directory.Path, "Outside")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(game, "inside.pak"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.pak"), [2]);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(game, "OutsideLink"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return;
        }
        var runner = new FakeProcessRunner(new ProcessResult(0, """
            version: V9
            compression: Zlib
            1 file entries
            """));
        var service = new PakToolService(runner);

        var entries = await service.ScanAsync(game, null, null, CancellationToken.None);

        Assert.Single(entries);
        Assert.Equal("inside.pak", entries[0].Name);
    }

    [Fact]
    public async Task ScanDoesNotCallAPakWithMissingCompressionMetadataExtractable()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "unknown.pak"), [1, 2, 3]);
        var runner = new FakeProcessRunner(new ProcessResult(0, """
            mount point: ../../../
            version: V11
            encrypted index: false
            12 file entries
            """));
        var service = new PakToolService(runner);

        var entry = Assert.Single(await service.ScanAsync(
            directory.Path, null, null, CancellationToken.None));

        Assert.True(entry.IsValid);
        Assert.Equal("-", entry.Compression);
        Assert.False(entry.CanAttemptExtraction);
    }

    [Fact]
    public async Task ScanMarksAnOodlePakExtractable()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "oodle.pak"), [1, 2, 3]);
        var runner = new FakeProcessRunner(new ProcessResult(0, """
            mount point: ../../../
            version: V11
            compression: Oodle
            encrypted index: false
            12 file entries
            """));
        var service = new PakToolService(runner);

        var entry = Assert.Single(await service.ScanAsync(
            directory.Path, null, null, CancellationToken.None));

        Assert.True(entry.IsValid);
        Assert.True(entry.CanAttemptExtraction);
    }

    [Fact]
    public async Task RepakLaunchFailureStopsTheScanInsteadOfMislabelingEveryPak()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "one.pak"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "two.pak"), [2]);
        var service = new PakToolService(new LaunchFailureProcessRunner());

        await Assert.ThrowsAsync<ProcessLaunchException>(() => service.ScanAsync(
            directory.Path, null, null, CancellationToken.None));
    }

    [Fact]
    public void AesRedactionCoversCaseAndOptionalHexPrefix()
    {
        var redacted = PakToolService.RedactSensitive(
            "repak echoed ABCDEF0123456789", "0xabcdef0123456789");

        Assert.DoesNotContain("ABCDEF0123456789", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public async Task ScanRedactsAesKeyFromDiagnostics()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "encrypted.pak"), [1, 2, 3]);
        var runner = new FakeProcessRunner(new[]
        {
            new ProcessResult(1, "bad key: secret-value"),
            new ProcessResult(1, "bad key: secret-value")
        });
        var service = new PakToolService(runner);

        var entries = await service.ScanAsync(
            directory.Path, ["secret-value"], null, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.False(entry.IsValid);
        Assert.NotNull(entry.ScanError);
        Assert.DoesNotContain("secret-value", entry.ScanError);
        Assert.Contains("***", entry.ScanError);
    }

    [Fact]
    public async Task ScanRetriesWithNextAesKeyWhenIndexReadFails()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "encrypted.pak"), [1, 2, 3]);
        var runner = new FakeProcessRunner(new[]
        {
            new ProcessResult(1, "bad key: first-secret"),
            new ProcessResult(1, "bad key: second-secret"),
            new ProcessResult(0, "version: V9\ncompression: Zlib\n1 file entries")
        });
        var service = new PakToolService(runner);

        var entries = await service.ScanAsync(
            directory.Path, ["first-secret", "second-secret"], null, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsValid);
        Assert.Equal("second-secret", entry.AesKeyUsed);
        var infoCalls = runner.Calls.Where(call => call.Arguments.Contains("info")).ToList();
        Assert.Equal(3, infoCalls.Count); // 无密钥 + key1 + key2
        Assert.DoesNotContain("--aes-key", infoCalls[0].Arguments);
        Assert.Equal("--aes-key", infoCalls[1].Arguments[0]);
        Assert.Equal("first-secret", infoCalls[1].Arguments[1]);
        Assert.Equal("second-secret", infoCalls[2].Arguments[1]);
    }

    [Fact]
    public async Task ScanWithAllKeysFailingKeepsLastRedactedDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "encrypted.pak"), [1, 2, 3]);
        var runner = new FakeProcessRunner(new[]
        {
            new ProcessResult(1, "failed with first-secret"),
            new ProcessResult(1, "failed with first-secret"),
            new ProcessResult(1, "failed with second-secret")
        });
        var service = new PakToolService(runner);

        var entries = await service.ScanAsync(
            directory.Path, ["first-secret", "second-secret"], null, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.False(entry.IsValid);
        Assert.NotNull(entry.ScanError);
        Assert.DoesNotContain("first-secret", entry.ScanError);
        Assert.DoesNotContain("second-secret", entry.ScanError);
        Assert.Contains("***", entry.ScanError);
    }

    [Fact]
    public void RedactSensitiveCoversMultipleSecretsIncludingHexPrefixVariants()
    {
        var redacted = PakToolService.RedactSensitive(
            "keys abcdef1234567890 and 0x1234567890abcdef appear",
            ["0xabcdef1234567890", "0x1234567890abcdef"]);

        Assert.DoesNotContain("abcdef1234567890", redacted);
        Assert.DoesNotContain("1234567890abcdef", redacted);
        Assert.Equal(2, redacted.Split("***").Length - 1);
    }

    private static PakEntry Entry(string name, bool valid) =>
        new() { Name = name, FullPath = name, IsValid = valid };
}

public sealed class ProfileDetectionTests
{
    [Theory]
    [InlineData("V5", "ue4.20")]
    [InlineData("V7", "ue4.21")]
    [InlineData("V8A", "ue4.22")]
    [InlineData("V9", "ue4.25")]
    public void ExactContainerVersionsCanFillProfile(string version, string expected)
    {
        Assert.Equal(expected, Ue4ProfileDetector.Detect([Entry(version)]));
    }

    [Theory]
    [InlineData("V2")]
    [InlineData("V3")]
    [InlineData("V4")]
    [InlineData("V8B")]
    [InlineData("V11")]
    public void RangedContainerVersionsNeverGuessProfile(string version)
    {
        var detection = Ue4ProfileDetector.DetectDetailed([Entry(version)]);

        Assert.NotNull(detection);
        Assert.True(detection.IsAmbiguous);
        Assert.Null(detection.SuggestedProfile);
    }

    [Theory]
    [InlineData("V1")]
    [InlineData("V6")]
    [InlineData("V10")]
    public void UnconfirmedContainerVersionsNeverGuessProfile(string version)
    {
        var detection = Ue4ProfileDetector.DetectDetailed([Entry(version)]);

        Assert.NotNull(detection);
        Assert.True(detection.IsAmbiguous);
        Assert.Null(detection.SuggestedProfile);
    }

    [Fact]
    public void MissingContainerVersionIsAmbiguous()
    {
        var detection = Ue4ProfileDetector.DetectDetailed([Entry("-")]);

        Assert.NotNull(detection);
        Assert.True(detection.IsAmbiguous);
        Assert.Null(detection.SuggestedProfile);
    }

    [Fact]
    public void UnknownVersionPreventsAutomaticProfileFromKnownEntries()
    {
        var detection = Ue4ProfileDetector.DetectDetailed([Entry("V9"), Entry("V12")]);

        Assert.NotNull(detection);
        Assert.True(detection.IsAmbiguous);
        Assert.Null(detection.SuggestedProfile);
        Assert.Contains("V12", detection.Range);
    }

    private static PakEntry Entry(string version) =>
        new() { Name = "test.pak", FullPath = "test.pak", IsValid = true, Version = version };
}

public sealed class LogAndResourceTests
{
    [Fact]
    public async Task ProcessOutputCanBeStreamedWithoutBeingRetained()
    {
        var lines = new List<string>();
        var runner = new ProcessRunner();
        var command = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = await runner.RunAsync(
            command,
            ["/c", "echo streamed-line"],
            null,
            lines.Add,
            CancellationToken.None,
            captureOutput: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains(lines, line => line.Contains("streamed-line", StringComparison.Ordinal));
    }

    [Fact]
    public void UiLogBacklogIsBoundedAndUsesInjectedLocalizedNotice()
    {
        var buffer = new UiLogBuffer(count => $"omitted:{count}");
        for (var index = 0; index < 50_000; index++) buffer.Enqueue($"line-{index}");

        var batch = buffer.Drain();

        Assert.True(batch.Dropped >= 45_000);
        Assert.StartsWith("omitted:", batch.Lines[0].Text);
        Assert.Equal(UiLogLevel.Warning, batch.Lines[0].Level);
        Assert.True(batch.Remaining <= 1_000);
    }

    [Fact]
    public void DiskEstimateHonorsCancellationBeforeTraversingInputs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WorkflowService.EstimateDiskSpace([], Options(1, false), cancellation.Token));
    }

    [Fact]
    public void MergeAndFbxEstimateIncludesForcedGltfCopy()
    {
        const long pakSize = 100L << 20;
        var options = new WorkflowOptions
        {
            GameDirectory = ".",
            OutputDirectory = ".",
            GameProfile = "ue4.25",
            ExtractPaks = true,
            ConvertToFbx = true,
            MergeModels = true,
            KeepGltf = false,
            Workers = 1
        };
        var entry = new PakEntry
        {
            Name = "base.pak",
            FullPath = "base.pak",
            IsValid = true,
            SizeBytes = pakSize
        };

        var estimate = WorkflowService.EstimateDiskSpace([entry], options);

        Assert.Equal(600L << 20, estimate.RequiredBytes);
    }

    [Fact]
    public void LowResourceModeClampsWorkersAndNeverReturnsZero()
    {
        var normal = Options(workers: 8, lowResource: false);
        var low = Options(workers: 8, lowResource: true);
        var invalid = Options(workers: 0, lowResource: true);

        Assert.Equal(8, WorkflowService.GetEffectiveWorkers(normal));
        Assert.Equal(2, WorkflowService.GetEffectiveWorkers(low));
        Assert.Equal(1, WorkflowService.GetEffectiveWorkers(invalid));
    }

    private static WorkflowOptions Options(int workers, bool lowResource) => new()
    {
        GameDirectory = ".",
        OutputDirectory = ".",
        GameProfile = "ue4.26",
        Workers = workers,
        LowResource = lowResource
    };
}

public sealed class LocalizationTests
{
    [Fact]
    public void FilesLoadWithFallbackAndMalformedFormatsDoNotCrash()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "zh-CN.json"), """
            { "code": "zh-CN", "language": "简体中文", "strings": {
              "Greeting": "你好", "OnlyZh": "仅中文", "Broken": "{0"
            } }
            """);
        File.WriteAllText(Path.Combine(directory.Path, "en-US.json"), """
            { "code": "en-US", "language": "English", "strings": { "Greeting": "Hello" } }
            """);
        File.WriteAllText(Path.Combine(directory.Path, "custom-name.json"), """
            { "code": "fr-FR", "language": "Francais", "strings": { "Greeting": "Bonjour" } }
            """);
        File.WriteAllText(Path.Combine(directory.Path, "unsafe.json"), """
            { "code": "../outside", "language": "Unsafe", "strings": { "Greeting": "Bad" } }
            """);

        var localization = new LocalizationService(directory.Path);
        localization.Initialize();
        Assert.Equal(3, localization.AvailableLanguages.Count);
        localization.SetLanguage("fr-FR");
        Assert.Equal("Bonjour", localization.Get("Greeting"));
        Assert.Equal("仅中文", localization.Get("OnlyZh"));

        localization.SetLanguage("en-US");
        Assert.Equal("Hello", localization.Get("Greeting"));
        Assert.Equal("仅中文", localization.Get("OnlyZh"));
        Assert.Equal("Unknown_Key", localization.Get("Unknown_Key"));
        Assert.Equal("{0", localization.Format("Broken", 123));
    }
}

public sealed class OutputOwnershipTests
{
    [Fact]
    public async Task TextureOnlyExportCannotRequestFbxConversion()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = System.IO.Path.Combine(root.Path, "Output");
        var service = CreateService();
        var options = new WorkflowOptions
        {
            GameDirectory = game,
            OutputDirectory = output,
            GameProfile = "ue4.25",
            ExportTextures = true,
            ConvertToFbx = true,
            Workers = 1
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            [], options, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.Equal(LocalizationService.Text("Error_FbxRequiresModels"), error.Message);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task PreCancelledWorkflowDoesNotCreateOutput()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = System.IO.Path.Combine(root.Path, "Output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(
            [], Options(game, output), (_, _) => { }, (_, _) => { }, cancellation.Token));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void OutputInsideGameIsRejectedBeforeWorkflowWritesAnything()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = System.IO.Path.Combine(game, "Export");

        Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureOutputOutsideGame(game, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void SelectingContentPaksProtectsTheContainingGameDirectory()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "GameName")).FullName;
        var content = Directory.CreateDirectory(System.IO.Path.Combine(game, "Content")).FullName;
        var paks = Directory.CreateDirectory(System.IO.Path.Combine(content, "Paks")).FullName;
        var unsafeOutput = System.IO.Path.Combine(game, "Exports");
        var safeOutput = System.IO.Path.Combine(root.Path, "Exports");

        Assert.Equal(game, PathSafety.GetProtectedGameDirectory(paks));
        Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureOutputOutsideGame(paks, unsafeOutput));
        PathSafety.EnsureOutputOutsideGame(paks, safeOutput);
    }

    [Fact]
    public void SelectingContentPaksThroughDirectoryLinkProtectsThePhysicalGameDirectory()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "GameName")).FullName;
        var content = Directory.CreateDirectory(System.IO.Path.Combine(game, "Content")).FullName;
        var paks = Directory.CreateDirectory(System.IO.Path.Combine(content, "Paks")).FullName;
        var alias = System.IO.Path.Combine(root.Path, "SelectedPaks");
        try
        {
            Directory.CreateSymbolicLink(alias, paks);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return;
        }

        Assert.Equal(game, PathSafety.GetProtectedGameDirectory(alias));
        Assert.Throws<InvalidOperationException>(() =>
            PathSafety.EnsureOutputOutsideGame(alias, System.IO.Path.Combine(game, "Exports")));
    }

    [Fact]
    public void OutputLinkBackIntoGameIsRejected()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var target = Directory.CreateDirectory(System.IO.Path.Combine(game, "Target")).FullName;
        var link = System.IO.Path.Combine(root.Path, "OutputLink");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureOutputOutsideGame(game, link));
    }

    [Fact]
    public async Task OutputControlFileLinksAreRejectedWithoutTouchingTheirTarget()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var target = System.IO.Path.Combine(root.Path, "outside.txt");
        await File.WriteAllTextAsync(target, "keep");
        try
        {
            File.CreateSymbolicLink(System.IO.Path.Combine(output, ".pakassetstudio.lock"), target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return;
        }
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            [], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task NonEmptyUnownedOutputIsRejected()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        await File.WriteAllTextAsync(System.IO.Path.Combine(output, "unrelated.txt"), "keep");
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            [], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.True(File.Exists(System.IO.Path.Combine(output, "unrelated.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(output, ".pakassetstudio-output.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(output, ".pakassetstudio.lock")));
    }

    [Fact]
    public async Task DamagedOwnershipMarkerStopsWithoutTouchingOtherFiles()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var unrelated = System.IO.Path.Combine(output, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(output, ".pakassetstudio-output.json"), "not-json");
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            [], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.False(File.Exists(System.IO.Path.Combine(output, ".pakassetstudio.lock")));
    }

    [Fact]
    public async Task ManagedDirectoryLinksAreRejectedBeforeToolsRun()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var external = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "External")).FullName;
        var externalFile = System.IO.Path.Combine(external, "keep.gltf");
        await File.WriteAllTextAsync(externalFile, "{}");
        var service = CreateService();
        await service.RunAsync([], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None);
        try
        {
            Directory.CreateSymbolicLink(System.IO.Path.Combine(output, "ExportedAssets"), external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            [], Options(game, output, convertToFbx: true),
            (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.True(File.Exists(externalFile));
    }

    [Fact]
    public async Task ExistingManagedOutputRequiresExplicitOverwrite()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var service = CreateService();
        await service.RunAsync([], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None);
        var cooked = Directory.CreateDirectory(System.IO.Path.Combine(output, "CookedAssets")).FullName;
        var oldFile = System.IO.Path.Combine(cooked, "old.bin");
        await File.WriteAllTextAsync(oldFile, "old");
        var entry = new PakEntry { Name = "base.pak", FullPath = "base.pak", IsValid = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            [entry], Options(game, output, extractPaks: true), (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public async Task SuccessfulOverwriteRemovesPreviousOutputBackup()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var service = CreateService();
        await service.RunAsync([], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None);
        var cooked = Directory.CreateDirectory(System.IO.Path.Combine(output, "CookedAssets")).FullName;
        await File.WriteAllTextAsync(System.IO.Path.Combine(cooked, "old.bin"), "old");
        var entry = new PakEntry { Name = "base.pak", FullPath = "base.pak", IsValid = true };

        await service.RunAsync(
            [entry], Options(game, output, extractPaks: true, overwrite: true),
            (_, _) => { }, (_, _) => { }, CancellationToken.None);

        Assert.Empty(Directory.EnumerateDirectories(output, ".PakAssetStudio-backup-*"));
        Assert.False(File.Exists(System.IO.Path.Combine(output, "CookedAssets", "old.bin")));
    }

    [Fact]
    public async Task CancellingSuccessfulBackupCleanupRetainsBackupAndCompletesOutput()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var service = CreateService();
        await service.RunAsync([], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None);
        var cooked = Directory.CreateDirectory(System.IO.Path.Combine(output, "CookedAssets")).FullName;
        await File.WriteAllTextAsync(System.IO.Path.Combine(cooked, "old.bin"), "old");
        var entry = new PakEntry { Name = "base.pak", FullPath = "base.pak", IsValid = true };
        using var cancellation = new CancellationTokenSource();
        var progressValues = new List<double>();

        await service.RunAsync(
            [entry], Options(game, output, extractPaks: true, overwrite: true),
            (_, _) => { },
            (value, _) =>
            {
                progressValues.Add(value);
                if (value == 99) cancellation.Cancel();
            },
            cancellation.Token);

        var backup = Assert.Single(Directory.EnumerateDirectories(
            output, ".PakAssetStudio-backup-CookedAssets-*"));
        Assert.True(File.Exists(System.IO.Path.Combine(backup, "old.bin")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(output, "CookedAssets")));
        Assert.Equal(100, progressValues[^1]);
    }

    [Fact]
    public async Task FailedOverwriteRetainsPreviousOutputBackup()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var setupService = CreateService();
        await setupService.RunAsync([], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None);
        var cooked = Directory.CreateDirectory(System.IO.Path.Combine(output, "CookedAssets")).FullName;
        await File.WriteAllTextAsync(System.IO.Path.Combine(cooked, "old.bin"), "old");
        var failedRunner = new FakeProcessRunner(new ProcessResult(9, "failed"));
        var failedService = new WorkflowService(failedRunner, new PakToolService(failedRunner));
        var entry = new PakEntry { Name = "base.pak", FullPath = "base.pak", IsValid = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => failedService.RunAsync(
            [entry], Options(game, output, extractPaks: true, overwrite: true),
            (_, _) => { }, (_, _) => { }, CancellationToken.None));

        var backup = Assert.Single(Directory.EnumerateDirectories(output, ".PakAssetStudio-backup-CookedAssets-*"));
        Assert.True(File.Exists(System.IO.Path.Combine(backup, "old.bin")));
    }

    [Fact]
    public async Task WorkflowRedactsAesKeyFromDiskAndUiLogs()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        const string secret = "0123456789abcdef-secret";
        var runner = new FakeProcessRunner(new ProcessResult(0, string.Empty), $"tool echoed {secret}");
        var service = new WorkflowService(runner, new PakToolService(runner));
        var uiLines = new List<string>();
        var options = Options(game, output, extractPaks: true, aesKeys: [secret]);
        var entry = new PakEntry { Name = "base.pak", FullPath = "base.pak", IsValid = true };

        await service.RunAsync([entry], options, (line, _) => uiLines.Add(line), (_, _) => { }, CancellationToken.None);

        var diskLog = await File.ReadAllTextAsync(System.IO.Path.Combine(output, "PakAssetStudio.log"));
        Assert.DoesNotContain(secret, diskLog);
        Assert.DoesNotContain(uiLines, line => line.Contains(secret, StringComparison.Ordinal));
        Assert.Contains("***", diskLog);
    }

    [Fact]
    public async Task ConcurrentWorkflowCannotUseTheSameOutputDirectory()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var blockingRunner = new BlockingProcessRunner();
        var firstService = new WorkflowService(blockingRunner, new PakToolService(blockingRunner));
        var entry = new PakEntry { Name = "base.pak", FullPath = "base.pak", IsValid = true };
        var first = firstService.RunAsync(
            [entry], Options(game, output, extractPaks: true),
            (_, _) => { }, (_, _) => { }, CancellationToken.None);

        try
        {
            await blockingRunner.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var secondService = CreateService();
            await Assert.ThrowsAsync<InvalidOperationException>(() => secondService.RunAsync(
                [], Options(game, output), (_, _) => { }, (_, _) => { }, CancellationToken.None));
        }
        finally
        {
            blockingRunner.Release();
            await first;
        }

        Assert.False(File.Exists(System.IO.Path.Combine(output, ".pakassetstudio.lock")));
    }

    [Fact]
    public async Task MarkerPreventsReusingOutputForAnotherGame()
    {
        using var root = new TemporaryDirectory();
        var game1 = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game1")).FullName;
        var game2 = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Game2")).FullName;
        var output = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Output")).FullName;
        var service = CreateService();

        await service.RunAsync([], Options(game1, output), (_, _) => { }, (_, _) => { }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            [], Options(game2, output), (_, _) => { }, (_, _) => { }, CancellationToken.None));
    }

    private static WorkflowService CreateService()
    {
        var runner = new FakeProcessRunner(new ProcessResult(0, string.Empty));
        return new WorkflowService(runner, new PakToolService(runner));
    }

    private static WorkflowOptions Options(
        string game,
        string output,
        bool extractPaks = false,
        IReadOnlyList<string>? aesKeys = null,
        bool overwrite = false,
        bool convertToFbx = false) => new()
        {
            GameDirectory = game,
            OutputDirectory = output,
            GameProfile = string.Empty,
            Workers = 1,
            ExtractPaks = extractPaks,
            AesKeys = aesKeys ?? [],
            Overwrite = overwrite,
            ConvertToFbx = convertToFbx
        };
}

public sealed class RepakIntegrationTests
{
    [Fact]
    public async Task BundledRepakCanPackScanAndApplyPatchFixture()
    {
        using var root = new TemporaryDirectory();
        var game = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "游戏 Game")).FullName;
        var runner = new ProcessRunner();
        var pakService = new PakToolService(runner);

        async Task PackAsync(string packageName, string content)
        {
            var source = Directory.CreateDirectory(System.IO.Path.Combine(game, packageName)).FullName;
            var nested = Directory.CreateDirectory(System.IO.Path.Combine(source, "Content")).FullName;
            await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "fixture.txt"), content);
            var packed = await runner.RunAsync(
                pakService.RepakPath,
                ["pack", source],
                game,
                null,
                CancellationToken.None);
            Assert.True(packed.ExitCode == 0, packed.Output);
        }

        await PackAsync("pakchunk0", "base-content");
        await PackAsync("pakchunk0_2_P", "patch-content");
        Assert.Equal(2, Directory.EnumerateFiles(game, "*.pak").Count());

        var entries = await pakService.ScanAsync(game, null, null, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.True(entry.CanAttemptExtraction));
        Assert.Equal(
            new[] { "pakchunk0.pak", "pakchunk0_2_P.pak" },
            PakToolService.GetExtractionOrder(entries).Select(entry => entry.Name));

        var output = System.IO.Path.Combine(root.Path, "Output");
        var workflow = new WorkflowService(runner, pakService);
        await workflow.RunAsync(entries, new WorkflowOptions
        {
            GameDirectory = game,
            OutputDirectory = output,
            GameProfile = string.Empty,
            ExtractPaks = true,
            Workers = 1
        }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        var extracted = Assert.Single(Directory.EnumerateFiles(
            System.IO.Path.Combine(output, "CookedAssets"), "fixture.txt", SearchOption.AllDirectories));
        Assert.Equal("patch-content", await File.ReadAllTextAsync(extracted));
    }
}

public sealed class OptionalPakIntegrationTests
{
    [Fact]
    public async Task ScanConfiguredRealPakDirectory()
    {
        var path = Environment.GetEnvironmentVariable("PAK_TEST_DIR");
        if (string.IsNullOrWhiteSpace(path)) return;

        Assert.True(Directory.Exists(path), $"PAK test directory does not exist: {path}");
        var service = new PakToolService(new ProcessRunner());
        var aesKey = Environment.GetEnvironmentVariable("PAK_TEST_AES_KEY");
        var aesKeys = string.IsNullOrWhiteSpace(aesKey) ? null : new[] { aesKey };
        var entries = await service.ScanAsync(path, aesKeys, null, CancellationToken.None);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => entry.CanAttemptExtraction);
    }
}

internal sealed class LaunchFailureProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        ProcessPriorityClass? priority = null,
        bool captureOutput = true) =>
        throw new ProcessLaunchException("launch failed");
}

internal sealed class BlockingProcessRunner : IProcessRunner
{
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public void Release() => _release.TrySetResult(true);

    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        ProcessPriorityClass? priority = null,
        bool captureOutput = true)
    {
        _started.TrySetResult(true);
        await _release.Task.WaitAsync(cancellationToken);
        return new ProcessResult(0, string.Empty);
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessResult> _results;
    private readonly string? _emittedLine;

    /// <summary>每次进程调用的记录（可执行文件 + 参数），供断言多密钥重试等行为。</summary>
    public List<(string Executable, List<string> Arguments)> Calls { get; } = [];

    public FakeProcessRunner(ProcessResult result, string? emittedLine = null)
        : this(new[] { result }, emittedLine) { }

    public FakeProcessRunner(IEnumerable<ProcessResult> results, string? emittedLine = null)
    {
        _results = new Queue<ProcessResult>(results);
        _emittedLine = emittedLine;
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        ProcessPriorityClass? priority = null,
        bool captureOutput = true)
    {
        Calls.Add((executable, arguments.ToList()));
        if (_emittedLine is not null) onLine?.Invoke(_emittedLine);
        if (_results.Count == 0)
            throw new InvalidOperationException("FakeProcessRunner result queue exhausted");
        var result = _results.Dequeue();
        return Task.FromResult(result);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = Directory.CreateTempSubdirectory("pakassetstudio-tests-").FullName;
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
