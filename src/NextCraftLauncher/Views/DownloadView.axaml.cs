using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCraftLauncher.ViewModels;

namespace NextCraftLauncher.Views;

public partial class DownloadView : UserControl
{
    private MainViewModel? _viewModel;

    public DownloadView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ModSaveFilePicker = PickModSavePathAsync;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel = viewModel;
        }
        else
        {
            _viewModel = null;
        }
    }

    /// <summary>翻页后把列表滚回顶部，否则会停在新一页的末尾；并驱动进入页面时的进场动画。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ModPage))
        {
            var scroll = ModResultsScroll;
            if (scroll is not null) scroll.Offset = new Vector(scroll.Offset.X, 0);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsDownloadPageSelected))
        {
            if (_viewModel?.IsDownloadPageSelected == true)
            {
                AnimateSideBar();
                // 右侧列表可能已经生成，也要等这一轮布局完成后再取行。
                Dispatcher.UIThread.Post(AnimateContent, DispatcherPriority.Background);
            }
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.CurrentDownloadSection))
        {
            // 等这一轮布局把新栏目的列表行生成出来，再取条目播放。
            Dispatcher.UIThread.Post(AnimateContent, DispatcherPriority.Background);
        }
    }

    /// <summary>从顶部导航进入下载页时，左侧条目一条接一条出现。</summary>
    private void AnimateSideBar()
    {
        var items = new List<Control>();

        if (SectionSideBar.IsVisible)
            items.AddRange(SectionSideBar.Children.OfType<Control>());
        else
            items.AddRange(RelatedSideBarRows());

        items.AddRange(TaskSideBar.Children.OfType<Control>());

        EnterAnimation.Play(items, -18, 55, 160, 220);
    }

    /// <summary>次级页面时左侧是相关条目列表。</summary>
    private IEnumerable<Control> RelatedSideBarRows()
        => RelatedItemsPanel.Children
            .OfType<ItemsControl>()
            .Where(items => items.IsVisible)
            .SelectMany(EnterAnimation.RealizedRows);

    /// <summary>切换左侧栏目（或进入页面）时，右侧当前列表的条目更轻地逐条出现。</summary>
    private void AnimateContent()
    {
        if (_viewModel is not { } viewModel) return;

        ItemsControl? target = viewModel.CurrentDownloadSection switch
        {
            DownloadSection.Versions => viewModel.IsRemoteVersionDetailOpen
                ? LoaderVersionsList
                : RemoteVersionsList,
            DownloadSection.Mods or DownloadSection.ResourcePacks => viewModel.IsModDetailOpen
                ? ModVersionList
                : ModResultsItems,
            DownloadSection.Java => JavaRuntimeList,
            DownloadSection.Running => DownloadTasksItems,
            _ => null,
        };

        if (target is null) return;

        var rows = EnterAnimation.RealizedRows(target).ToList();

        // 数据还没到（列表为空）时，让列表整体进场，避免右侧看起来完全没动画。
        if (rows.Count == 0) rows.Add(target);

        EnterAnimation.Play(rows, 10, 30, 140, 180);
    }

    /// <summary>侧栏结果条目从左侧依次滑入；按条目顺序错开一点时间。</summary>
    private void OnRelatedItemAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control) return;

        var index = 0;
        if (control.FindAncestorOfType<ItemsControl>() is { ItemsSource: IList items } &&
            control.DataContext is { } item)
        {
            index = Math.Max(0, items.IndexOf(item));
        }

        var delay = TimeSpan.FromMilliseconds(index * 55);

        control.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(160),
                Delay = delay,
            },
            new ThicknessTransition
            {
                Property = MarginProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Delay = delay,
            },
        };

        control.Opacity = 0;
        control.Margin = new Thickness(-18, 0, 0, 0);

        Dispatcher.UIThread.Post(
            () =>
            {
                control.Opacity = 1;
                control.Margin = new Thickness(0);
            },
            DispatcherPriority.Background);
    }

    private void OnModSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainViewModel viewModel) return;
        if (!viewModel.SearchModsCommand.CanExecute(null)) return;

        viewModel.SearchModsCommand.Execute(null);
        e.Handled = true;
    }

    private async Task<string?> PickModSavePathAsync(string fileName, string defaultDirectory)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        IStorageFolder? startLocation = null;
        try
        {
            Directory.CreateDirectory(defaultDirectory);
            startLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(defaultDirectory);
        }
        catch (Exception)
        {
            // 目录不可用时忽略，让对话框自己决定起始位置。
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存 Mod",
            SuggestedFileName = fileName,
            DefaultExtension = Path.GetExtension(fileName).TrimStart('.'),
            SuggestedStartLocation = startLocation,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Mod 文件")
                {
                    Patterns = new[] { "*.jar", "*.litemod" },
                },
            },
        });

        return file?.Path.LocalPath;
    }

    private async void OnBrowseJavaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 java.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java 可执行文件")
                {
                    Patterns = new[] { "java.exe", "java" },
                },
            },
        });

        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            await viewModel.SetPreferredJavaAsync(path);
    }
}
