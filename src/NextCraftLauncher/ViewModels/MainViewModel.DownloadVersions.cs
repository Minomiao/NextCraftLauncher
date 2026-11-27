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
    [RelayCommand]
    private async Task LoadRemoteVersionsAsync()
    {
        if (IsRemoteVersionsLoading) return;

        IsRemoteVersionsLoading = true;
        RemoteVersionsStatus = "正在获取 Mojang 版本清单...";
        var generation = _gameMirrorGeneration;

        try
        {
            // 清单请求不等待测速：多个候选源直接并发竞速。
            var mirror = ResolveGameMirror();
            var result = await MinecraftVersionCatalog.LoadAsync(
                mirror: mirror,
                cancellationToken: _lifetime.Token);

            // 加载期间切换了游戏版本源，丢弃这次结果，回到该子页时会重新获取。
            if (generation != _gameMirrorGeneration)
            {
                _remoteManifestLoaded = false;
                return;
            }

            ApplyRemoteManifest(result, markLoaded: true);
            Status = $"已加载 {_allRemoteVersions.Count} 个远程版本（最新正式版 {result.LatestRelease ?? "-"}）";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RemoteVersionsStatus = "获取版本清单失败：" + exception.Message;
        }
        finally
        {
            IsRemoteVersionsLoading = false;
        }
    }

    private void ApplyRemoteManifest(MinecraftVersionManifestResult result, bool markLoaded)
    {
        if (markLoaded) _remoteManifestLoaded = true;

        // 启动时会先用缓存、再用网络结果各刷新一次。内容没变就不要重建集合：
        // 重建会让列表整体 Reset，正在按下的行被替换，点击会被丢掉。
        if (SameManifest(result.Versions))
        {
            UpdateRemoteInstalledStatus();

            // 缓存清单先到、网络清单后到时内容一般一致，但可见行可能还是空的
            // （启动时只填了源数据）；这里补一次筛选，避免列表一直空白。
            if (RemoteVersionRows.Count == 0 && _allRemoteVersions.Count > 0)
                ApplyRemoteVersionFilter();

            return;
        }

        var clock = Stopwatch.StartNew();
        var selectedId = SelectedRemoteVersion?.Id;

        _allRemoteVersions.Clear();
        _remoteVersionRows.Clear();
        foreach (var entry in result.Versions)
            _allRemoteVersions.Add(new RemoteVersionItemViewModel(entry, SelectRemoteVersion));

        // 重新构建集合后把选中的版本重新指向新实例，避免详情页引用旧对象。
        if (selectedId is not null)
        {
            var selected = _allRemoteVersions.FirstOrDefault(item =>
                string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));

            if (selected is not null)
                SelectedRemoteVersion = selected;
            else if (IsRemoteVersionDetailOpen)
            {
                _activeLoaderVersionId = null;
                IsRemoteVersionDetailOpen = false;
            }
        }

        UpdateRemoteInstalledStatus();
        ApplyRemoteVersionFilter();
        LauncherLog.Info($"版本清单已重建：{_allRemoteVersions.Count} 个版本（{clock.ElapsedMilliseconds} ms）");
    }

    /// <summary>比较版本列表是否与当前一致（不看 URL / 哈希，这些不影响列表展示）。</summary>
    private bool SameManifest(IReadOnlyList<MinecraftVersionEntry> versions)
    {
        if (versions.Count != _allRemoteVersions.Count) return false;

        for (var index = 0; index < versions.Count; index++)
        {
            var incoming = versions[index];
            var current = _allRemoteVersions[index];

            if (!string.Equals(incoming.Id, current.Id, StringComparison.Ordinal) ||
                !string.Equals(incoming.Type ?? "unknown", current.Type, StringComparison.Ordinal) ||
                incoming.ReleaseTime != current.ReleaseTime)
            {
                return false;
            }
        }

        return true;
    }

    private async Task EnsureRemoteVersionsLoadedAsync()
    {
        if (_remoteManifestLoaded || IsRemoteVersionsLoading) return;
        await LoadRemoteVersionsAsync();
    }

    /// <summary>当前类型 / 搜索条件下会显示的版本（与列表同一套过滤）。</summary>
    private List<RemoteVersionItemViewModel> FilteredRemoteVersions()
    {
        var search = RemoteVersionSearch.Trim();
        var filtered = new List<RemoteVersionItemViewModel>();

        foreach (var item in _allRemoteVersions)
        {
            if (!MatchesRemoteVersionType(item)) continue;
            if (search.Length > 0 && !item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            filtered.Add(item);
        }

        return filtered;
    }

    private void ApplyRemoteVersionFilter()
    {
        var rows = new List<RemoteVersionListRowViewModel>();

        var filtered = FilteredRemoteVersions();
        _filteredRemoteVersionCount = filtered.Count;

        // 侧栏的邻近版本按列表实际显示的顺序取。
        _filteredRemoteVersions.Clear();

        // 「全部」按发布时间新→旧平铺，不分类型组；单一类型仍保留分组标题。
        if (RemoteVersionType == "全部")
        {
            foreach (var item in filtered
                         .OrderByDescending(item => item.ReleaseTime ?? DateTimeOffset.MinValue))
            {
                rows.Add(GetRemoteVersionRow(item));
                _filteredRemoteVersions.Add(item);
            }
        }
        else
        {
            foreach (var group in filtered
                         .GroupBy(item => item.Type)
                         .OrderBy(group => VersionTypeOrder(group.Key)))
            {
                var items = group
                    .OrderByDescending(item => item.ReleaseTime ?? DateTimeOffset.MinValue)
                    .ToList();

                if (items.Count == 0) continue;

                rows.Add(RemoteVersionListRowViewModel.CreateHeader(VersionTypeTitle(group.Key), items.Count));
                foreach (var item in items)
                {
                    rows.Add(GetRemoteVersionRow(item));
                    _filteredRemoteVersions.Add(item);
                }
            }
        }

        RemoteVersionRows.ReplaceAll(rows);
        OnPropertyChanged(nameof(ShowRemoteVersionsLoading));
        UpdateRemoteVersionsStatus();
        UpdateRelatedRemoteVersions();
    }

    private RemoteVersionListRowViewModel GetRemoteVersionRow(RemoteVersionItemViewModel item)
    {
        if (_remoteVersionRows.TryGetValue(item, out var row)) return row;

        row = RemoteVersionListRowViewModel.CreateVersion(item);
        _remoteVersionRows[item] = row;
        return row;
    }

    private void SelectRemoteVersion(RemoteVersionItemViewModel item)
    {
        SelectedRemoteVersion = item;
        IsRemoteVersionDetailOpen = true;
        _activeLoaderVersionId = item.Id;

        _selectedLoaderKey = null;
        _selectedLoaderVersion = null;
        _versionNameEdited = false;
        SetVersionName(VersionNaming.BuildDefault(item.Id, null, null));

        _ = LoadLoaderVersionsAsync(item);
    }

    /// <summary>取 index 前后各若干项，凑够 6 个（不含 index 自己）。</summary>
    private static List<T> RelatedItems<T>(IReadOnlyList<T> source, int index)
    {
        const int count = 6;
        var related = new List<T>(count);
        if (index < 0 || source.Count <= 1) return related;

        var start = index - count / 2;
        var end = index + count / 2;

        if (start < 0)
        {
            end -= start;
            start = 0;
        }

        if (end >= source.Count)
        {
            start -= end - (source.Count - 1);
            start = Math.Max(0, start);
            end = source.Count - 1;
        }

        for (var position = start; position <= end && related.Count < count; position++)
        {
            if (position != index) related.Add(source[position]);
        }

        return related;
    }

    private void UpdateRelatedRemoteVersions()
    {
        var selected = SelectedRemoteVersion;
        var index = selected is null ? -1 : _filteredRemoteVersions.IndexOf(selected);

        RelatedRemoteVersions.ReplaceAll(RelatedItems(_filteredRemoteVersions, index));
        OnPropertyChanged(nameof(HasRelatedRemoteVersions));
    }

    private void UpdateRelatedModProjects()
    {
        var selected = SelectedModProject;
        var index = selected is null ? -1 : ModProjects.IndexOf(selected);

        RelatedModProjects.ReplaceAll(RelatedItems(ModProjects, index));
        OnPropertyChanged(nameof(HasRelatedModProjects));
    }

    [RelayCommand]
    private void CloseRemoteVersionDetail()
    {
        _loaderVersionsCts?.Cancel();
        _fabricApiCts?.Cancel();
        _activeLoaderVersionId = null;
        IsRemoteVersionDetailOpen = false;
    }

    private void ResetLoaderGroups()
    {
        ClearLoaderSelection();

        _fabricApiCts?.Cancel();
        _fabricApiLoadedVersion = null;
        _selectedFabricApiVersion = null;
        FabricApiGroup.SelectedVersionText = null;
        FabricApiGroup.Versions.Clear();
        FabricApiGroup.IsAvailable = false;
        FabricApiGroup.IsExpanded = false;
        FabricApiGroup.IsLoading = false;
        FabricApiGroup.ErrorText = null;
        FabricApiGroup.StatusText = "加载中…";

        foreach (var group in LoaderGroups)
        {
            group.Versions.Clear();
            group.IsAvailable = false;
            group.IsExpanded = false;
            group.IsLoading = true;
            group.ErrorText = null;
            group.StatusText = "加载中…";
        }

        RebuildLoaderVersionRows();
    }

    /// <summary>
    /// 把分组与展开状态展平成列表行：分组标题始终显示，展开后显示版本行或空占位。
    /// </summary>
    private void RebuildLoaderVersionRows()
    {
        var rows = new List<LoaderVersionListRowViewModel>();

        foreach (var group in LoaderGroups)
        {
            AppendGroupRows(rows, group);

            // Fabric API 与加载器分组同级，紧跟在 Fabric 分组后面。
            if (group.Key == "fabric" && IsFabricApiGroupVisible)
                AppendGroupRows(rows, FabricApiGroup);
        }

        LoaderVersionRows.ReplaceAll(rows);
    }

    private void AppendGroupRows(List<LoaderVersionListRowViewModel> rows, LoaderVersionGroupViewModel group)
    {
        rows.Add(CreateLoaderHeaderRow(group));

        if (!group.IsExpanded) return;

        if (group.Versions.Count == 0)
        {
            rows.Add(LoaderVersionListRowViewModel.CreateEmpty(group, DescribeLoaderEmpty(group)));
            return;
        }

        foreach (var version in group.Versions)
            rows.Add(LoaderVersionListRowViewModel.CreateVersion(version));
    }

    private LoaderVersionListRowViewModel CreateLoaderHeaderRow(LoaderVersionGroupViewModel group)
        => LoaderVersionListRowViewModel.CreateHeader(group, () => ToggleLoaderGroup(group));

    private static string DescribeLoaderEmpty(LoaderVersionGroupViewModel group)
    {
        if (group.IsLoading) return "正在获取版本列表…";
        if (!string.IsNullOrEmpty(group.ErrorText)) return $"获取失败：{group.ErrorText}";
        return $"该版本没有可用的 {group.DisplayName} 版本";
    }

    /// <summary>
    /// 只增删该分组的内容行，不重建整个列表，避免切换展开时滚动位置跳回顶部。
    /// </summary>
    private void UpdateLoaderGroupRows(LoaderVersionGroupViewModel group, bool contentChanged = true)
    {
        var headerIndex = -1;
        for (var index = 0; index < LoaderVersionRows.Count; index++)
        {
            if (LoaderVersionRows[index].IsHeader &&
                ReferenceEquals(LoaderVersionRows[index].Group, group))
            {
                headerIndex = index;
                break;
            }
        }

        if (headerIndex < 0)
        {
            RebuildLoaderVersionRows();
            return;
        }

        LoaderVersionRows[headerIndex] = CreateLoaderHeaderRow(group);

        // 后台刷新拿到与当前相同的数据时只换标题，不动内容行。
        if (!contentChanged) return;

        var contentCount = 0;
        while (headerIndex + 1 + contentCount < LoaderVersionRows.Count &&
               !LoaderVersionRows[headerIndex + 1 + contentCount].IsHeader)
        {
            contentCount++;
        }

        if (contentCount > 0)
            LoaderVersionRows.RemoveRange(headerIndex + 1, contentCount);

        if (!group.IsExpanded) return;

        if (group.Versions.Count == 0)
        {
            LoaderVersionRows.Insert(
                headerIndex + 1,
                LoaderVersionListRowViewModel.CreateEmpty(group, DescribeLoaderEmpty(group)));
            return;
        }

        LoaderVersionRows.InsertRange(
            headerIndex + 1,
            group.Versions.Select(version =>
                LoaderVersionListRowViewModel.CreateVersion(version)));
    }

    private bool IsLoaderSelected(string? loaderKey, string? version)
    {
        if (version is null) return false;

        if (string.Equals(loaderKey, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase))
            return string.Equals(_selectedFabricApiVersion, version, StringComparison.Ordinal);

        return loaderKey is not null &&
               string.Equals(_selectedLoaderKey, loaderKey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_selectedLoaderVersion, version, StringComparison.Ordinal);
    }

    private void SelectLoaderVersion(LoaderVersionItemViewModel item)
    {
        // Fabric API 的选择独立于加载器单选。
        if (string.Equals(item.LoaderKey, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase))
        {
            SelectFabricApiVersion(item);
            return;
        }

        // 加载器互不兼容：再次点击已选中的行即取消，只能保留一个选择。
        if (item.IsSelected)
        {
            ClearLoaderSelection();
            return;
        }

        var wasFabricApiVisible = IsFabricApiGroupVisible;

        _selectedLoaderKey = item.LoaderKey;
        _selectedLoaderVersion = item.Version;

        foreach (var group in LoaderGroups)
        {
            group.SelectedVersionText = string.Equals(group.Key, item.LoaderKey, StringComparison.OrdinalIgnoreCase)
                ? item.Version
                : null;

            foreach (var version in group.Versions)
                version.IsSelected = ReferenceEquals(version, item);
        }

        NotifyLoaderSelectionChanged();
        RefreshDefaultVersionName();

        if (IsFabricApiGroupVisible != wasFabricApiVisible)
            OnFabricApiVisibilityChanged(SelectedRemoteVersion?.Id);

        // 选完就收起该分组，标题旁已经能看到选中的版本。
        CollapseGroup(item.LoaderKey);
        RefreshLoaderGroupHeaders();
    }

    private void ClearLoaderSelection()
    {
        var wasFabricApiVisible = IsFabricApiGroupVisible;

        _selectedLoaderKey = null;
        _selectedLoaderVersion = null;

        foreach (var group in LoaderGroups)
        {
            group.SelectedVersionText = null;

            foreach (var version in group.Versions)
                version.IsSelected = false;
        }

        NotifyLoaderSelectionChanged();
        RefreshDefaultVersionName();

        if (wasFabricApiVisible)
            OnFabricApiVisibilityChanged(SelectedRemoteVersion?.Id);
        else
            RefreshLoaderGroupHeaders();
    }

    /// <summary>重画所有分组的标题行（已选版本可能已变化）。</summary>
    private void RefreshLoaderGroupHeaders()
    {
        foreach (var group in LoaderGroups)
            UpdateLoaderGroupRows(group, contentChanged: false);

        if (IsFabricApiGroupVisible)
            UpdateLoaderGroupRows(FabricApiGroup, contentChanged: false);
    }

    /// <summary>收起某个分组；已收起时只刷新标题以显示新的选中版本。</summary>
    private void CollapseGroup(string loaderKey)
    {
        var group = string.Equals(loaderKey, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase)
            ? FabricApiGroup
            : LoaderGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, loaderKey, StringComparison.OrdinalIgnoreCase));

        if (group is null) return;

        if (group.IsExpanded)
        {
            group.IsExpanded = false;
            UpdateLoaderGroupRows(group);
            return;
        }

        UpdateLoaderGroupRows(group, contentChanged: false);
    }

    /// <summary>Fabric API 分组的显示状态发生变化：出现时加载列表，隐藏时清除选择。</summary>
    private void OnFabricApiVisibilityChanged(string? minecraftVersion)
    {
        OnPropertyChanged(nameof(IsFabricApiGroupVisible));

        if (IsFabricApiGroupVisible && !string.IsNullOrEmpty(minecraftVersion))
            PrepareFabricApiGroup(minecraftVersion);
        else
            ClearFabricApiSelection();

        RebuildLoaderVersionRows();
    }

    /// <summary>展开 Fabric API 分组；同一游戏版本只请求一次。</summary>
    private void PrepareFabricApiGroup(string minecraftVersion)
    {
        FabricApiGroup.IsExpanded = true;

        if (string.Equals(_fabricApiLoadedVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase))
            return;

        _fabricApiLoadedVersion = minecraftVersion;
        _selectedFabricApiVersion = null;
        FabricApiGroup.Versions.Clear();
        FabricApiGroup.IsAvailable = false;
        FabricApiGroup.IsLoading = true;
        FabricApiGroup.ErrorText = null;
        FabricApiGroup.StatusText = "加载中…";

        _fabricApiCts?.Cancel();
        _fabricApiCts?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _fabricApiCts = cts;

        _ = LoadFabricApiGroupSafeAsync(minecraftVersion, cts.Token);
    }

    private async Task LoadFabricApiGroupSafeAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            await LoadLoaderGroupAsync(
                FabricApiGroup, minecraftVersion, _loaderVersionsGeneration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FabricApiGroup.IsLoading = false;
            FabricApiGroup.ErrorText = exception.Message;
            FabricApiGroup.StatusText = "获取失败";
            UpdateLoaderGroupRows(FabricApiGroup);
        }
    }

    private void SelectFabricApiVersion(LoaderVersionItemViewModel item)
    {
        if (item.IsSelected)
        {
            _selectedFabricApiVersion = null;
            FabricApiGroup.SelectedVersionText = null;

            foreach (var version in FabricApiGroup.Versions)
                version.IsSelected = false;
        }
        else
        {
            _selectedFabricApiVersion = item.Version;
            FabricApiGroup.SelectedVersionText = item.Version;

            foreach (var version in FabricApiGroup.Versions)
                version.IsSelected = ReferenceEquals(version, item);
        }

        NotifyFabricApiSelectionChanged();
        CollapseGroup(FabricApiGroup.Key);
    }

    private void ClearFabricApiSelection()
    {
        _selectedFabricApiVersion = null;
        FabricApiGroup.SelectedVersionText = null;

        foreach (var version in FabricApiGroup.Versions)
            version.IsSelected = false;

        NotifyFabricApiSelectionChanged();
    }

    private void NotifyFabricApiSelectionChanged()
    {
        OnPropertyChanged(nameof(CanDownloadSelected));
    }

    private void NotifyLoaderSelectionChanged()
    {
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(DownloadIconName));
        OnPropertyChanged(nameof(HasDuplicateVersionName));
    }

    private void RefreshDefaultVersionName()
    {
        if (_versionNameEdited) return;

        SetVersionName(VersionNaming.BuildDefault(
            SelectedRemoteVersion?.Id ?? string.Empty,
            _selectedLoaderKey,
            _selectedLoaderVersion));
    }

    private void SetVersionName(string value)
    {
        _updatingVersionName = true;
        VersionName = value;
        _updatingVersionName = false;
    }

    [RelayCommand]
    private void ResetVersionName()
    {
        _versionNameEdited = false;
        RefreshDefaultVersionName();
    }

    private static string GetLoaderDisplayName(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => "Forge",
        "fabric" => "Fabric",
        "neoforge" => "NeoForge",
        _ => loader,
    };

    private void ToggleLoaderGroup(LoaderVersionGroupViewModel group)
    {
        group.IsExpanded = !group.IsExpanded;
        UpdateLoaderGroupRows(group);
    }

    private async Task LoadLoaderVersionsAsync(RemoteVersionItemViewModel item)
    {
        _loaderVersionsCts?.Cancel();
        _loaderMirrorDirty = false;

        var generation = ++_loaderVersionsGeneration;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loaderVersionsCts = cts;

        ResetLoaderGroups();

        try
        {
            await Task.WhenAll(LoaderGroups.Select(group =>
                LoadLoaderGroupAsync(group, item.Id, generation, cts.Token)));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>读取加载器版本缓存；文件读取与反序列化都在线程池完成。</summary>
    private static Task<(LoaderVersionResult Result, DateTimeOffset UpdatedAt)?> LoadCachedLoaderVersionsAsync(
        string loader,
        string minecraftVersion,
        CancellationToken cancellationToken)
        => Task.Run<(LoaderVersionResult, DateTimeOffset)?>(() =>
        {
            if (!LoaderVersionStore.TryLoad(loader, minecraftVersion, out var result, out var updatedAt))
                return null;

            return (result, updatedAt);
        }, cancellationToken);

    private async Task LoadLoaderGroupAsync(
        LoaderVersionGroupViewModel group,
        string minecraftVersion,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1) 先用本地缓存（72 小时内）秒开，不等网络。
            //    文件读取与 JSON 解析放线程池，避免点开详情页时卡住界面。
            var cached = await LoadCachedLoaderVersionsAsync(group.Key, minecraftVersion, cancellationToken);
            if (cached is { } cache && LoaderVersionStore.IsFresh(cache.UpdatedAt))
            {
                ApplyLoaderGroupResult(group, cache.Result);
            }

            // 2) 无论是否有缓存，都继续访问网络获取最新数据（按分组各自的源）。
            var mirror = ResolveLoaderMirror(group.Key);
            var result = await LoaderVersionCatalog.LoadAsync(
                group.Key, minecraftVersion, mirror, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // 切换下载源或版本时旧请求可能刚好返回，丢弃过期结果。
            if (generation != _loaderVersionsGeneration) return;

            ApplyLoaderGroupResult(group, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (generation != _loaderVersionsGeneration) return;

            group.IsLoading = false;
            group.ErrorText = exception.Message;

            if (group.Versions.Count > 0)
            {
                // 已有缓存时保留内容，只提示本次刷新失败。
                group.IsAvailable = true;
                group.StatusText = $"{group.Versions.Count} 个版本";
                UpdateLoaderGroupRows(group, contentChanged: false);
            }
            else
            {
                group.IsAvailable = false;
                group.StatusText = "获取失败";
                UpdateLoaderGroupRows(group);
            }
        }
    }

    private static int VersionTypeOrder(string type) => type switch
    {
        "release" => 0,
        "snapshot" => 1,
        "old_beta" => 2,
        "old_alpha" => 3,
        _ => 4,
    };

    private static string VersionTypeTitle(string type) => type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => "其他",
    };

    private bool MatchesRemoteVersionType(RemoteVersionItemViewModel item) => RemoteVersionType switch
    {
        "正式版" => item.Type == "release",
        "快照" => item.Type == "snapshot",
        "远古" => item.Type is "old_beta" or "old_alpha",
        _ => true,
    };

    private void UpdateRemoteInstalledStatus()
    {
        if (_allRemoteVersions.Count == 0) return;

        var installed = new HashSet<string>(Versions.Select(version => version.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allRemoteVersions)
            item.IsInstalled = installed.Contains(item.Id);

        UpdateRemoteVersionsStatus();
    }

    private void UpdateRemoteVersionsStatus()
    {
        if (_allRemoteVersions.Count == 0)
        {
            if (!IsRemoteVersionsLoading) RemoteVersionsStatus = "尚未加载版本清单";
            return;
        }

        var installedCount = _allRemoteVersions.Count(item => item.IsInstalled);
        RemoteVersionsStatus = _filteredRemoteVersionCount == _allRemoteVersions.Count
            ? $"共 {_allRemoteVersions.Count} 个版本 · 已安装 {installedCount} 个"
            : $"显示 {_filteredRemoteVersionCount} / {_allRemoteVersions.Count} 个版本 · 已安装 {installedCount} 个";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadSelectedAsync()
    {
        if (!CanDownloadSelected || SelectedRemoteVersion is null) return;

        if (HasDuplicateVersionName)
        {
            ShowNotice(
                "无法下载",
                $"版本名 “{VersionNaming.Normalize(VersionName)}” 已存在，请换一个名称。");
            return;
        }

        if (_selectedLoaderKey is not null && _selectedLoaderVersion is not null)
            await InstallSelectedLoaderAsync();
        else
            await DownloadSelectedVersionAsync();
    }

    /// <summary>校验并规范化版本名；名称冲突时直接给出提示。</summary>
    private bool TryResolveVersionName(out string name)
    {
        name = VersionNaming.Normalize(VersionName);

        if (name.Length == 0)
        {
            Status = "版本名不能为空";
            return false;
        }

        if (Directory.Exists(Path.Combine(MinecraftDirectory, "versions", name)))
        {
            Status = $"版本名 “{name}” 已存在，请换一个名称";
            return false;
        }

        return true;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadSelectedVersionAsync()
    {
        if (!CanDownloadSelectedVersion || SelectedRemoteVersion is null) return;

        var item = SelectedRemoteVersion;

        if (!TryResolveVersionName(out var desiredName))
            return;

        var task = TryCreateTask(desiredName, DownloadTaskKind.Version, item.Id);
        if (task is null) return;

        AddVersionSteps(task);
        task.StatusText = "正在准备下载...";

        CurrentPage = AppPage.Download;
        EnterTaskSection(leaveVersionDetail: true);
        Status = $"正在下载 Minecraft {item.Id}...";
        LauncherLog.Info($"开始下载版本 {item.Id}（目标名称 {desiredName}）");

        var stage = MinecraftDownloadStage.Preparing;

        try
        {
            // 版本列表与文件下载共用「游戏版本」源。
            var mirror = ResolveGameMirror();

            var lastUiTicks = 0L;
            var progress = new Progress<MinecraftVersionDownloadProgress>(report =>
            {
                // 准备阶段没有百分比，直接把当前在拉的东西写在卡片上，避免看上去卡死。
                if (report.Stage == MinecraftDownloadStage.Preparing && report.CompletedFiles == 0)
                {
                    task.StatusText = $"正在获取{report.CurrentFile}…";
                    return;
                }

                var stageChanged = stage != report.Stage;
                var now = Environment.TickCount64;
                var isFinal = report.Percent is >= 100 ||
                              (report.TotalFiles > 0 && report.CompletedFiles >= report.TotalFiles);

                // 高并发下载会每文件上报一次，限制 UI 刷新频率，但最终状态一定更新。
                if (!stageChanged && !isFinal && now - lastUiTicks < 100)
                    return;

                lastUiTicks = now;

                if (stageChanged)
                {
                    stage = report.Stage;
                    ApplyVersionStage(task, stage);
                }

                task.CompletedFiles = report.CompletedFiles;
                task.TotalFiles = report.TotalFiles;

                var percent = report.Percent is double value ? $"（{value:0}%）" : string.Empty;
                var megabytes = report.BytesDownloaded / 1024.0 / 1024.0;
                var speed = task.SpeedMeter.Feed(report.BytesDownloaded);
                var speedText = speed.Length > 0 ? $"，{speed}" : string.Empty;

                // 并发下载时进度可能因文件完成顺序出现瞬时回落，进度条只允许前进。
                if (report.Percent is double currentPercent && currentPercent > task.Percent)
                    task.Percent = Math.Min(currentPercent, 99);

                if (stage == MinecraftDownloadStage.Files && report.Percent is double filePercent)
                    task.Steps[1].Detail = $"{filePercent:0}%";

                task.StatusText = $"{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}";
                Status = report.TotalFiles > 0
                    ? $"正在下载 {item.Id}：{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}{percent}"
                    : $"正在下载 {item.Id}...";
                NotifyDownloadStats();
            });

            // 下载本身是纯 IO / 校验，放到线程池：其中「文件已存在 → 校验并跳过」的路径
            // 几乎全是同步工作，留在 UI 线程会让整个窗口卡住（进度和输入都没反应）。
            var result = await Task.Run(
                () => MinecraftVersionDownloader.DownloadAsync(
                    MinecraftDirectory,
                    item.Entry,
                    progress,
                    new MinecraftVersionDownloadOptions
                    {
                        Mirror = mirror,
                    },
                    task.Token),
                task.Token);

            // 收尾（改名、重扫版本列表）也算任务时间，进度先停在 99，真正完成才打满。
            task.Percent = 99;
            NotifyDownloadStats();

            var finalId = VersionNaming.RenameInstalledVersion(
                MinecraftDirectory, result.VersionId, desiredName);

            // 只重扫版本：下载不会改变系统里的 Java，重新发现只会把收尾拖长。
            await RefreshCoreAsync(refreshJava: false);

            var local = Versions.FirstOrDefault(version =>
                string.Equals(version.Id, finalId, StringComparison.OrdinalIgnoreCase));
            if (local is not null) SelectedVersion = local;

            UpdateRemoteInstalledStatus();
            Status = $"版本 {finalId} 下载完成";
            LauncherLog.Info($"版本 {finalId} 下载完成");
            task.Percent = 100;
            FinishTask(task, DownloadTaskState.Completed, $"版本 {finalId} 下载完成");
        }
        catch (OperationCanceledException)
        {
            Status = "版本下载已取消";
            LauncherLog.Warn($"版本 {item.Id} 下载已取消");
            FinishTask(task, DownloadTaskState.Canceled, "下载已取消");
        }
        catch (Exception exception)
        {
            Status = "版本下载失败";
            LauncherLog.Error($"版本 {item.Id} 下载失败", exception);
            ShowDownloadError($"下载 Minecraft {item.Id}", exception);
            FinishTask(task, DownloadTaskState.Failed, "下载失败");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task InstallSelectedLoaderAsync()
    {
        if (SelectedRemoteVersion is null ||
            _selectedLoaderKey is null ||
            _selectedLoaderVersion is null ||
            IsBusy || IsLaunching)
        {
            return;
        }

        var item = SelectedRemoteVersion;
        var loader = _selectedLoaderKey;
        var loaderVersion = _selectedLoaderVersion;
        var displayName = GetLoaderDisplayName(loader);

        if (!TryResolveVersionName(out var desiredName))
            return;

        var task = TryCreateTask(desiredName, DownloadTaskKind.Version, item.Id);
        if (task is null) return;

        AddVersionSteps(task);
        task.StatusText = "正在准备安装...";

        CurrentPage = AppPage.Download;
        EnterTaskSection(leaveVersionDetail: true);
        Status = $"正在安装 {displayName} {loaderVersion}...";
        LauncherLog.Info($"开始安装 {displayName} {loaderVersion} → {desiredName}");

        // 原版文件直接下载到最终版本目录（versions/{desiredName}），
        // 加载器安装只把配置合并进同一份 JSON，不会多出一个普通版本。
        const double vanillaPhase = 65.0;
        var installerRunning = false;
        var stage = MinecraftDownloadStage.Preparing;

        var creepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        creepTimer.Tick += (_, _) =>
        {
            if (!installerRunning) return;
            if (task.Percent >= 97) return;

            // 安装器不提供百分比，用「逐步逼近 97%」推进：
            // 刚开始明显往前走，越接近上限越慢，不会像固定步长那样一分钟只动几个点。
            task.Percent = Math.Min(97, task.Percent + Math.Max(0.4, (97 - task.Percent) * 0.06));

            // 侧边栏的总进度也来自 task.Percent，这里不通知会和卡片进度条对不上。
            NotifyDownloadStats();
        };
        creepTimer.Start();

        try
        {
            // 每次安装都重新铺一遍原版文件：目标目录是新的，库与 assets 已存在时会被校验后跳过。
            var lastVanillaTicks = 0L;
            var vanillaProgress = new Progress<MinecraftVersionDownloadProgress>(report =>
            {
                if (report.Stage == MinecraftDownloadStage.Preparing && report.CompletedFiles == 0)
                {
                    task.StatusText = $"正在获取{report.CurrentFile}…";
                    return;
                }

                var stageChanged = stage != report.Stage;
                var now = Environment.TickCount64;
                var isFinal = report.Percent is >= 100 ||
                              (report.TotalFiles > 0 && report.CompletedFiles >= report.TotalFiles);

                if (!stageChanged && !isFinal && now - lastVanillaTicks < 100)
                    return;

                lastVanillaTicks = now;

                if (stageChanged)
                {
                    stage = report.Stage;
                    ApplyVersionStage(task, stage);
                }

                if (report.Percent is double percent)
                {
                    var mapped = percent * vanillaPhase / 100.0;
                    if (mapped > task.Percent) task.Percent = Math.Min(mapped, 99);

                    // 和普通版本下载一致，让“游戏文件”这一步也带上百分比。
                    if (stage == MinecraftDownloadStage.Files && task.Steps.Count > 1)
                        task.Steps[1].Detail = $"{percent:0}%";
                }

                if (report.BytesDownloaded > 0)
                    task.SpeedMeter.Feed(report.BytesDownloaded);

                task.CompletedFiles = report.CompletedFiles;
                task.TotalFiles = report.TotalFiles;
                task.StatusText = report.TotalFiles > 0
                    ? $"正在下载原版文件 {report.CompletedFiles}/{report.TotalFiles}..."
                    : "正在下载原版文件...";
                Status = task.StatusText;
                NotifyDownloadStats();
            });

            // 同理放到线程池：这一轮经常几乎全是「已存在 → 校验 → 跳过」的同步工作。
            await Task.Run(
                () => MinecraftVersionDownloader.DownloadAsync(
                    MinecraftDirectory,
                    item.Entry,
                    vanillaProgress,
                    new MinecraftVersionDownloadOptions
                    {
                        Mirror = ResolveGameMirror(),
                        TargetVersionId = desiredName,
                    },
                    task.Token),
                task.Token);

            // 原版文件下载结束后重新计量，安装器阶段的字节从 0 开始。
            task.SpeedMeter.Reset();
            NotifyDownloadStats();

            string? javaPath = null;
            var javaMajor = 0;

            if (loader is "forge" or "neoforge")
            {
                // 原版 json 现在就在目标版本目录里，用它的 javaVersion 选安装器用的 Java。
                var requiredJava = ReadRequiredJavaMajor(desiredName, loader);
                var runtimes = JavaRuntimes.Count > 0
                    ? (IEnumerable<JavaRuntime>)JavaRuntimes
                    : await JavaLocator.DiscoverAsync(
                        MinecraftDirectory, _settings.JavaPath, task.Token);

                var java = SelectedJava is not null && SelectedJava.MajorVersion >= requiredJava
                    ? SelectedJava
                    : JavaLocator.SelectBest(runtimes, requiredJava);

                if (java is null)
                {
                    throw new InvalidOperationException(
                        $"未找到可运行 {displayName} 安装器的 Java（需要 Java {requiredJava}+）。");
                }

                javaPath = java.Path;
                javaMajor = java.MajorVersion;
            }

            var lastInstallTicks = 0L;
            var lastInstallBytes = 0L;
            var lastInstallText = string.Empty;
            var installProgress = new Progress<LoaderInstallProgress>(report =>
            {
                // 阶段文本变化时记一笔，事后能从日志看出每一步的进度值。
                if (report.Text != lastInstallText)
                {
                    lastInstallText = report.Text;
                    LauncherLog.Info($"安装阶段：{report.Text}（{report.Percent?.ToString("0.0") ?? "无百分比"}）");
                }
                // 下载回调很密集，限制界面刷新频率；阶段切换与开始/结束阶段一定刷新。
                var now = Environment.TickCount64;
                var isPhaseChange = report.Percent is null or >= 100;
                if (!isPhaseChange && now - lastInstallTicks < 100) return;
                lastInstallTicks = now;

                if (stage != report.Stage)
                {
                    stage = report.Stage;
                    ApplyVersionStage(task, stage);
                }

                // 安装器依赖与安装器 jar 的下载量累计上报，用于显示实时速度。
                if (report.BytesDownloaded is long bytes)
                {
                    // 每个阶段重新从 0 开始计数，先重置测速，否则速度会一瞬显示 0。
                    if (bytes < lastInstallBytes) task.SpeedMeter.Reset();
                    lastInstallBytes = bytes;
                    task.SpeedMeter.Feed(bytes);
                }

                if (report.Percent is double percent)
                {
                    installerRunning = false;
                    var mapped = vanillaPhase + percent * (100 - vanillaPhase) / 100.0;
                    if (mapped > task.Percent) task.Percent = Math.Min(mapped, 99);
                }
                else
                {
                    // Forge / NeoForge 安装器不提供百分比，由定时器推进进度条。
                    installerRunning = true;
                    task.SpeedMeter.Reset();
                }

                task.IsIndeterminate = false;

                task.StatusText = report.Text;
                Status = report.Text;
                NotifyDownloadStats();
            });

            // 安装器输出很密集，限制刷新频率，只展示最近一行。
            var lastLogTicks = 0L;
            var installLog = new Progress<string>(line =>
            {
                var now = Environment.TickCount64;
                if (now - lastLogTicks < 200) return;
                lastLogTicks = now;

                task.StatusText = line;
                Status = line;
            });

            // 安装过程中有解压安装器、解析 JSON、拷贝库等同步工作，同样放线程池，避免界面卡住。
            var result = await Task.Run(
                () => LoaderInstaller.InstallAsync(
                    MinecraftDirectory,
                    item.Id,
                    loader,
                    loaderVersion,
                    new LoaderInstallOptions
                    {
                        FabricApiVersion = _selectedFabricApiVersion,
                        IsolateVersion = VersionIsolationDetector.Detect(
                            MinecraftDirectory,
                            desiredName,
                            item.Entry.Type,
                            modable: true,
                            _settings.DefaultIsolation).IsIsolated,
                        Mirror = ResolveLoaderMirror(loader),
                        JavaPath = javaPath,
                        JavaMajor = javaMajor,
                        VersionName = desiredName,
                        Progress = installProgress,
                        Log = installLog,
                    },
                    task.Token),
                task.Token);

            task.Percent = 99;
            NotifyDownloadStats();

            // 只重扫版本：安装加载器不会改变系统里的 Java。
            await RefreshCoreAsync(refreshJava: false);

            var local = Versions.FirstOrDefault(version =>
                string.Equals(version.Id, result.VersionId, StringComparison.OrdinalIgnoreCase));
            if (local is not null) SelectedVersion = local;

            Status = $"{displayName} {loaderVersion} 安装完成";
            LauncherLog.Info($"{displayName} {loaderVersion} 安装完成 → {result.VersionId}");
            task.Percent = 100;
            FinishTask(task, DownloadTaskState.Completed, $"{displayName} {loaderVersion} 安装完成");
        }
        catch (OperationCanceledException)
        {
            Status = "加载器安装已取消";
            LauncherLog.Warn($"{displayName} {loaderVersion} 安装已取消");
            FinishTask(task, DownloadTaskState.Canceled, "安装已取消");
        }
        catch (Exception exception)
        {
            Status = $"{displayName} {loaderVersion} 安装失败";
            LauncherLog.Error($"{displayName} {loaderVersion} 安装失败", exception);
            ShowDownloadError($"安装 {displayName} {loaderVersion}", exception);
            FinishTask(task, DownloadTaskState.Failed, "安装失败");
        }
        finally
        {
            creepTimer.Stop();
        }
    }
}
