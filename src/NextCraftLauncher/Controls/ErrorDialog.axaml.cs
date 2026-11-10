using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 可复用的置顶错误弹窗：遮罩 + 卡片，包含标题、推测原因、固定建议和三个按钮。
/// 只负责展示与按钮点击，具体动作交给宿主（<see cref="CloseRequested"/> 等事件）。
/// </summary>
public partial class ErrorDialog : UserControl
{
    /// <summary>默认的建议文案；把 <see cref="Advice"/> 设成空字符串即可隐藏这一行。</summary>
    public const string DefaultAdviceText =
        "如果要寻求帮助，请向他人发送导出的错误报告文件，而不是发送这个窗口的截图。";

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ErrorDialog, bool>(nameof(IsOpen));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ErrorDialog, string?>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<ErrorDialog, string?>(nameof(Hint), string.Empty);

    public static readonly StyledProperty<string?> AdviceProperty =
        AvaloniaProperty.Register<ErrorDialog, string?>(nameof(Advice), DefaultAdviceText);

    public ErrorDialog()
    {
        InitializeComponent();
        Apply();
    }

    /// <summary>点「确定」时触发。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>点「打开 log 文件」时触发。</summary>
    public event EventHandler? OpenLogRequested;

    /// <summary>点「导出错误报告」时触发。</summary>
    public event EventHandler? ExportRequested;

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>推测原因；为空时整行隐藏。</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>底部建议文案；为空时整行隐藏。</summary>
    public string? Advice
    {
        get => GetValue(AdviceProperty);
        set => SetValue(AdviceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsOpenProperty ||
            change.Property == TitleProperty ||
            change.Property == HintProperty ||
            change.Property == AdviceProperty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        Mask.IsVisible = IsOpen;

        TitleText.Text = Title ?? string.Empty;

        HintText.Text = Hint ?? string.Empty;
        HintText.IsVisible = !string.IsNullOrWhiteSpace(Hint);

        AdviceText.Text = Advice ?? string.Empty;
        AdviceText.IsVisible = !string.IsNullOrWhiteSpace(Advice);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenLogClick(object? sender, RoutedEventArgs e)
        => OpenLogRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportClick(object? sender, RoutedEventArgs e)
        => ExportRequested?.Invoke(this, EventArgs.Empty);
}
