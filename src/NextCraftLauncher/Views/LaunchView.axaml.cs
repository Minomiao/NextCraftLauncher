using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NextCraftLauncher.ViewModels;

namespace NextCraftLauncher.Views;

public partial class LaunchView : UserControl
{
    public LaunchView()
    {
        InitializeComponent();
    }

    /// <summary>在版本列表里点选一个版本后直接回到启动页，轮播会从上一个版本滚过去。</summary>
    private void OnVersionSelectedByPointer(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.Source is not Visual source) return;

        // 行内的「设置 / 删除」自己处理点击，不跳回启动页。
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        // 点在列表空白处也不跳。
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;

        if (DataContext is MainViewModel viewModel)
            viewModel.ShowStartSectionCommand.Execute(null);
    }

    private async void OnCopyLoginCodeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(viewModel.LoginUserCode)) return;

        await clipboard.SetTextAsync(viewModel.LoginUserCode);
    }

    private async void OnOpenLoginLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null) return;
        if (!Uri.TryCreate(viewModel.LoginVerificationUri, UriKind.Absolute, out var uri)) return;

        await launcher.LaunchUriAsync(uri);
    }

    private async void OnAddGameFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择游戏文件夹（.minecraft）",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;

        var path = folders[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            viewModel.AddGameFolder(path);
    }

    private async void OnChoosePclExecutableClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 PCL2 启动器",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PCL2 启动器")
                {
                    Patterns = new[] { "*.exe" },
                },
            },
        });

        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            await viewModel.ImportPcl2FromLocationAsync(path);
    }

    private async void OnChoosePclFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 PCL 文件夹、启动器所在目录或游戏目录",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;

        var path = folders[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            await viewModel.ImportPcl2FromLocationAsync(path);
    }
}
