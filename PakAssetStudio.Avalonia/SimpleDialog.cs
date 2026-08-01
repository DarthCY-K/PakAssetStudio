using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PakAssetStudio.Services;

/// <summary>
/// WPF MessageBox 的 Avalonia 替代：模态对话框（居中于 owner），
/// 按钮文案走语言文件（Dialog_Ok / Dialog_Cancel / Dialog_Yes / Dialog_No）。
/// Avalonia 的 ShowDialog 是异步的，所有调用点必须 await。
/// </summary>
public static class SimpleDialog
{
    /// <summary>信息提示，单“确定”按钮。</summary>
    public static Task InfoAsync(Window owner, string message, string title) =>
        ShowAsync(owner, message, title, severity: DialogSeverity.Information, okOnly: true);

    /// <summary>警告提示，单“确定”按钮。</summary>
    public static Task WarnAsync(Window owner, string message, string title) =>
        ShowAsync(owner, message, title, severity: DialogSeverity.Warning, okOnly: true);

    /// <summary>错误提示，单“确定”按钮。</summary>
    public static Task ErrorAsync(Window owner, string message, string title) =>
        ShowAsync(owner, message, title, severity: DialogSeverity.Error, okOnly: true);

    /// <summary>确认询问，是/否 两个按钮，返回用户是否选择“是”。</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string message, string title) =>
        await ShowAsync(owner, message, title, severity: DialogSeverity.Warning, okOnly: false) == true;

    private static async Task<bool?> ShowAsync(
        Window owner, string message, string title, DialogSeverity severity, bool okOnly)
    {
        var dialog = new DialogWindow(title, message, severity, okOnly);
        return await dialog.ShowDialog<bool?>(owner);
    }

    private enum DialogSeverity
    {
        Information,
        Warning,
        Error
    }

    private sealed class DialogWindow : Window
    {
        public DialogWindow(string title, string message, DialogSeverity severity, bool okOnly)
        {
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;

            var (icon, iconColor) = severity switch
            {
                DialogSeverity.Warning => ("⚠️", Color.FromRgb(0xE3, 0xB3, 0x41)),
                DialogSeverity.Error => ("❌", Color.FromRgb(0xE0, 0x6C, 0x5B)),
                _ => ("ℹ️", Color.FromRgb(0x4C, 0xC2, 0xFF))
            };

            var messageText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                VerticalAlignment = VerticalAlignment.Center
            };

            var okButton = new Button
            {
                Content = okOnly
                    ? LocalizationService.Text("Dialog_Ok")
                    : LocalizationService.Text("Dialog_Yes"),
                MinWidth = 84
            };
            okButton.Click += (_, _) => Close(okOnly);

            var cancelButton = new Button
            {
                Content = LocalizationService.Text("Dialog_No"),
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0)
            };
            cancelButton.Click += (_, _) => Close(false);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            if (!okOnly) buttonRow.Children.Add(cancelButton);
            buttonRow.Children.Add(okButton);

            var contentGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                }
            };

            var iconText = new TextBlock
            {
                Text = icon,
                FontSize = 26,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 14, 0),
                Foreground = new SolidColorBrush(iconColor)
            };
            Grid.SetColumn(iconText, 0);

            var textColumn = new StackPanel
            {
                Children = { messageText, buttonRow }
            };
            Grid.SetColumn(textColumn, 1);

            contentGrid.Children.Add(iconText);
            contentGrid.Children.Add(textColumn);

            Content = new Border
            {
                Padding = new Thickness(22, 18),
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                Child = contentGrid
            };
        }
    }
}
