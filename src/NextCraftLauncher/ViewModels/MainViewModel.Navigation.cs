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
    // ---- 下载任务卡片 ----

    /// <summary>新建一个任务卡片并切到任务页；同一个去重键的任务已在运行时不重复创建。</summary>
    private DownloadTaskViewModel? TryCreateTask(string title, DownloadTaskKind kind, string key)
    {
        var duplicate = DownloadTasks.Any(task =>
            task.IsRunning &&
            task.Kind == kind &&
            string.Equals(task.Key, key, StringComparison.OrdinalIgnoreCase));

        if (duplicate)
        {
            Status = $"{title} 已在下载任务中";
            return null;
        }

        var task = new DownloadTaskViewModel(title, key, kind, _lifetime.Token);
        DownloadTasks.Add(task);
        OnPropertyChanged(nameof(HasDownloadTasks));
        RaiseActiveDownloadState();
        return task;
    }

    /// <summary>结束任务：写回终态，并在几秒后自动移除卡片。</summary>
    private void FinishTask(DownloadTaskViewModel task, DownloadTaskState state, string statusText)
    {
        task.StatusText = statusText;
        task.State = state;
        RaiseActiveDownloadState();

        try
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RemoveTask(task);
            };
            timer.Start();
        }
        catch (Exception)
        {
            // 启动器正在关闭时调度器已停止，卡片留着也无影响。
        }
    }

    private void RemoveTask(DownloadTaskViewModel task)
    {
        if (!DownloadTasks.Remove(task)) return;

        OnPropertyChanged(nameof(HasDownloadTasks));
        RaiseActiveDownloadState();
        task.Dispose();
    }

    /// <summary>任务增减会影响左侧的聚合统计与入口按钮。</summary>
    private void RaiseActiveDownloadState()
    {
        // 后台预取会从另一个线程询问「是否忙」，不能直接读 ObservableCollection。
        Volatile.Write(ref _activeTaskCount, DownloadTasks.Count(task => task.IsRunning));
        OnPropertyChanged(nameof(HasActiveDownloads));
        NotifyDownloadStats();
    }

    /// <summary>把版本下载阶段映射到卡片上的三行步骤。</summary>
    private static void ApplyVersionStage(DownloadTaskViewModel task, MinecraftDownloadStage stage)
    {
        if (task.Steps.Count < 3) return;

        var (first, second, third) = (task.Steps[0], task.Steps[1], task.Steps[2]);

        switch (stage)
        {
            case MinecraftDownloadStage.Preparing:
                first.SetState(DownloadStepState.Active, "…");
                second.SetState(DownloadStepState.Pending);
                third.SetState(DownloadStepState.Pending);
                break;

            case MinecraftDownloadStage.Files:
                first.SetState(DownloadStepState.Done);
                if (second.State != DownloadStepState.Active)
                    second.SetState(DownloadStepState.Active, "…");
                third.SetState(DownloadStepState.Pending);
                break;

            case MinecraftDownloadStage.Finalizing:
                first.SetState(DownloadStepState.Done);
                second.SetState(DownloadStepState.Done);
                third.SetState(DownloadStepState.Active, "…");
                break;
        }
    }

    /// <summary>给版本任务卡片添上三步流程。</summary>
    private static void AddVersionSteps(DownloadTaskViewModel task)
    {
        task.AddStep("版本信息");
        task.AddStep("游戏文件");
        task.AddStep("写入配置");
        ApplyVersionStage(task, MinecraftDownloadStage.Preparing);
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsLaunchPageSelected));
        OnPropertyChanged(nameof(IsDownloadPageSelected));
        OnPropertyChanged(nameof(IsSettingsPageSelected));
        OnPropertyChanged(nameof(IsSubPageOpen));

        if (value == AppPage.Download)
        {
            var clock = Stopwatch.StartNew();

            _ = EnsureRemoteVersionsLoadedAsync();
            _ = EnsureJavaRuntimesLoadedAsync();

            // 预取在启动时完成；若此处仍未完成，界面会显示“正在获取版本清单”。
            if (_remoteManifestLoaded && _filteredRemoteVersionCount == 0 && _allRemoteVersions.Count > 0)
                ApplyRemoteVersionFilter();

            // 首次打开要完成页面模板解析与布局；只在真的卡了的时候记一笔，
            // 平时（几毫秒）不刷日志。Background 低于 Render，能覆盖到渲染。
            Dispatcher.UIThread.Post(
                () =>
                {
                    var elapsed = clock.ElapsedMilliseconds;
                    if (elapsed >= 50)
                        LauncherLog.Info($"[PERF] 下载页首帧：{elapsed} ms");
                },
                DispatcherPriority.Background);
        }
    }

    partial void OnRemoteVersionSearchChanged(string value)
    {
        _remoteSearchTimer.Stop();
        _remoteSearchTimer.Start();
    }

    partial void OnRemoteVersionTypeChanged(string value) => ApplyRemoteVersionFilter();

    partial void OnIsRemoteVersionsLoadingChanged(bool value)
        => OnPropertyChanged(nameof(ShowRemoteVersionsLoading));

    partial void OnSelectedRemoteVersionChanged(RemoteVersionItemViewModel? value)
    {
        foreach (var item in _allRemoteVersions)
            item.IsSelected = ReferenceEquals(item, value);

        UpdateRelatedRemoteVersions();

        OnPropertyChanged(nameof(CanDownloadSelectedVersion));
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(DownloadIconName));
        OnPropertyChanged(nameof(HasDuplicateVersionName));
    }

    partial void OnVersionNameChanged(string value)
    {
        if (_updatingVersionName) return;

        _versionNameEdited = true;
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(HasDuplicateVersionName));
    }



    [RelayCommand]
    private void ShowLaunchPage() => CurrentPage = AppPage.Launch;

    [RelayCommand]
    private void ShowStartSection()
    {
        IsVersionDetailOpen = false;
        CurrentLaunchSection = LaunchSection.Start;
    }

    [RelayCommand]
    private void ShowVersionsSection()
    {
        // 从侧边栏回到「版本」时总是先看到列表。
        IsVersionDetailOpen = false;
        CurrentLaunchSection = LaunchSection.Versions;
    }

    [RelayCommand]
    private void ShowDownloadVersions() => CurrentDownloadSection = DownloadSection.Versions;

    [RelayCommand]
    private void ShowDownloadMods() => CurrentDownloadSection = DownloadSection.Mods;

    [RelayCommand]
    private void ShowDownloadResourcePacks() => CurrentDownloadSection = DownloadSection.ResourcePacks;

    [RelayCommand]
    private void ShowDownloadJava() => CurrentDownloadSection = DownloadSection.Java;

    [RelayCommand]
    private void ShowDownloadRunning() => EnterTaskSection();

    [RelayCommand]
    private void ReturnFromDownloadTasks() => CurrentDownloadSection = _previousDownloadSection;

    private void EnterTaskSection(bool leaveVersionDetail = false)
    {
        // 从下载页跳到任务页时，把版本详情收起来，返回后回到版本列表而不是停在详情页。
        if (leaveVersionDetail && IsRemoteVersionDetailOpen)
            CloseRemoteVersionDetail();

        if (CurrentDownloadSection != DownloadSection.Running)
            _previousDownloadSection = CurrentDownloadSection;

        CurrentDownloadSection = DownloadSection.Running;
    }


    [RelayCommand]
    private void ShowDownloadPage() => CurrentPage = AppPage.Download;

    [RelayCommand]
    private void ShowSettingsPage() => CurrentPage = AppPage.Settings;

    [RelayCommand]
    private void ShowSettingsLaunch() => CurrentSettingsSection = SettingsSection.Launch;

    [RelayCommand]
    private void ShowSettingsDownload() => CurrentSettingsSection = SettingsSection.Download;

    [RelayCommand]
    private void ShowSettingsAppearance() => CurrentSettingsSection = SettingsSection.Appearance;

    /// <summary>标题栏的返回按钮：按当前所在的子页面决定退回哪里。</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (IsLaunchPageSelected)
        {
            if (IsVersionDetailOpen)
                CloseVersionSettings();
            else
                ShowStartSection();

            return;
        }

        if (!IsDownloadPageSelected) return;

        if (IsRemoteVersionDetailOpen)
            CloseRemoteVersionDetail();
        else if (IsModDetailOpen)
            CloseModDetail();
        else if (IsDownloadRunningSelected)
            ReturnFromDownloadTasks();
    }

    [RelayCommand]
    private void RequestVersionDelete(VersionItemViewModel? version)
    {
        if (version is not null) SelectedVersion = version;
        if (SelectedVersion is null) return;

        VersionDeleteDialogMessage =
            $"确定要处理版本 “{SelectedVersion.Id}” 吗？\n\n" +
            $"{SelectedVersion.DirectoryPath}\n\n" +
            "完全删除会从磁盘移除整个版本文件夹，且无法恢复。";
        IsVersionDeleteDialogOpen = true;
    }

    /// <summary>
    /// 进入某个版本的设置页；目前该页只展示版本信息。
    /// 只切换设置页查看的版本，不改变启动页当前版本。
    /// </summary>
    [RelayCommand]
    private void OpenVersionSettings(VersionItemViewModel? version)
    {
        var target = version ?? SelectedVersion;
        if (target is null) return;

        // 设置页挂在「版本」区块下，所以先切过去；关闭时再回到原来的区块。
        _sectionBeforeVersionDetail = CurrentLaunchSection;

        // 先标记详情页，再切区块：版本选择列表的进场动画据此跳过。
        IsVersionDetailOpen = true;
        CurrentVersionSettingsSection = VersionSettingsSection.Settings;
        CurrentLaunchSection = LaunchSection.Versions;

        VersionSettingsVersion = target;
        _ = LoadVersionSettingsDetailsAsync(target);
    }

    /// <summary>解析设置页要展示的版本信息，与启动页的解析互不影响。</summary>
    private async Task LoadVersionSettingsDetailsAsync(VersionItemViewModel item)
    {
        var revision = ++_versionSettingsRevision;
        VersionSettingsDetails = "正在解析版本...";

        try
        {
            var resolved = await Task.Run(
                () => VersionResolver.Resolve(MinecraftDirectory, item.Id, _settings.DefaultIsolation),
                _lifetime.Token);
            if (revision != _versionSettingsRevision) return;

            VersionSettingsDetails = BuildDetailsText(resolved);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (revision != _versionSettingsRevision) return;
            VersionSettingsDetails = "解析失败: " + exception.Message;
        }
    }

    /// <summary>启动按钮下方的「版本设置」：直接打开当前版本的设置页。</summary>
    [RelayCommand]
    private void OpenCurrentVersionSettings() => OpenVersionSettings(null);

    [RelayCommand]
    private void ShowVersionSettings() => CurrentVersionSettingsSection = VersionSettingsSection.Settings;

    [RelayCommand]
    private void ShowVersionMods() => CurrentVersionSettingsSection = VersionSettingsSection.Mods;

    [RelayCommand]
    private void ShowVersionExport() => CurrentVersionSettingsSection = VersionSettingsSection.Export;

    [RelayCommand]
    private void CloseVersionSettings()
    {
        IsVersionDetailOpen = false;
        CurrentLaunchSection = _sectionBeforeVersionDetail;
    }

    [RelayCommand]
    private void CancelVersionDelete() => IsVersionDeleteDialogOpen = false;

    [RelayCommand]
    private void HideSelectedVersion()
    {
        var version = SelectedVersion;
        IsVersionDeleteDialogOpen = false;
        if (version is null) return;

        IsVersionDetailOpen = false;

        var directory = AppSettings.NormalizePath(version.DirectoryPath);
        if (directory is not null &&
            !_settings.HiddenVersions.Any(item =>
                string.Equals(item, directory, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.HiddenVersions.Add(directory);
            _settings.Save();
        }

        Versions.Remove(version);
        VersionsCountText = $"共 {Versions.Count} 个版本";
        SelectedVersion = Versions.FirstOrDefault();
        Status = $"已隐藏版本 {version.Id}";
    }

    [RelayCommand]
    private async Task DeleteSelectedVersionAsync()
    {
        var version = SelectedVersion;
        IsVersionDeleteDialogOpen = false;
        if (version is null || IsBusy || IsLaunching) return;

        IsVersionDetailOpen = false;

        var directory = AppSettings.NormalizePath(version.DirectoryPath);
        var versionsRoot = AppSettings.NormalizePath(Path.Combine(MinecraftDirectory, "versions"));
        if (directory is null || versionsRoot is null ||
            !directory.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Status = "删除版本失败：路径无效";
            return;
        }

        IsBusy = true;
        var deleted = false;

        try
        {
            await Task.Run(() => Directory.Delete(directory, recursive: true), _lifetime.Token);
            deleted = true;
            Status = $"已删除版本 {version.Id}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            VersionDeleteDialogMessage =
                $"删除版本 “{version.Id}” 失败：{ex.Message}\n\n" +
                "可以重试，或选择「不显示」仅从列表中隐藏。";
            IsVersionDeleteDialogOpen = true;
        }
        finally
        {
            IsBusy = false;
        }

        if (deleted) await RefreshAsync();
    }

    partial void OnIsVersionIsolatedChanged(bool value) => OnPropertyChanged(nameof(EffectiveGameDirectory));

    partial void OnSelectedIsolationIndexChanged(int value)
    {
        var mode = value is >= 0 and <= 4 ? (DefaultIsolationMode)value : DefaultIsolationMode.All;
        if (_settings.DefaultIsolation == mode) return;

        _settings.DefaultIsolation = mode;
        _settings.Save();

        // 重新解析当前版本，让隔离方式立即按新默认值生效。
        if (SelectedVersion is not null)
            _ = LoadDetailsAsync(SelectedVersion);
    }

    partial void OnMinecraftDirectoryChanged(string value)
    {
        _settings.MinecraftDirectory = value;
        _settings.Save();
    }

    /// <summary>添加一个游戏文件夹并切换过去。</summary>
    public void AddGameFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string fullPath;
        try
        {
            fullPath = AppSettings.NormalizePath(path) ?? path.Trim();
        }
        catch (Exception)
        {
            fullPath = path.Trim();
        }

        var existing = GameFolders.FirstOrDefault(folder =>
            string.Equals(folder.Path, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new GameFolderItemViewModel(fullPath);
            GameFolders.Add(existing);
        }

        if (!_settings.GameDirectories.Any(item =>
                string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.GameDirectories.Add(fullPath);
        }

        _settings.Save();

        MinecraftDirectory = fullPath;
        SelectedGameFolder = existing;
        _ = RefreshAsync();
    }

    /// <summary>确保当前扫描的游戏目录出现在文件夹列表中。</summary>
    private void EnsureGameFolderEntry(string path) => AddGameFolderEntry(path, select: true);

    private void AddGameFolderEntry(string path, bool select)
    {
        var normalized = AppSettings.NormalizePath(path);
        if (normalized is null) return;

        var existing = GameFolders.FirstOrDefault(folder =>
            string.Equals(folder.Path, normalized, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new GameFolderItemViewModel(normalized);
            GameFolders.Add(existing);
        }

        if (!_settings.GameDirectories.Any(item =>
                string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.GameDirectories.Add(normalized);
            _settings.Save();
        }

        if (select && !ReferenceEquals(SelectedGameFolder, existing))
            SelectedGameFolder = existing;
    }
}
