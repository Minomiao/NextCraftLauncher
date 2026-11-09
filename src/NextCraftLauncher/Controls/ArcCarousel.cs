using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 试验用的无容器弧形轮播：不画任何底色或边框，一次只显示一项；
/// 切换时相邻项沿圆弧滑出/滑入，并带轻微旋转与缩放。
/// 用滚轮切换，选中项通过双向绑定同步给视图模型。
/// </summary>
public class ArcCarousel : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ArcCarousel, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<ArcCarousel, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<ArcCarousel, IDataTemplate?>(nameof(ItemTemplate));

    private const double DurationSeconds = 0.32;
    private const double MaxOffset = 2.4;

    private readonly Grid _host = new();
    private readonly List<Entry> _entries = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _animationStartedAt;
    private INotifyCollectionChanged? _observed;
    private bool _rebuildQueued;

    public ArcCarousel()
    {
        ClipToBounds = true;
        Content = _host;

        // 没有背景的控件不参与命中测试，滚轮事件就永远到不了这里。
        Background = Brushes.Transparent;

        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    static ArcCarousel()
    {
        ItemsSourceProperty.Changed.AddClassHandler<ArcCarousel>((carousel, _) => carousel.Rebuild());
        ItemTemplateProperty.Changed.AddClassHandler<ArcCarousel>((carousel, _) => carousel.Rebuild());
        SelectedItemProperty.Changed.AddClassHandler<ArcCarousel>((carousel, _) => carousel.StartAnimation());
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    // ------------------------------------------------------------ 构建

    private void Rebuild()
    {
        _host.Children.Clear();
        _entries.Clear();

        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }

        if (ItemsSource is not null)
        {
            foreach (var item in ItemsSource)
            {
                var content = new ContentControl
                {
                    Content = item,
                    ContentTemplate = ItemTemplate,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                };

                var entry = new Entry(item, content);
                content.RenderTransform = entry.Group;
                content.RenderTransformOrigin = RelativePoint.Center;

                _host.Children.Add(content);
                _entries.Add(entry);
            }

            if (ItemsSource is INotifyCollectionChanged changed)
            {
                _observed = changed;
                changed.CollectionChanged += OnCollectionChanged;
            }
        }

        var index = Math.Max(0, SelectedIndex());
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            entry.Offset = OffsetOf(i, index);
            entry.StartOffset = entry.Offset;
            Apply(entry);
        }
    }

    /// <summary>环形距离：没有头尾，向前/向后都取最短一圈。</summary>
    private double OffsetOf(int index, int selected)
    {
        var count = _entries.Count;
        if (count == 0) return 0;

        var distance = (index - selected) % count;
        if (distance > count / 2.0) distance -= count;
        else if (distance < -count / 2.0) distance += count;

        return distance;
    }

    /// <summary>版本列表是一次性批量替换的，合并成一次重建，避免逐项重建。</summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    private int SelectedIndex()
    {
        if (SelectedItem is null) return -1;

        for (var i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i].Item, SelectedItem)) return i;
        }

        return -1;
    }

    // ------------------------------------------------------------ 动画

    private void StartAnimation()
    {
        var index = SelectedIndex();
        if (index < 0) return;

        _animationStartedAt = _clock.Elapsed.TotalSeconds;
        foreach (var entry in _entries) entry.StartOffset = entry.Offset;

        RequestFrame();
    }

    private void RequestFrame()
        => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => OnFrame());

    private void OnFrame()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnFrame);
            return;
        }

        var index = SelectedIndex();
        if (index < 0) return;

        var progress = Math.Clamp(
            (_clock.Elapsed.TotalSeconds - _animationStartedAt) / DurationSeconds, 0, 1);

        // CubicEaseOut：起步快、结尾稳。
        var eased = 1 - Math.Pow(1 - progress, 3);

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var target = OffsetOf(i, index);
            var delta = target - entry.StartOffset;

            // 跨越半圈跳变的那一项本来就在屏幕外，直接归位，免得从中间“飞”过去。
            entry.Offset = Math.Abs(delta) > 3
                ? target
                : entry.StartOffset + delta * eased;

            Apply(entry);
        }

        if (progress < 1) RequestFrame();
    }

    /// <summary>把偏移量换算成横向弧线上的位置、角度、缩放与透明度。</summary>
    private static void Apply(Entry entry)
    {
        var offset = Clamp(entry.Offset);
        var distance = Math.Abs(offset);

        entry.Translate.X = 340 * offset;
        entry.Translate.Y = 22 * offset * offset;
        // 两侧向外倾：右侧顺时针、左侧逆时针。
        entry.Rotate.Angle = offset * 4;

        var scale = Math.Max(0.45, 1 - distance * 0.4);
        entry.Scale.ScaleX = scale;
        entry.Scale.ScaleY = scale;

        // 相邻项保留一部分透明度在两侧露出来，再往外的新渐隐。
        entry.Host.Opacity = Math.Max(0, 1 - distance * 0.55);

        // 越靠近中心的越在上层，避免两侧的项盖住当前项。
        entry.Host.ZIndex = (int)Math.Round(100 - distance * 10);
    }

    private static double Clamp(double offset) => Math.Clamp(offset, -MaxOffset, MaxOffset);

    // ------------------------------------------------------------ 滚轮

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var index = SelectedIndex();
        if (index < 0) return;

        // 纵向滚轮与控制板的横向滑动都支持：向上/向右回退，向下/向左前进。
        var horizontal = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        var delta = horizontal ? e.Delta.X : e.Delta.Y;
        if (delta == 0) return;

        var previous = delta > 0;

        // 循环：越过最后一项回到第一项。
        var count = _entries.Count;
        var next = ((index + (previous ? -1 : 1)) % count + count) % count;
        if (next == index) return;

        SelectedItem = _entries[next].Item;
        e.Handled = true;
    }

    // ------------------------------------------------------------ 条目

    private sealed class Entry
    {
        public Entry(object? item, ContentControl host)
        {
            Item = item;
            Host = host;

            Group = new TransformGroup
            {
                Children = { Scale, Rotate, Translate },
            };
        }

        public object? Item { get; }

        public ContentControl Host { get; }

        public ScaleTransform Scale { get; } = new(1, 1);

        public RotateTransform Rotate { get; } = new(0);

        public TranslateTransform Translate { get; } = new(0, 0);

        public TransformGroup Group { get; }

        /// <summary>当前显示位置，以「距选中项的项数」为单位。</summary>
        public double Offset { get; set; }

        /// <summary>本次动画的起点。</summary>
        public double StartOffset { get; set; }
    }
}
