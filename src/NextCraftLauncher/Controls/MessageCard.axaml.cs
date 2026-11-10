using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace NextCraftLauncher.Controls;

/// <summary>卡片类型；每种类型对应一套预设（标题颜色、建议文案、附带按钮）。</summary>
public enum MessageKind
{
    /// <summary>普通通知：中性标题色，只显示「确定」。</summary>
    Notice,

    /// <summary>警告：琥珀色标题，只显示「确定」。</summary>
    Warning,

    /// <summary>错误：红色标题，附带「打开 log 文件」与「导出错误报告」。</summary>
    Error,
}

/// <summary>
/// 通用提示卡片：遮罩 + 卡片，可用于通知、警告或错误。
/// 只负责展示与按钮点击，具体动作交给宿主（<see cref="CloseRequested"/> 等事件）。
/// </summary>
public partial class MessageCard : UserControl
{
    /// <summary>错误预设下的建议文案。</summary>
    public const string DefaultErrorAdvice =
        "如果要寻求帮助，请向他人发送导出的错误报告文件，而不是发送这个窗口的截图。";

    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D13438"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#C77B00"));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<MessageCard, bool>(nameof(IsOpen));

    public static readonly StyledProperty<MessageKind> KindProperty =
        AvaloniaProperty.Register<MessageCard, MessageKind>(nameof(Kind));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<MessageCard, string?>(nameof(Title), string.Empty);

    /// <summary>正文：说明发生了什么。为空时整行隐藏。</summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<MessageCard, string?>(nameof(Message), string.Empty);

    /// <summary>
    /// 底部建议文案：<c>null</c> 时按 <see cref="Kind"/> 取预设，
    /// 设为空字符串可以隐藏这一行。
    /// </summary>
    public static readonly StyledProperty<string?> AdviceProperty =
        AvaloniaProperty.Register<MessageCard, string?>(nameof(Advice));

    /// <summary>「确定」按钮的文字。</summary>
    public static readonly StyledProperty<string> CloseTextProperty =
        AvaloniaProperty.Register<MessageCard, string>(nameof(CloseText), "确定");

    public MessageCard()
    {
        InitializeComponent();
        Apply();
    }

    /// <summary>点「确定」时触发。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>点「打开 log 文件」时触发；只有错误预设会显示该按钮。</summary>
    public event EventHandler? OpenLogRequested;

    /// <summary>点「导出错误报告」时触发；只有错误预设会显示该按钮。</summary>
    public event EventHandler? ExportRequested;

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public MessageKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string? Advice
    {
        get => GetValue(AdviceProperty);
        set => SetValue(AdviceProperty, value);
    }

    public string CloseText
    {
        get => GetValue(CloseTextProperty);
        set => SetValue(CloseTextProperty, value);
    }

    /// <summary>一次性填好内容并打开卡片。</summary>
    public void Show(MessageKind kind, string title, string? message = null, string? advice = null)
    {
        Kind = kind;
        Title = title;
        Message = message;
        Advice = advice;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsOpenProperty ||
            change.Property == KindProperty ||
            change.Property == TitleProperty ||
            change.Property == MessageProperty ||
            change.Property == AdviceProperty ||
            change.Property == CloseTextProperty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        Mask.IsVisible = IsOpen;

        TitleText.Text = Title ?? string.Empty;

        MessageText.Text = Message ?? string.Empty;
        MessageText.IsVisible = !string.IsNullOrWhiteSpace(Message);

        switch (Kind)
        {
            case MessageKind.Error:
                TitleText.Foreground = ErrorBrush;
                break;

            case MessageKind.Warning:
                TitleText.Foreground = WarningBrush;
                break;

            default:
                // 中性：交还给主题的默认前景色。
                TitleText.ClearValue(TextBlock.ForegroundProperty);
                break;
        }

        var advice = Advice ?? (Kind == MessageKind.Error ? DefaultErrorAdvice : string.Empty);
        AdviceText.Text = advice;
        AdviceText.IsVisible = !string.IsNullOrWhiteSpace(advice);

        // 只有错误预设附带排查入口。
        var detailed = Kind == MessageKind.Error;
        LogButton.IsVisible = detailed;
        ExportButton.IsVisible = detailed;

        CloseButton.Content = CloseText;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenLogClick(object? sender, RoutedEventArgs e)
        => OpenLogRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportClick(object? sender, RoutedEventArgs e)
        => ExportRequested?.Invoke(this, EventArgs.Empty);
}
