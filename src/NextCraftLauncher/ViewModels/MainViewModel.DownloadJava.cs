using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Threading;
using NextCraftLauncher.Core.Auth;
using NextCraftLauncher.Core.Java;
using NextCraftLauncher.Core.Launch;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Minecraft;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Download;
using NextCraftLauncher.Download.Java;
using NextCraftLauncher.Download.Loaders;
using NextCraftLauncher.Download.Minecraft;
using NextCraftLauncher.Download.Mods;
using NextCraftLauncher.Icons;
using NextCraftLauncher.Theme;

namespace NextCraftLauncher.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>读取原版 JSON 中要求的 Java 主版本；Forge 至少 8，NeoForge 至少 17。</summary>
    private int ReadRequiredJavaMajor(string versionId, string loader)
    {
        var fallback = loader == "neoforge" ? 17 : 8;

        try
        {
            var path = Path.Combine(
                MinecraftDirectory, "versions", versionId, versionId + ".json");
            if (!File.Exists(path)) return fallback;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("javaVersion", out var javaVersion) &&
                javaVersion.TryGetProperty("majorVersion", out var majorVersion))
            {
                var required = majorVersion.GetInt32();
                return loader == "neoforge" ? Math.Max(required, 17) : Math.Max(required, 8);
            }
        }
        catch (Exception)
        {
            // 解析失败时使用兜底值。
        }

        return fallback;
    }

    [RelayCommand]
    private async Task LoadJavaRuntimesAsync()
    {
        if (IsJavaRuntimeCatalogLoading) return;

        IsJavaRuntimeCatalogLoading = true;
        IsJavaRuntimeCatalogFailed = false;
        JavaRuntimeCatalogStatus = "正在获取 Mojang Java 运行时列表...";
        var clock = Stopwatch.StartNew();

        try
        {
            var mirror = ResolveGameMirror();
            var result = await JavaRuntimeCatalog.LoadAsync(
                mirror: mirror,
                cancellationToken: _lifetime.Token);

            FillJavaRuntimes(result);

            _javaRuntimeCatalogLoaded = true;
            UpdateJavaRuntimeInstalledStatus();
            UpdateJavaRuntimeCatalogStatus();

            LauncherLog.Info($"[PERF] Java 运行时列表处理：{_allJavaRuntimes.Count} 项，{clock.ElapsedMilliseconds} ms");

            Status = $"已加载 {_allJavaRuntimes.Count} 个 Java 运行时（{result.Platform}）";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // 源不可用时用户只需要知道拿不到，具体原因写进日志。
            LauncherLog.Warn($"获取 Java 运行时列表失败：{exception.Message}");

            if (_allJavaRuntimes.Count == 0)
            {
                IsJavaRuntimeCatalogFailed = true;
                JavaRuntimeCatalogStatus = string.Empty;
            }
            else
            {
                // 缓存里还有一份，继续展示，不把错误顶到界面上。
                UpdateJavaRuntimeCatalogStatus();
            }
        }
        finally
        {
            IsJavaRuntimeCatalogLoading = false;
        }
    }

    /// <summary>把清单填进两个列表（全部 + 可安装），界面上出现的都是同一批实例。</summary>
    private void FillJavaRuntimes(JavaRuntimeCatalogResult result)
    {
        _allJavaRuntimes.Clear();
        foreach (var entry in result.Runtimes)
            _allJavaRuntimes.Add(new RemoteJavaRuntimeItemViewModel(entry, DownloadCatalogJavaAsync));

        AvailableJavaRuntimes.Clear();
        foreach (var item in _allJavaRuntimes)
            AvailableJavaRuntimes.Add(item);

        OnPropertyChanged(nameof(ShowJavaRuntimeCatalogLoading));
    }

    private async Task EnsureJavaRuntimesLoadedAsync()
    {
        if (_javaRuntimeCatalogLoaded || IsJavaRuntimeCatalogLoading) return;

        // 先用上次的缓存填表：网络慢或不通时 Java 页也不至于空白。
        if (_allJavaRuntimes.Count == 0) ApplyCachedJavaRuntimes();

        await LoadJavaRuntimesAsync();
    }

    /// <summary>读取上次成功获取的 Java 运行时清单；没有缓存时什么也不做。</summary>
    private void ApplyCachedJavaRuntimes()
    {
        var cached = JavaRuntimeCatalog.TryLoadCached();
        if (cached is null || cached.Runtimes.Count == 0) return;

        FillJavaRuntimes(cached);
        UpdateJavaRuntimeInstalledStatus();
    }

    private async Task DownloadCatalogJavaAsync(RemoteJavaRuntimeItemViewModel item)
    {
        if (item.IsInstalled) return;

        var label = string.IsNullOrWhiteSpace(item.VersionName)
            ? item.ComponentName
            : $"Java {item.MajorVersion}（{item.VersionName}）";

        await DownloadJavaRuntimeAsync(item.ComponentName, item.MajorVersion, label);
    }

    private static bool IsJavaRuntimeInstalled(string minecraftDirectory, string componentName)
    {
        var runtimeDirectory = Path.Combine(minecraftDirectory, "runtime", componentName);
        if (!Directory.Exists(runtimeDirectory)) return false;

        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        try
        {
            return Directory.EnumerateFiles(runtimeDirectory, executable, SearchOption.AllDirectories).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 刷新各运行时的安装状态。递归搜文件名很费磁盘，放到线程池做，避免启动和打开下载页时卡 UI。
    /// </summary>
    private void UpdateJavaRuntimeInstalledStatus()
    {
        if (_allJavaRuntimes.Count == 0) return;

        var directory = MinecraftDirectory;
        var items = _allJavaRuntimes.ToArray();

        _ = Task.Run(() =>
        {
            var results = items
                .Select(item => (Item: item, Installed: IsJavaRuntimeInstalled(directory, item.ComponentName)))
                .ToArray();

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (item, installed) in results)
                    item.IsInstalled = installed;

                UpdateJavaRuntimeCatalogStatus();
            });
        });
    }

    private void UpdateJavaRuntimeCatalogStatus()
    {
        if (_allJavaRuntimes.Count == 0)
        {
            if (!IsJavaRuntimeCatalogLoading) JavaRuntimeCatalogStatus = "尚未加载 Java 运行时列表";
            return;
        }

        var installedCount = _allJavaRuntimes.Count(item => item.IsInstalled);
        JavaRuntimeCatalogStatus = $"已安装 {installedCount} / {_allJavaRuntimes.Count}";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadJavaAsync()
    {
        if (!CanDownloadJava || _resolved is null) return;

        await DownloadJavaRuntimeAsync(
            _resolved.Merged.JavaVersion?.Component,
            _resolved.RequiredJavaMajor,
            $"Java {_resolved.RequiredJavaMajor}");
    }

    private async Task DownloadJavaRuntimeAsync(string? component, int requiredMajor, string label)
    {
        if (requiredMajor <= 0) return;

        var task = TryCreateTask(label, DownloadTaskKind.Java, label);
        if (task is null) return;

        task.StatusText = "正在获取下载信息...";

        CurrentPage = AppPage.Download;
        EnterTaskSection(leaveVersionDetail: true);
        Status = $"正在获取 {label} 下载信息...";
        LauncherLog.Info($"开始下载 {label}");

        try
        {
            var mirror = ResolveGameMirror();

            var lastUiTicks = 0L;
            var progress = new Progress<JavaRuntimeDownloadProgress>(report =>
            {
                var now = Environment.TickCount64;
                var isFinal = report.Percent is >= 100 ||
                              (report.TotalFiles > 0 && report.CompletedFiles >= report.TotalFiles);

                if (!isFinal && now - lastUiTicks < 100)
                    return;

                lastUiTicks = now;

                task.CompletedFiles = report.CompletedFiles;
                task.TotalFiles = report.TotalFiles;

                var percent = report.Percent is double value ? $"（{value:0}%）" : string.Empty;
                var megabytes = report.BytesDownloaded / 1024.0 / 1024.0;
                var speed = task.SpeedMeter.Feed(report.BytesDownloaded);
                var speedText = speed.Length > 0 ? $"，{speed}" : string.Empty;
                task.Percent = report.Percent ?? 0;
                task.StatusText = $"{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}";
                Status = report.TotalFiles > 0
                    ? $"正在下载 {label}：{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}{percent}"
                    : $"正在下载 {label}...";
                NotifyDownloadStats();
            });

            var result = await Task.Run(
                () => JavaRuntimeDownloader.DownloadAsync(
                    MinecraftDirectory,
                    component,
                    requiredMajor,
                    progress,
                    new JavaRuntimeDownloadOptions
                    {
                        Mirror = mirror,
                    },
                    task.Token),
                task.Token);

            var runtime = await JavaLocator.ProbeAsync(
                result.JavaExecutablePath, MinecraftDirectory, _lifetime.Token);

            if (runtime is null)
            {
                Status = $"{label} 下载完成，但无法运行";
                FinishTask(task, DownloadTaskState.Failed, "下载完成，但无法运行");
                return;
            }

            var existing = JavaRuntimes.FirstOrDefault(item =>
                string.Equals(item.Path, runtime.Path, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
                JavaRuntimes.Add(runtime);
            else
                runtime = existing;

            SelectedJava = runtime;
            task.Percent = 100;
            Status = $"{label} 下载完成";
            LauncherLog.Info($"{label} 下载完成：{runtime.Path}");

            foreach (var item in _allJavaRuntimes)
            {
                if (string.Equals(item.ComponentName, result.ComponentName, StringComparison.OrdinalIgnoreCase))
                    item.IsInstalled = true;
            }
            UpdateJavaRuntimeCatalogStatus();

            FinishTask(task, DownloadTaskState.Completed, $"已安装 {label}");
        }
        catch (OperationCanceledException)
        {
            Status = "Java 下载已取消";
            LauncherLog.Warn($"{label} 下载已取消");
            FinishTask(task, DownloadTaskState.Canceled, "下载已取消");
        }
        catch (Exception exception)
        {
            Status = "Java 下载失败";
            LauncherLog.Error($"{label} 下载失败", exception);
            ShowDownloadError($"下载 {label}", exception);
            FinishTask(task, DownloadTaskState.Failed, "下载失败");
        }
        finally
        {
            UpdateJavaRuntimeInstalledStatus();
        }
    }
}
