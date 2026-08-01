using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace PakAssetStudio.Services;

/// <summary>
/// Avalonia 版多语言标记扩展：<c>Text="{l:Loc Workspace_Title}"</c>。
/// 生成到 <see cref="LocalizationService"/> 索引器的绑定，语言切换时自动刷新。
/// 与 WPF 版 LocExtension 语义一致（该文件仅存在于 Avalonia 项目，WPF 版使用其自有实现）。
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key) => Key = key;

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
    }
}
