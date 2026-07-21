using System.Windows.Data;
using System.Windows.Markup;

namespace PakAssetStudio.Services;

/// <summary>
/// XAML 多语言标记扩展：<c>Text="{l:Loc Workspace_Title}"</c>。
/// 生成到 <see cref="LocalizationService"/> 索引器的绑定，语言切换时自动刷新。
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key) => Key = key;

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
