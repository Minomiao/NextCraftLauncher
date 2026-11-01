using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.ViewModels;

namespace NextCraftLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>打开当天的日志文件；文件不存在时打开日志目录。</summary>
    private void OnOpenLogFileClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = LauncherLog.CurrentFilePath;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (Directory.Exists(LauncherLog.DirectoryPath))
                Process.Start(new ProcessStartInfo(LauncherLog.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            LauncherLog.Error("打开日志文件失败", exception);
        }
    }

    /// <summary>导出包含异常全文与最近日志的错误报告，方便发给他人排查。</summary>
    private async void OnExportErrorReportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出错误报告",
                SuggestedFileName = $"NCL-error-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } },
                },
            });

            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(viewModel.BuildErrorReport());

            LauncherLog.Info($"错误报告已导出：{file.Path.LocalPath}");
        }
        catch (Exception exception)
        {
            LauncherLog.Error("导出错误报告失败", exception);
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 导航按钮等交互控件自行处理点击，不触发窗口拖动。
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        BeginMoveDrag(e);
    }
}
