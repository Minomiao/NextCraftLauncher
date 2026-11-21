using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NextCraftLauncher.Icons;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 界面图标：按名字从 <see cref="UiIcons"/> 取几何并绘制。
/// <para>
/// 缩放与居中都在这里算，不依赖 <c>Shape.Stretch</c>，因此同一个几何被多处使用时
/// 不会互相影响；墨水范围是零高度（例如最小化的横线）也能正确居中。
/// </para>
/// </summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<IconView, string?>(nameof(Icon));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconView, double>(nameof(IconSize), 12d);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<IconView, double>(nameof(StrokeThickness), 1.5d);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<IconView, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<IconView, bool>(nameof(Filled));

    static IconView()
    {
        AffectsRender<IconView>(IconProperty, IconSizeProperty, StrokeThicknessProperty,
            StrokeProperty, FilledProperty);
        AffectsMeasure<IconView>(IconSizeProperty);
    }

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>图标墨水区的目标边长（像素）。</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>线宽（像素）；实心图标不使用。</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>描边色；实心图标同时用它填充。</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>强制实心（SVG 根节点写了 fill 时会自动为实心）。</summary>
    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Max(0, IconSize);
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var icon = UiIcons.For(Icon);
        if (icon is null) return;

        var side = Math.Min(IconSize, Math.Min(Bounds.Width, Bounds.Height));
        if (side <= 0) return;

        var ink = icon.InkBounds;
        var span = Math.Max(ink.Width, ink.Height);
        if (span <= 0) span = Math.Max(icon.ViewBox.Width, icon.ViewBox.Height);
        if (span <= 0) return;

        // 描边会被渲染在墨水范围之外，先留出半个线宽，避免贴着边界被切掉。
        var inset = Math.Min(side / 4, Math.Max(0, StrokeThickness) * 0.5);
        var scale = Math.Max(0.0001, (side - inset * 2) / span);

        var offsetX = (Bounds.Width - ink.Width * scale) / 2 - ink.X * scale;
        var offsetY = (Bounds.Height - ink.Height * scale) / 2 - ink.Y * scale;

        var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);

        var filled = Filled || icon.Filled;
        // 线宽按缩放反算，保证最终落在屏幕上的厚度就是 StrokeThickness 像素。
        var pen = filled
            ? null
            : new Pen(Stroke, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushTransform(matrix))
        {
            context.DrawGeometry(filled ? Stroke : null, pen, icon.Geometry);
        }
    }
}
