using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace NextCraftLauncher.Views;

/// <summary>
/// 栏目进场动画：条目从左侧滑入并淡入，按顺序错开。
/// 用 RenderTransform 而不是 Margin，避免改变布局和已有的外边距。
/// </summary>
internal static class EnterAnimation
{
    /// <summary>对一组条目播放进场动画。</summary>
    /// <param name="items">按显示顺序排列的条目。</param>
    /// <param name="offset">起始时向左偏移的像素。</param>
    /// <param name="staggerMs">相邻条目之间的延迟。</param>
    /// <param name="fadeMs">淡入时长。</param>
    /// <param name="slideMs">位移时长。</param>
    public static void Play(IEnumerable<Control> items, double offset, int staggerMs, int fadeMs, int slideMs)
    {
        var index = 0;

        foreach (var control in items)
        {
            var delay = TimeSpan.FromMilliseconds(index++ * staggerMs);
            var slide = new TranslateTransform(-offset, 0);

            control.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(fadeMs),
                    Delay = delay,
                },
            };

            slide.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = TimeSpan.FromMilliseconds(slideMs),
                    Delay = delay,
                },
            };

            control.RenderTransform = slide;
            control.Opacity = 0;

            // 等这一轮渲染把初始值记下来，再设终点，过渡才会真正播放。
            Dispatcher.UIThread.Post(
                () =>
                {
                    control.Opacity = 1;
                    slide.X = 0;
                },
                DispatcherPriority.Background);
        }
    }

    /// <summary>列表当前已经生成的条目（虚拟化滚动时只有可见部分）。</summary>
    public static IEnumerable<Control> RealizedRows(ItemsControl items)
        => items.GetRealizedContainers().OfType<Control>();

    /// <summary>设置页的一节：标题、分组眉标、每一行、分割线依次作为一条。</summary>
    public static IEnumerable<Control> SectionRows(StackPanel section)
    {
        foreach (var child in section.Children)
        {
            if (child is StackPanel group)
            {
                foreach (var row in group.Children.OfType<Control>())
                    yield return row;
            }
            else if (child is Control control)
            {
                yield return control;
            }
        }
    }
}
