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
/// 试验用的无容器弧形轮播：不画任何底色或边框，一次只显示一项；
/// 滚轮给出冲量，减速靠阻尼，会依次经过中间的每一页，最后吸附停在某一页。
/// 选中项通过双向绑定同步给视图模型。
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

    /// <summary>阻尼（1/s）：越大停得越快，滑过的页数越少。</summary>
    private const double Friction = 7.5;

    /// <summary>每格滚轮给出的速度（页/秒）。等于阻尼时，一格刚好滑一页。</summary>
    private const double Impulse = 7.5;

    /// <summary>反向滚动时抵消力的倍数：大于 1 收得更快。</summary>
    private const double CancelBoost = 1.8;

    /// <summary>速度上限（页/秒），避免连续滚轮把速度叠到失控。</summary>
    private const double MaxSpeed = 40;

    /// <summary>速度低于此值开始吸附到最近的整页。</summary>
    private const double SnapSpeed = 1.6;

    /// <summary>吸附刚度：把当前页拉正。</summary>
    private const double SnapStiffness = 45;

    private const double MaxOffset = 2.4;

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

    /// <summary>累计速度（页/秒）：滚轮不断叠加冲量，阻尼让它衰减。</summary>
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

        _page = Math.Max(0, SelectedIndex());
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

    /// <summary>外部（启动恢复、其它入口）改了选中项时，直接把页面位置对齐过去。</summary>
    private void OnSelectedItemChanged()
    {
        if (_syncingSelection) return;

        var index = SelectedIndex();
        if (index < 0) return;

        // 自己滚动过程中同步过来的选中项不用处理，避免打断正在进行的滑动。
        if (_animating && Math.Abs(index - _page) < 1.5) return;

        _page = index;
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

        // 1. 阻尼：速度按指数衰减，这就是“阻力”，滚轮给的总冲量决定能滑多远。
        _velocity *= Math.Exp(-Friction * delta);

        // 2. 速度不快时向最近的整页吸附，保证最后停在某一页而不是停在两页之间。
        var nearest = Math.Round(_page);
        if (Math.Abs(_velocity) < SnapSpeed)
            _velocity += (nearest - _page) * SnapStiffness * delta;

        // 3. 推进位置；归一化到 [0, count) 以支持无头尾循环。
        _page = Normalize(_page + _velocity * delta, count);

        ApplyAll();
        SyncSelection();

        // 4. 停稳判定：速度足够小且已经对齐到整页。
        var settled = Math.Abs(_velocity) < 0.05 && Math.Abs(Math.Round(_page) - _page) < 0.01;
        if (!settled)
        {
            if (!RequestFrame()) _animating = false;
            return;
        }

        _page = Normalize(Math.Round(_page), count);
        _velocity = 0;
        ApplyAll();
        SyncSelection();
        _animating = false;
    }

    /// <summary>把位置换算成每一项的偏移；跨过半圈时取最短方向。</summary>
    private void ApplyAll()
    {
        var count = _entries.Count;
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            var offset = i - _page;
            if (offset > count / 2.0) offset -= count;
            else if (offset < -count / 2.0) offset += count;

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
    }

    // ------------------------------------------------------------ 滚轮

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_entries.Count == 0) return;

        // 纵向滚轮与控制板的横向滑动都支持：向上/向右回退，向下/向左前进。
        var horizontal = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        var delta = horizontal ? e.Delta.X : e.Delta.Y;
        if (delta == 0) return;

        ApplyImpulse(delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    /// <summary>把滚轮格数换成冲量；反向时只抵消已有的力，剩下的惯性继续滑。</summary>
    private void ApplyImpulse(int notches)
    {
        var opposing = _velocity * notches < 0;

        // 反向时抵消力放大，停得更快；同向仍按原冲量叠加。
        var magnitude = Math.Abs(notches) * Impulse * (opposing ? CancelBoost : 1);
        var impulse = Math.Sign(notches) * magnitude;
        var next = _velocity + impulse;

        // 反向滚动只用来抵消：最多减到 0，不会因为这一格就翻到反方向；
        // 还没被抵消掉的惯性保留下来，继续按阻尼减速，而不是硬停。
        if (opposing && _velocity * next < 0) next = 0;

        _velocity = Math.Clamp(next, -MaxSpeed, MaxSpeed);
        EnsureFrames();
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

        /// <summary>当前显示位置，以「距当前页的页数」为单位。</summary>
        public double Offset { get; set; }
    }
}
