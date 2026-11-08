using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NextCraftLauncher.ViewModels;

namespace NextCraftLauncher.Views;

public partial class DownloadView : UserControl
{
    public DownloadView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainViewModel viewModel)
            viewModel.ModSaveFilePicker = PickModSavePathAsync;
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
