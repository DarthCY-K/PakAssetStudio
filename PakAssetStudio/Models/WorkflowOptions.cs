namespace PakAssetStudio.Models;

public sealed class WorkflowOptions
{
    public required string GameDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string GameProfile { get; init; }
    public string? AesKey { get; init; }
    public bool ExtractPaks { get; init; }
    public bool ExportModels { get; init; }
    public bool ExportTextures { get; init; }
    public bool ConvertToFbx { get; init; }
    public bool KeepGltf { get; init; }
    public bool Overwrite { get; init; }
    public int Workers { get; init; }
    public bool LowResource { get; init; }
}
