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
    partial void OnMemoryMbChanged(int value)
    {
        _settings.MemoryMb = value;
        _settings.Save();
    }

    partial void OnOpenFolderAfterModDownloadChanged(bool value)
    {
        _settings.OpenFolderAfterModDownload = value;
        _settings.Save();
    }

    partial void OnSelectedModDefaultFilterIndexChanged(int value)
    {
        if (value is < 0 or > 1) return;

        _settings.ModDetailStartsWithRecommend = value == 1;
        _settings.Save();
    }

    partial void OnOpenFolderAfterResourcePackDownloadChanged(bool value)
    {
        _settings.OpenFolderAfterResourcePackDownload = value;
        _settings.Save();
    }

    partial void OnJumpToTasksAfterModDownloadChanged(bool value)
    {
        _settings.JumpToTasksAfterModDownload = value;
        _settings.Save();
    }

    partial void OnJumpToTasksAfterResourcePackDownloadChanged(bool value)
    {
        _settings.JumpToTasksAfterResourcePackDownload = value;
        _settings.Save();
    }

    partial void OnSelectedResourcePackDefaultFilterIndexChanged(int value)
    {
        if (value is < 0 or > 1) return;

        _settings.ResourcePackDetailStartsWithRecommend = value == 1;
        _settings.Save();
    }

    partial void OnPlayerNameChanged(string value)
    {
        _settings.PlayerName = value;
        _settings.Save();
    }

    partial void OnSelectedGameMirrorIndexChanged(int value)
    {
        if (value is < 0 or > 3) return;

        var mirror = (DownloadMirror)value;
        if (_settings.GameMirror == mirror) return;

        _settings.GameMirror = mirror;
        _settings.Save();

        // 回到「游戏版本」子页时会重新拉取清单；进行中的旧请求结果也作废。
        _gameMirrorGeneration++;
        _remoteManifestLoaded = false;
        _javaRuntimeCatalogLoaded = false;
        RemoteVersionsStatus = "游戏版本源已切换，正在重新获取...";
    }

    partial void OnSelectedForgeMirrorIndexChanged(int value)
    {
        UpdateLoaderMirror(value, _settings.ForgeMirror, mirror => _settings.ForgeMirror = mirror);
    }

    partial void OnSelectedFabricMirrorIndexChanged(int value)
    {
        UpdateLoaderMirror(value, _settings.FabricMirror, mirror => _settings.FabricMirror = mirror);
    }

    partial void OnSelectedNeoForgeMirrorIndexChanged(int value)
    {
        UpdateLoaderMirror(value, _settings.NeoForgeMirror, mirror => _settings.NeoForgeMirror = mirror);
    }

    private void UpdateLoaderMirror(int value, DownloadMirror current, Action<DownloadMirror> apply)
    {
        if (value is < 0 or > 2) return;

        var mirror = LoaderMirrorFromIndex(value);
        if (current == mirror) return;

        apply(mirror);
        _settings.Save();
        _loaderMirrorDirty = true;
    }

    partial void OnSelectedLauncherVisibilityIndexChanged(int value)
    {
        var visibility = value is >= 0 and <= 2
            ? (LauncherVisibilityAfterLaunch)value
            : LauncherVisibilityAfterLaunch.Keep;
        if (_settings.LauncherVisibility == visibility) return;

        _settings.LauncherVisibility = visibility;
        _settings.Save();
    }

    partial void OnSelectedProcessPriorityIndexChanged(int value)
    {
        var priority = value is >= 0 and <= 4 ? (GameProcessPriority)value : GameProcessPriority.Normal;
        if (_settings.GameProcessPriority == priority) return;

        _settings.GameProcessPriority = priority;
        _settings.Save();
    }

    partial void OnSelectedWindowSizeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomWindowSize));

        var size = value is >= 0 and <= 4 ? (GameWindowSizeMode)value : GameWindowSizeMode.Default;
        if (_settings.GameWindowSize == size) return;

        _settings.GameWindowSize = size;
        _settings.Save();
    }

    partial void OnCustomWindowWidthChanged(double value)
    {
        var width = (int)Math.Clamp(value, 320, 7680);
        if (_settings.GameWindowWidth == width) return;

        _settings.GameWindowWidth = width;
        _settings.Save();
    }

    partial void OnCustomWindowHeightChanged(double value)
    {
        var height = (int)Math.Clamp(value, 240, 4320);
        if (_settings.GameWindowHeight == height) return;

        _settings.GameWindowHeight = height;
        _settings.Save();
    }

    private DownloadMirror ResolveGameMirror() => ToDownloadMirror(SelectedGameMirrorIndex);

    private DownloadMirror ResolveLoaderMirror(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => LoaderMirrorFromIndex(SelectedForgeMirrorIndex),
        "fabric" or "fabric-api" => LoaderMirrorFromIndex(SelectedFabricMirrorIndex),
        "neoforge" => LoaderMirrorFromIndex(SelectedNeoForgeMirrorIndex),
        _ => DownloadMirror.Automatic,
    };

    private DownloadMirror ResolvePrefetchMirror(string key)
        => string.Equals(key, "game", StringComparison.OrdinalIgnoreCase)
            ? ResolveGameMirror()
            : ResolveLoaderMirror(key);

    private static DownloadMirror ToDownloadMirror(int index)
        => index is >= 0 and <= 3 ? (DownloadMirror)index : DownloadMirror.Automatic;

    private static int LoaderMirrorIndexFrom(DownloadMirror mirror) => mirror switch
    {
        DownloadMirror.BmclApi => 2,
        DownloadMirror.Automatic => 0,
        // 加载器没有 Mojang CDN / 官方之分，旧配置里的 MojangCdn 归入「官方」。
        _ => 1,
    };

    private static DownloadMirror LoaderMirrorFromIndex(int index) => index switch
    {
        1 => DownloadMirror.Official,
        2 => DownloadMirror.BmclApi,
        _ => DownloadMirror.Automatic,
    };

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(refreshJava: true);

    private async Task RefreshCoreAsync(bool refreshJava)
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = "正在扫描版本...";

        try
        {
            var directory = MinecraftDirectory;
            var summaries = await Task.Run(() => VersionScanner.Scan(directory), _lifetime.Token);

            Versions.Clear();
            foreach (var summary in summaries)
            {
                var versionDirectory = AppSettings.NormalizePath(Path.GetDirectoryName(summary.JsonPath));
                if (versionDirectory is not null &&
                    _settings.HiddenVersions.Any(item =>
                        string.Equals(item, versionDirectory, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Versions.Add(new VersionItemViewModel(summary));
            }

            VersionsCountText = $"共 {Versions.Count} 个版本";
            EnsureGameFolderEntry(directory);
            UpdateRemoteInstalledStatus();
            UpdateJavaRuntimeInstalledStatus();

            var previousId = RememberedVersionId(directory) ?? SelectedVersion?.Id;
            SelectedVersion = Versions.FirstOrDefault(version => version.Id == previousId) ?? Versions.FirstOrDefault();

            if (refreshJava)
                await RefreshJavaAsync();

            Status = Versions.Count == 0
                ? "未找到版本，请确认路径指向 .minecraft 目录"
                : $"已加载 {Versions.Count} 个版本";

        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Status = "扫描失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 从用户选择的 PCL2 启动器 exe、PCL 配置文件夹或游戏目录导入设置。
    /// </summary>
    public async Task ImportPcl2FromLocationAsync(string selectedPath)
    {
        if (IsBusy) return;

        try
        {
            var location = await Task.Run(
                () => Pcl2ConfigImporter.ResolveLocation(selectedPath),
                _lifetime.Token);

            if (location is null)
            {
                Status = "未找到 PCL2 配置";
                return;
            }

            MinecraftDirectory = location.GameDirectory;

            // PCL 中打开过的文件夹全部登记，最后仍停留在 PCL 当前的文件夹上。
            foreach (var directory in location.GameDirectories)
                AddGameFolderEntry(directory, select: false);

            var currentFolder = GameFolders.FirstOrDefault(folder =>
                string.Equals(folder.Path, location.GameDirectory, StringComparison.OrdinalIgnoreCase));
            if (currentFolder is not null) SelectedGameFolder = currentFolder;

            await RefreshAsync();

            if (!string.IsNullOrWhiteSpace(location.PreferredVersionId))
            {
                var preferred = Versions.FirstOrDefault(version =>
                    string.Equals(version.Id, location.PreferredVersionId, StringComparison.OrdinalIgnoreCase));
                if (preferred is not null) SelectedVersion = preferred;
            }

            // 等版本详情解析完，避免它随后覆盖导入的 Java 选择和隔离状态。
            await _detailsTask;

            var result = await Task.Run(
                () => Pcl2ConfigImporter.Import(location, SelectedVersion?.Id),
                _lifetime.Token);

            await ApplyPcl2ImportAsync(result, location.GameDirectories.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Status = "导入 PCL2 设置失败";
        }
    }

    private async Task ApplyPcl2ImportAsync(Pcl2ImportResult result, int folderCount = 0)
    {
        if (!result.FoundConfig)
        {
            Status = "未找到 PCL2 配置";
            return;
        }

        var applied = new List<string>();

        if (folderCount > 0)
            applied.Add($"{folderCount} 个文件夹");

        if (result.MemoryMb is int memoryMb)
        {
            if (!MemoryOptions.Contains(memoryMb))
            {
                MemoryOptions.Add(memoryMb);
                var sorted = MemoryOptions.OrderBy(value => value).ToList();
                MemoryOptions.Clear();
                foreach (var value in sorted) MemoryOptions.Add(value);
            }

            MemoryMb = memoryMb;
            applied.Add($"内存 {memoryMb} MB");
        }

        if (result.Isolated is bool isolated)
        {
            SelectedIsolationIndex = isolated ? (int)DefaultIsolationMode.All : (int)DefaultIsolationMode.Off;
            applied.Add(isolated ? "版本隔离开" : "版本隔离关");
        }

        if (!string.IsNullOrWhiteSpace(result.JavaPath))
        {
            var java = JavaRuntimes.FirstOrDefault(runtime =>
                string.Equals(runtime.Path, result.JavaPath, StringComparison.OrdinalIgnoreCase));

            if (java is null)
            {
                java = await JavaLocator.ProbeAsync(result.JavaPath, MinecraftDirectory, _lifetime.Token);
                if (java is not null) JavaRuntimes.Add(java);
            }

            if (java is not null)
            {
                SelectedJava = java;
                applied.Add($"Java {java.MajorVersion}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.JavaRange))
        {
            var java = JavaLocator.SelectInRange(JavaRuntimes, result.JavaRange);
            if (java is not null)
            {
                SelectedJava = java;
                applied.Add($"Java {java.MajorVersion}");
            }
        }

        Status = applied.Count > 0
            ? "已导入 PCL2 设置：" + string.Join("、", applied)
            : "已读取 PCL2 配置，但没有可应用的启动设置";
    }
}
