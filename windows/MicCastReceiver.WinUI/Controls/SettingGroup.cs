using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MicCastReceiver.WinUI.Controls;

/// <summary>
/// 一组设置项：卡片 + 标题。
///
/// **存在的意义**：把「语义上属于同一件事」的设置项框在一起。
/// 比如「手机」这一组包含「自动发现」和「或手动 IP」—— 两者都是回答
/// 「连哪台手机」，放在同一张卡片里才读得懂；原先它们是三行平铺的独立项，
/// 分组关系看不出来。
/// </summary>
public sealed partial class SettingGroup : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingGroup),
            new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty HeaderVisibilityProperty =
        DependencyProperty.Register(nameof(HeaderVisibility), typeof(Visibility), typeof(SettingGroup),
            new PropertyMetadata(Visibility.Collapsed));

    public SettingGroup()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>分组标题。留空则整条标题行收起。</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public Visibility HeaderVisibility
    {
        get => (Visibility)GetValue(HeaderVisibilityProperty);
        private set => SetValue(HeaderVisibilityProperty, value);
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingGroup group)
        {
            group.HeaderVisibility = string.IsNullOrWhiteSpace(e.NewValue as string)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }
}