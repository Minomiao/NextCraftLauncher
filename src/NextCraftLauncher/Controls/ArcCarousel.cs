using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 试验用的无容器弧形轮播：不画任何底色或边框，一次只显示一项。
/// 滚轮每格移动一格目标页，页面用弹簧追目标，因此每次滚动都一定生效，
/// 连续滚动会依次经过中间的每一页，最后稳稳停在一页上。
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

    /// <summary>弹簧刚度：越大越跟手，越小越“重”。</summary>
    private const double Stiffness = 60;

    /// <summary>阻尼（1/s）：越小回弹越明显，越大越干脆。</summary>
    private const double Friction = 9;

    private const double MaxOffset = 2.4;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly Grid _host = new();
    private readonly List<Entry> _entries = new();

    private INotifyCollectionChanged? _observed;
    private bool _rebuildQueued;
    private bool _animating;
    private bool _syncingSelection;
    private double _lastFrameSeconds = -1;
    private long _lastFrameTick;

    /// <summary>当前显示位置，单位是「页」，可以是小数。</summary>
    private double _page;

    /// <summary>目标页，滚轮每格加减 1；弹簧把 <see cref="_page"/> 拉向它。</summary>
    private double _target;

    /// <summary>弹簧速度（页/秒）。</summary>
    private double _velocity;

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
        SelectedItemProperty.Changed.AddClassHandler<ArcCarousel>((carousel, _) => carousel.OnSelectedItemChanged());
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

                // 透明底的 Border 包一层，这样条目本身可被点到（内容控件默认不参与命中测试）。
                var host = new Border
                {
                    Child = content,
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var entry = new Entry(item, host);
                host.RenderTransform = entry.Group;
                host.RenderTransformOrigin = RelativePoint.Center;
                host.PointerPressed += (_, args) => OnItemPressed(entry, args);

                _host.Children.Add(host);
                _entries.Add(entry);
            }

            if (ItemsSource is INotifyCollectionChanged changed)
            {
                _observed = changed;
                changed.CollectionChanged += OnCollectionChanged;
            }
        }

        _page = Math.Max(0, SelectedIndex());
        _target = _page;
        _velocity = 0;
        ApplyAll();
        _animating = false;
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

    /// <summary>外部（启动恢复、其它入口）改了选中项时，直接把位置和目标都对齐过去。</summary>
    private void OnSelectedItemChanged()
    {
        if (_syncingSelection) return;

        var index = SelectedIndex();
        if (index < 0) return;

        // 自己滚动过程中同步过来的选中项不用处理，避免打断正在进行的滑动。
        if (_animating && Math.Abs(index - _page) < 1.5) return;

        _page = index;
        _target = index;
        _velocity = 0;
        ApplyAll();
    }

    // ------------------------------------------------------------ 动画

    private void EnsureFrames()
    {
        // 帧回调可能因为窗口被遮挡等原因中断：超过 0.4 秒没有任何帧就重启循环，
        // 否则 _animating 会一直为 true，后续滚动全部被挡住（表现为卡住）。
        if (_animating && Environment.TickCount64 - _lastFrameTick > 400)
            _animating = false;

        if (_animating) return;

        if (!RequestFrame()) return;

        _animating = true;
        _lastFrameSeconds = -1;
    }

    private bool RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return false;

        topLevel.RequestAnimationFrame(OnFrame);
        return true;
    }

    private void OnFrame(TimeSpan timestamp)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnFrame(timestamp));
            return;
        }

        _lastFrameTick = Environment.TickCount64;

        var now = timestamp.TotalSeconds;
        var delta = _lastFrameSeconds < 0
            ? 1.0 / 60
            : Math.Clamp(now - _lastFrameSeconds, 0, 0.05);
        _lastFrameSeconds = now;

        var count = _entries.Count;
        if (count == 0)
        {
            _animating = false;
            return;
        }

        // 与目标页的环形最短距离，避免跨过首尾时绕一大圈。
        var remaining = ShortestDelta(_target - _page, count);

        // 1. 弹簧：速度向目标页加速，阻尼让它收敛，略带一点过冲。
        _velocity += remaining * Stiffness * delta;
        _velocity *= Math.Exp(-Friction * delta);

        // 2. 推进位置。
        _page = Normalize(_page + _velocity * delta, count);
        _target = Normalize(_target, count);

        ApplyAll();
        SyncSelection();

        // 3. 停稳判定（用推进后的位置重新计算剩余距离）。
        var left = ShortestDelta(_target - _page, count);
        var settled = Math.Abs(left) < 0.004 && Math.Abs(_velocity) < 0.03;
        if (!settled)
        {
            if (!RequestFrame()) _animating = false;
            return;
        }

        _page = Normalize(_target, count);
        _velocity = 0;
        ApplyAll();
        SyncSelection();
        _animating = false;
    }

    /// <summary>把差值折算到 (-count/2, count/2]，用于环形最短路径。</summary>
    private static double ShortestDelta(double value, int count)
    {
        if (count == 0) return 0;

        value %= count;
        if (value > count / 2.0) value -= count;
        else if (value < -count / 2.0) value += count;

        return value;
    }

    /// <summary>把位置换算成每一项的偏移；跨过半圈时取最短方向。</summary>
    private void ApplyAll()
    {
        var count = _entries.Count;
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            var offset = ShortestDelta(i - _page, count);

            var entry = _entries[i];
            entry.Offset = offset;
            Apply(entry);
        }
    }

    /// <summary>经过哪一页就把选中项切到哪一页，这样滚过的每一页都会真正显示出来。</summary>
    private void SyncSelection()
    {
        var count = _entries.Count;
        if (count == 0) return;

        var page = (int)Math.Round(_page);
        page = ((page % count) + count) % count;

        var current = SelectedIndex();
        if (page == current) return;

        _syncingSelection = true;
        try
        {
            SelectedItem = _entries[page].Item;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private static double Normalize(double page, int count)
    {
        if (count == 0) return 0;

        page %= count;
        return page < 0 ? page + count : page;
    }

    /// <summary>把偏移量换算成横向弧线上的位置、角度、缩放与透明度。</summary>
    private static void Apply(Entry entry)
    {
        var offset = Math.Clamp(entry.Offset, -MaxOffset, MaxOffset);
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

        // 两侧的项可以点击翻页，给个手型光标。
        var clickable = distance > 0.5;
        if (entry.Clickable != clickable)
        {
            entry.Clickable = clickable;
            entry.Host.Cursor = clickable ? HandCursor : null;
        }
    }

    /// <summary>点两侧的项切到那一页；点中间项不做事。</summary>
    private void OnItemPressed(Entry entry, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(entry.Host).Properties.IsLeftButtonPressed) return;
        if (Math.Abs(entry.Offset) < 0.5) return;

        ApplyStep(entry.Offset < 0 ? -1 : 1);
        e.Handled = true;
    }

    // ------------------------------------------------------------ 滚轮

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_entries.Count == 0) return;

        // 纵向滚轮与控制板的横向滑动都支持：向上/向右回退，向下/向左前进。
        var horizontal = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        var delta = horizontal ? e.Delta.X : e.Delta.Y;
        if (delta == 0) return;

        ApplyStep(delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    /// <summary>移动一格目标页。反向时先吃掉当前惯性，避免还没停下就多翻一页。</summary>
    private void ApplyStep(int step)
    {
        if (_velocity * step < 0) _velocity = 0;

        _target += step;
        EnsureFrames();
    }

    // ------------------------------------------------------------ 条目

    private sealed class Entry
    {
        public Entry(object? item, Border host)
        {
            Item = item;
            Host = host;

            Group = new TransformGroup
            {
                Children = { Scale, Rotate, Translate },
            };
        }

        public object? Item { get; }

        public Border Host { get; }

        public bool Clickable { get; set; }

        public ScaleTransform Scale { get; } = new(1, 1);

        public RotateTransform Rotate { get; } = new(0);

        public TranslateTransform Translate { get; } = new(0, 0);

        public TransformGroup Group { get; }

        /// <summary>当前显示位置，以「距当前页的页数」为单位。</summary>
        public double Offset { get; set; }
    }
}
