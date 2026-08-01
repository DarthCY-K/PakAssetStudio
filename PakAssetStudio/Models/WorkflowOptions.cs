namespace PakAssetStudio.Models;

public sealed class WorkflowOptions
{
    public required string GameDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string GameProfile { get; init; }
    /// <summary>全部 AES 密钥（多 KeyGuid 游戏可填多个）；空列表表示无密钥。</summary>
    public IReadOnlyList<string> AesKeys { get; init; } = [];
    public bool ExtractPaks { get; init; }
    public bool ExportModels { get; init; }
    public bool ExportTextures { get; init; }
    public bool ExportAudio { get; init; }
    public bool ExportAnimations { get; init; }
    public bool ConvertToFbx { get; init; }
    public bool KeepGltf { get; init; }
    public bool MergeModels { get; init; }
    public bool DeleteCooked { get; init; }
    public bool Overwrite { get; init; }
    public int Workers { get; init; }
    public bool LowResource { get; init; }
}
