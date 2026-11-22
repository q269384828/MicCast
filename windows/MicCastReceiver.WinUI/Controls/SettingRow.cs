using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MicCastReceiver.WinUI.Controls;

/// <summary>
/// 一行设置项：图标 + 标题 + 右侧任意控件。
///
/// **存在的意义**：垂直居中和列宽在模板里保证一次，所有设置行都一致。
/// 之前每行都手写一遍 Grid + ColumnDefinitions + Margin，结果就是
/// 「有的行图标没对齐、有的行文字偏上」这类问题反复出现 —— 每次复制都差一点。
/// 现在使用点只声明语义（Glyph / Label / Content），布局由模板负责。
/// </summary>
public sealed partial class SettingRow : ContentControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow),
            new PropertyMetadata("\uE946"));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingRow),
            new PropertyMetadata(string.Empty));

    public SettingRow()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>Segoe Fluent Icons 字形码。</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>左侧标题文字。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}