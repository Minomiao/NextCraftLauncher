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
    // ---- Mod 下载 ----

    private CancellationTokenSource? _modSearchCts;
    private CancellationTokenSource? _modDetailCts;
    // 只在第一次进入内容页时套用当前版本的筛选并自动搜索，之后保留用户的搜索状态。
    private bool _modPageInitialized;
    // 当前内容页实际展示的类型（Mod / 材质包）。
    private ModProjectType _modPageType = ModProjectType.Mod;
    // 当前打开的详情页属于哪种类型，决定下载落点与推荐匹配规则。
    private ModProjectType _modDetailType = ModProjectType.Mod;
    // 当前结果列表对应的搜索条件，翻页时沿用这一组，避免筛选未生效时错位。
    private (ModProjectType Type, ModPlatform? Platform, string Query, string? GameVersion, string? Loader)?
        _modActiveQuery;

    public RangeObservableCollection<ModProjectItemViewModel> ModProjects { get; } = new();

    /// <summary>项目详情页顶部的同批搜索结果（最多 6 个，不含当前项目）。</summary>
    public RangeObservableCollection<ModProjectItemViewModel> RelatedModProjects { get; } = new();

    public bool HasRelatedModProjects => RelatedModProjects.Count > 0;

    /// <summary>模组详情页的扁平行：可折叠分组标题 + 文件行。</summary>
    public RangeObservableCollection<ModVersionListRowViewModel> ModVersionRows { get; } = new();

    /// <summary>当前展开的分组（加载器 + 发布类型），跨筛选保留。</summary>
    private readonly HashSet<string> _expandedModVersionGroups = new(StringComparer.Ordinal);

    /// <summary>模组详情页当前选中的文件；选中后底部出现下载按钮。</summary>
    [ObservableProperty] private ModVersionItemViewModel? _selectedModFile;

    /// <summary>选中行所属的目标版本（推荐页才有，其它页面为 null）。</summary>
    [ObservableProperty] private VersionItemViewModel? _selectedModFileTarget;

    public bool HasSelectedModFile => SelectedModFile is not null;

    public string SelectedModFileTitle => SelectedModFile?.Title ?? string.Empty;

    /// <summary>选中文件将落入的目录：带目标版本时给游戏版本的 mods，否则给本机的 mod 库。</summary>
    public string SelectedModFileFolder
    {
        get
        {
            if (SelectedModFile is not { } file) return string.Empty;

            if (SelectedModFileTarget is { } target)
            {
                return ResolveModsDirectory(target.Summary, file.Version, _modDetailType)
                       ?? "当前版本不兼容，下载时选择位置";
            }

            return UserContentDirectory(_modDetailType);
        }
    }

    partial void OnSelectedModFileChanged(ModVersionItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedModFile));
        OnPropertyChanged(nameof(SelectedModFileTitle));
        OnPropertyChanged(nameof(SelectedModFileFolder));
    }

    partial void OnSelectedModFileTargetChanged(VersionItemViewModel? value)
        => OnPropertyChanged(nameof(SelectedModFileFolder));

    /// <summary>选中一行：只高亮这一行，并记住它携带的目标版本。</summary>
    private void SelectModFileRow(ModVersionListRowViewModel row)
    {
        foreach (var item in ModVersionRows)
            item.IsSelected = ReferenceEquals(item, row);

        SelectedModFile = row.Version;
        SelectedModFileTarget = row.Target;
    }

    [RelayCommand]
    private Task DownloadSelectedModFileAsync()
        => SelectedModFile is { } file
            ? DownloadModVersionAsync(file, SelectedModFileTarget)
            : Task.CompletedTask;

    /// <summary>搜索时可选的原版版本；在打开 Mod 页时用本地版本填充。</summary>
    public ObservableCollection<string> ModGameVersions { get; } = new();

    public IReadOnlyList<string> ModSources { get; } = new[] { "全部", "CurseForge", "Modrinth" };

    /// <summary>版本条当前是否选中「推荐」（按已安装的游戏版本匹配）。</summary>
    [ObservableProperty] private bool _isModRecommendSelected;

    public IReadOnlyList<string> ModLoaderOptions { get; } = new[] { "全部", "Forge", "Fabric", "NeoForge", "Quilt" };

    [ObservableProperty] private string _modSearchText = string.Empty;
    [ObservableProperty] private int _selectedModSourceIndex;
    [ObservableProperty] private int _selectedModGameVersionIndex;
    [ObservableProperty] private int _selectedModLoaderIndex;
    [ObservableProperty] private bool _isModsLoading;
    [ObservableProperty] private int _modPage = 1;
    [ObservableProperty] private int _modTotalPages = 1;
    [ObservableProperty] private string _modStatus = "输入关键字搜索 Mod";
    [ObservableProperty] private bool _isModDetailOpen;
    [ObservableProperty] private ModProjectItemViewModel? _selectedModProject;
    [ObservableProperty] private string _modDetailTitle = string.Empty;
    [ObservableProperty] private string _modDetailDescription = string.Empty;
    [ObservableProperty] private string _modDetailStatus = string.Empty;

    /// <summary>由视图提供：当前版本不兼容时让用户选择保存位置（参数：默认文件名、默认目录）。</summary>
    public Func<string, string, Task<string?>>? ModSaveFilePicker { get; set; }

    /// <summary>翻页控件：上一页 / 页码/总页 / 下一页。</summary>
    public string ModPageText => $"{ModPage} / {ModTotalPages}";

    public bool ShowModPager => ModTotalPages > 1;

    public bool CanGoPreviousModPage => ModPage > 1;

    public bool CanGoNextModPage => ModPage < ModTotalPages;

    partial void OnModPageChanged(int value) => RaiseModPagerState();

    partial void OnModTotalPagesChanged(int value) => RaiseModPagerState();

    private void RaiseModPagerState()
    {
        OnPropertyChanged(nameof(ModPageText));
        OnPropertyChanged(nameof(ShowModPager));
        OnPropertyChanged(nameof(CanGoPreviousModPage));
        OnPropertyChanged(nameof(CanGoNextModPage));
    }

    private ModPlatform? SelectedModPlatform => SelectedModSourceIndex switch
    {
        1 => ModPlatform.CurseForge,
        2 => ModPlatform.Modrinth,
        _ => null,
    };

    private string? SelectedModGameVersionFilter =>
        SelectedModGameVersionIndex > 0 && SelectedModGameVersionIndex < ModGameVersions.Count
            ? ModGameVersions[SelectedModGameVersionIndex]
            : null;

    private string? SelectedModLoaderFilter =>
        SelectedModLoaderIndex > 0 && SelectedModLoaderIndex < ModLoaderOptions.Count
            ? ModLoaderOptions[SelectedModLoaderIndex]
            : null;

    /// <summary>用本地版本填充搜索筛选；默认停在「全部」，不自动套用当前选中版本。</summary>
    private void UpdateModFiltersFromSelection()
    {
        var versions = Versions
            .Select(version => version.Summary.ClientVersion)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 清空集合会让 ComboBox 把选中项抬成 -1；而属性的默认值是 0，
        // 直接再赋 0 不会产生变更通知，界面就会停在“无选择”的空白状态。
        // 先打到 -1，填完再回 0，强制刷新一次。
        SelectedModGameVersionIndex = -1;
        SelectedModLoaderIndex = -1;

        ModGameVersions.Clear();
        ModGameVersions.Add("全部");
        foreach (var value in versions)
            ModGameVersions.Add(value);

        SelectedModGameVersionIndex = 0;
        SelectedModLoaderIndex = 0;
    }

    /// <summary>
    /// 进入 Mod / 材质包页：类型变了就清空上一个类型的状态，否则保留上次的搜索结果。
    /// </summary>
    private void EnterContentSection(ModProjectType type)
    {
        if (_modPageType != type)
        {
            _modPageType = type;

            _modSearchCts?.Cancel();
            _modActiveQuery = null;
            _modPageInitialized = false;

            ModSearchText = string.Empty;
            ModProjects.Clear();
            ModVersionRows.Clear();
            _allModVersions.Clear();
            ModVersionFilters.Clear();
            _expandedModVersionGroups.Clear();
            _selectedModVersionFilter = null;
            IsModDetailOpen = false;
            SelectedModProject = null;
            SelectedModFile = null;
            SelectedModFileTarget = null;
            ModPage = 1;
            ModTotalPages = 1;
            ModStatus = type == ModProjectType.ResourcePack
                ? "输入关键字搜索材质包"
                : "输入关键字搜索 Mod";

            OnPropertyChanged(nameof(ShowModVersionFilters));
            OnPropertyChanged(nameof(IsModSearchLoaderVisible));
            OnPropertyChanged(nameof(ModSearchPlaceholder));
        }

        if (!_modPageInitialized)
        {
            // 没预取过（例如启动时预取被取消）：进页面时补一次。
            _modPageInitialized = true;
            UpdateModFiltersFromSelection();
            _ = ShowModsAsync();
        }
        else if (ModProjects.Count == 0 && !IsModsLoading)
        {
            // 预取失败时列表为空，进页面再试一次（优先用缓存）。
            _ = ShowModsAsync();
        }
    }

    [RelayCommand]
    private Task SearchModsAsync() => FetchModPageAsync(1, newSearch: true, ignoreCache: true);

    /// <summary>进入 Mod 页时先用缓存展示，避免每次都等一次网络。</summary>
    private Task ShowModsAsync() => FetchModPageAsync(1, newSearch: true, ignoreCache: false);

    [RelayCommand]
    private Task GoPreviousModPageAsync() => FetchModPageAsync(Math.Max(1, ModPage - 1), newSearch: false);

    [RelayCommand]
    private Task GoNextModPageAsync() => FetchModPageAsync(Math.Min(ModTotalPages, ModPage + 1), newSearch: false);

    /// <summary>
    /// 取搜索结果的一页。<paramref name="newSearch"/> 为 true 时按当前筛选重新搜索，
    /// 否则翻页；<paramref name="ignoreCache"/> 为 true 时跳过本地缓存。
    /// </summary>
    private async Task FetchModPageAsync(int page, bool newSearch, bool ignoreCache = false)
    {
        _modSearchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _modSearchCts = cts;

        if (newSearch)
        {
            _modActiveQuery = (
                _modPageType,
                SelectedModPlatform,
                ModSearchText.Trim(),
                SelectedModGameVersionFilter,
                _modPageType == ModProjectType.Mod ? SelectedModLoaderFilter : null);

            ModStatus = "正在搜索…";
            ModProjects.Clear();
            IsModDetailOpen = false;
        }
        else
        {
            if (_modActiveQuery is null) return;
            ModStatus = $"正在加载第 {page} 页…";
        }

        var request = _modActiveQuery!.Value;
        IsModsLoading = true;

        try
        {
            var result = await Task.Run(
                () => ModCatalog.SearchAsync(
                    request.Type,
                    request.Platform,
                    request.Query,
                    request.GameVersion,
                    request.Loader,
                    page,
                    ignoreCache,
                    cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            ModPage = result.Page;
            ModTotalPages = result.TotalPages;

            ModProjects.ReplaceAll(
                result.Projects.Select(project => new ModProjectItemViewModel(project, SelectModProject)));

            ModStatus = ModProjects.Count == 0
                ? request.Type == ModProjectType.ResourcePack ? "没有找到材质包" : "没有找到 Mod"
                : string.Empty;

            // 后台静默把后续页面和每个工程的文件列表准备好（低优先级线程）。
            if (newSearch)
            {
                ModPrefetcher.Start(
                    request.Type, request.Platform, request.Query, request.GameVersion, request.Loader, _lifetime.Token);
            }

            LauncherLog.Info(
                $"Mod 搜索：\"{request.Query}\"（{request.GameVersion ?? "全部"} / " +
                $"{request.Loader ?? "全部"}）第 {result.Page}/{result.TotalPages} 页，" +
                $"共 {result.Total} 个结果，本页 {ModProjects.Count} 个");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ModStatus = "搜索失败：" + exception.Message;
            LauncherLog.Warn($"Mod 搜索失败：{exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_modSearchCts, cts)) IsModsLoading = false;
        }
    }

    private void SelectModProject(ModProjectItemViewModel item)
    {
        SelectedModProject = item;
        _modDetailType = item.Project.Type;
        IsModDetailOpen = true;
        ModDetailTitle = item.Name;
        ModDetailDescription = item.Description;
        ModVersionRows.Clear();
        _allModVersions.Clear();
        ModVersionFilters.Clear();
        _expandedModVersionGroups.Clear();
        _selectedModVersionFilter = null;
        IsModRecommendSelected = item.Project.Type == ModProjectType.ResourcePack
            ? _settings.ResourcePackDetailStartsWithRecommend
            : _settings.ModDetailStartsWithRecommend;
        SelectedModFile = null;
        SelectedModFileTarget = null;
        OnPropertyChanged(nameof(ShowModVersionFilters));
        _ = LoadModVersionsAsync(item);
        UpdateRelatedModProjects();
    }

    [RelayCommand]
    private void CloseModDetail()
    {
        _modDetailCts?.Cancel();
        IsModDetailOpen = false;
        SelectedModProject = null;
        UpdateRelatedModProjects();
    }

    private async Task LoadModVersionsAsync(ModProjectItemViewModel item)
    {
        _modDetailCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _modDetailCts = cts;

        ModDetailStatus = "正在获取版本列表…";

        try
        {
            var versions = await Task.Run(
                () => ModCatalog.GetVersionsAsync(item.Project, null, null, cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            _allModVersions.Clear();
            foreach (var version in versions)
            {
                var vm = new ModVersionItemViewModel(version);
                if (version.Dependencies.Count > 0)
                    vm.DependencyText = $"{version.Dependencies.Count} 项前置";

                _allModVersions.Add(vm);
            }

            RebuildModVersionFilters();
            ApplyModVersionFilter();

            _ = FillDependencyTextAsync(item.Project.Platform, _allModVersions.ToList(), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ModDetailStatus = "获取版本失败：" + exception.Message;
        }
    }

    /// <summary>
    /// 推荐页的匹配目标：每个已安装版本一组，标题用它在本地列表里的名字。
    /// 没有加载器的原版实例不参与。
    /// </summary>
    private sealed record RecommendTarget(
        string Name,
        string GameVersion,
        List<string> Loaders,
        VersionItemViewModel Version);

    private List<RecommendTarget> RecommendTargets(ModProjectType type)
    {
        var targets = new List<RecommendTarget>();

        foreach (var version in Versions)
        {
            var gameVersion = version.Summary.ClientVersion;
            if (string.IsNullOrWhiteSpace(gameVersion)) continue;

            // 材质包不需要加载器，原版实例也能用。
            var loaders = type == ModProjectType.Mod
                ? ModCatalog.LoaderTags(version.Summary.Loader)
                : new List<string>();
            if (type == ModProjectType.Mod && loaders.Count == 0) continue;

            targets.Add(new RecommendTarget(version.Id, gameVersion!, loaders, version));
        }

        targets.Sort((left, right) =>
        {
            var byVersion = MinecraftVersionOrder.Compare(right.GameVersion, left.GameVersion);
            return byVersion != 0
                ? byVersion
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        return targets;
    }

    /// <summary>文件是否适配某个目标版本：游戏版本要对上；Mod 还要求加载器相交。</summary>
    private static bool MatchesTarget(ModVersionItemViewModel item, RecommendTarget target, ModProjectType type)
        => item.Version.GameVersions.Contains(target.GameVersion, StringComparer.OrdinalIgnoreCase) &&
           (type != ModProjectType.Mod ||
            item.Version.Loaders.Any(loader => target.Loaders.Any(tag =>
                string.Equals(tag, loader, StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// 重建模组版本条：把同一个大版本的文件并成一格（<c>1.21</c> 涵盖 1.21 / 1.21.1 …），
    /// 再按该线下的最高版本从新到旧排列。
    /// </summary>
    private void RebuildModVersionFilters()
    {
        // 版本条始终从全部文件生成：推荐只收窄列表，不应该把其他版本格吞掉。
        var lines = _allModVersions
            .SelectMany(item => item.Version.GameVersions)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => MinecraftVersionOrder.Line(value), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Line = group.Key,
                Newest = group.OrderByDescending(value => value, MinecraftVersionOrder.Comparer).First(),
            })
            .Where(entry => entry.Line.Length > 0)
            .OrderByDescending(entry => entry.Newest, MinecraftVersionOrder.Comparer)
            .Select(entry => entry.Line)
            .ToList();

        if (_selectedModVersionFilter is { Length: > 0 } selected &&
            !lines.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            _selectedModVersionFilter = null;
        }

        var filters = new List<ModVersionFilterViewModel>(lines.Count + 2)
        {
            new(RecommendFilter, "推荐", SelectModVersionFilter)
            {
                IsRecommend = true,
                IsSelected = IsModRecommendSelected,
            },
            new(null, "全部", SelectModVersionFilter)
            {
                IsSelected = !IsModRecommendSelected && _selectedModVersionFilter is null,
            },
        };

        foreach (var line in lines)
        {
            filters.Add(new ModVersionFilterViewModel(line, line, SelectModVersionFilter)
            {
                IsSelected = !IsModRecommendSelected &&
                             string.Equals(line, _selectedModVersionFilter, StringComparison.OrdinalIgnoreCase),
            });
        }

        ModVersionFilters.ReplaceAll(filters);
        OnPropertyChanged(nameof(ShowModVersionFilters));
    }

    /// <summary>
    /// 详情页列表：推荐页按本地版本分组（标题就是本地版本名）；
    /// 其余情况按「加载器 + 游戏版本线 + 正式/预览」分组。
    /// </summary>
    private void ApplyModVersionFilter()
    {
        ModVersionRows.Clear();
        SelectedModFile = null;
        SelectedModFileTarget = null;

        if (IsModRecommendSelected)
        {
            ApplyRecommendRows();
            return;
        }

        var source = _allModVersions;
        var filtered = _selectedModVersionFilter is { Length: > 0 } line
            ? source.Where(item => item.Version.GameVersions.Any(value =>
                string.Equals(MinecraftVersionOrder.Line(value), line, StringComparison.OrdinalIgnoreCase)))
            : source;

        var ordered = filtered
            .OrderByDescending(item => NewestGameVersion(item.Version), MinecraftVersionOrder.Comparer)
            .ThenByDescending(item => item.Version.Published ?? DateTimeOffset.MinValue)
            .ToList();

        foreach (var group in ordered
                     .GroupBy(item =>
                     {
                         var loader = string.Empty;
                         var loaderOrder = 0;
                         if (_modDetailType == ModProjectType.Mod)
                             (loader, loaderOrder) = LoaderOf(item.Version);

                         return (Line: VersionLineOf(item.Version), Loader: loader, LoaderOrder: loaderOrder,
                                 Preview: IsPreviewRelease(item.Version.ReleaseType));
                     })
                     .OrderByDescending(group => group.Key.Line, MinecraftVersionOrder.Comparer)
                     .ThenBy(group => group.Key.Preview)
                     .ThenBy(group => group.Key.LoaderOrder))
        {
            var items = group.ToList();
            var key = $"{group.Key.Line}|{group.Key.Loader}|{group.Key.Preview}";
            var expanded = _expandedModVersionGroups.Contains(key);

            var title = group.Key.Loader.Length == 0
                ? group.Key.Line
                : group.Key.Line.Length == 0
                    ? group.Key.Loader
                    : $"{group.Key.Loader} {group.Key.Line}";
            if (group.Key.Preview) title += " 预览版";

            ModVersionRows.Add(ModVersionListRowViewModel.CreateHeader(
                key, title, items, expanded, ToggleModVersionGroup));

            if (!expanded) continue;

            foreach (var item in items)
                ModVersionRows.Add(ModVersionListRowViewModel.CreateVersion(item, null, SelectModFileRow));
        }

        if (source.Count == 0)
        {
            ModDetailStatus = _modDetailType == ModProjectType.ResourcePack
                ? "该材质包没有可用文件"
                : "该 Mod 没有可用文件";
        }
        else if (_selectedModVersionFilter is null)
        {
            ModDetailStatus = $"共 {source.Count} 个文件";
        }
        else
        {
            ModDetailStatus = $"显示 {ordered.Count} / {source.Count} 个文件";
        }
    }

    /// <summary>
    /// 推荐页：每个已安装（且有加载器）的版本一组，标题用它在本地的名字；
    /// 一个文件如果能同时适配多个本地版本，就会在各组里各出现一次。
    /// </summary>
    private void ApplyRecommendRows()
    {
        var targets = RecommendTargets(_modDetailType);

        var ordered = _allModVersions
            .OrderByDescending(item => NewestGameVersion(item.Version), MinecraftVersionOrder.Comparer)
            .ThenByDescending(item => item.Version.Published ?? DateTimeOffset.MinValue)
            .ToList();

        var matched = new HashSet<ModVersionItemViewModel>();

        foreach (var target in targets)
        {
            var items = ordered.Where(item => MatchesTarget(item, target, _modDetailType)).ToList();
            if (items.Count == 0) continue;

            foreach (var item in items) matched.Add(item);

            var key = "rec|" + target.Name;
            var expanded = _expandedModVersionGroups.Contains(key);

            ModVersionRows.Add(ModVersionListRowViewModel.CreateHeader(
                key, target.Version.Id, items, expanded, ToggleModVersionGroup, target.Version));

            if (!expanded) continue;

            foreach (var item in items)
                ModVersionRows.Add(ModVersionListRowViewModel.CreateVersion(item, target.Version, SelectModFileRow));
        }

        ModDetailStatus = matched.Count == 0
            ? "没有适配已安装版本的文件"
            : $"共 {matched.Count} 个文件";
    }

    private void ToggleModVersionGroup(string key)
    {
        var index = -1;
        for (var i = 0; i < ModVersionRows.Count; i++)
        {
            if (ModVersionRows[i].IsHeader && string.Equals(ModVersionRows[i].Key, key, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0) return;

        var header = ModVersionRows[index];
        var expanded = !header.IsExpanded;
        header.IsExpanded = expanded;

        if (expanded)
        {
            _expandedModVersionGroups.Add(key);
            for (var i = 0; i < header.Items.Count; i++)
                ModVersionRows.Insert(index + 1 + i,
                    ModVersionListRowViewModel.CreateVersion(header.Items[i], header.Target, SelectModFileRow));
        }
        else
        {
            _expandedModVersionGroups.Remove(key);
            while (index + 1 < ModVersionRows.Count && !ModVersionRows[index + 1].IsHeader)
                ModVersionRows.RemoveAt(index + 1);
        }
    }

    /// <summary>文件所属加载器：多个加载器时取第一个认识的，未知时用原值，未标注时单独一组。</summary>
    private static (string Label, int Order) LoaderOf(ModVersion version)
    {
        foreach (var raw in version.Loaders)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "neoforge": return ("NeoForge", 0);
                case "fabric": return ("Fabric", 1);
                case "forge": return ("Forge", 2);
                case "quilt": return ("Quilt", 3);
                case "liteloader": return ("LiteLoader", 4);
            }
        }

        if (version.Loaders.Count == 0) return ("未标注加载器", 6);

        var first = version.Loaders[0].Trim();
        return (first.Length == 0
            ? "未标注加载器"
            : char.ToUpperInvariant(first[0]) + first[1..], 5);
    }

    /// <summary>该文件所属的版本线（支持的最新游戏版本所属的线）。</summary>
    private static string VersionLineOf(ModVersion version)
    {
        var newest = NewestGameVersion(version);
        return newest.Length == 0 ? string.Empty : MinecraftVersionOrder.Line(newest);
    }

    /// <summary>Beta / Alpha 统一归到「预览版」。</summary>
    private static bool IsPreviewRelease(string? releaseType) =>
        releaseType?.Trim().ToLowerInvariant() is "beta" or "alpha";

    private void SelectModVersionFilter(string? line)
    {
        var recommend = string.Equals(line, RecommendFilter, StringComparison.Ordinal);
        IsModRecommendSelected = recommend;
        _selectedModVersionFilter = recommend || string.IsNullOrWhiteSpace(line) ? null : line;

        foreach (var filter in ModVersionFilters)
        {
            filter.IsSelected = recommend
                ? filter.IsRecommend
                : !filter.IsRecommend && string.Equals(filter.Line, _selectedModVersionFilter,
                    StringComparison.OrdinalIgnoreCase);
        }

        ApplyModVersionFilter();
    }

    /// <summary>该文件支持的最高游戏版本；没有标注时返回空串（排序时落到底部）。</summary>
    private static string NewestGameVersion(ModVersion version)
    {
        var newest = string.Empty;

        foreach (var value in version.GameVersions)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (newest.Length == 0 || MinecraftVersionOrder.Compare(value, newest) > 0) newest = value;
        }

        return newest;
    }

    /// <summary>把前置 Mod 的 ID 换成名字，仅用于展示（PCL2 也不会自动下载单个 Mod 的前置）。</summary>
    private static async Task FillDependencyTextAsync(
        ModPlatform platform,
        IReadOnlyList<ModVersionItemViewModel> items,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = items.SelectMany(item => item.Version.Dependencies).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return;

            var projects = await ModCatalog.GetProjectsAsync(platform, ids, cancellationToken);
            var names = projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (item.Version.Dependencies.Count == 0) continue;

                item.DependencyText = "前置：" + string.Join("、", item.Version.Dependencies
                    .Select(id => names.TryGetValue(id, out var name) ? name : id));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"获取前置 Mod 名称失败：{exception.Message}");
        }
    }

    private async Task DownloadModVersionAsync(ModVersionItemViewModel item, VersionItemViewModel? targetVersion)
    {
        var version = item.Version;
        var type = _modDetailType;
        var typeLabel = type == ModProjectType.ResourcePack ? "材质包" : "Mod";

        // 推荐页：落到该行的目标版本；其余页面：存进 NCL/user/mods 或 NCL/user/resourcepacks。
        var target = targetVersion is not null
            ? ResolveModTargetPath(targetVersion.Summary, version, type)
            : ResolveUserModPath(version, type);

        if (target is null)
        {
            // 当前没有兼容的版本：让用户自己选位置（等价于 PCL2 的「另存为」）。
            if (ModSaveFilePicker is null)
            {
                ModDetailStatus = "当前版本不兼容，请先选择一个兼容的游戏版本";
                return;
            }

            var defaultFolder = type == ModProjectType.ResourcePack ? "resourcepacks" : "mods";
            var defaultDirectory = Path.Combine(MinecraftDirectory, defaultFolder);
            target = await ModSaveFilePicker(version.FileName, defaultDirectory);
            if (string.IsNullOrWhiteSpace(target)) return;
        }

        var task = TryCreateTask(
            $"{typeLabel} · {item.Title}",
            DownloadTaskKind.Generic,
            $"mod:{version.Platform}:{version.Id}");
        if (task is null) return;

        // 下载已经进了任务队列；根据设置决定是否退出详情页并切到任务页。
        var jumpToTasks = type == ModProjectType.ResourcePack
            ? JumpToTasksAfterResourcePackDownload
            : JumpToTasksAfterModDownload;
        if (jumpToTasks)
        {
            CloseModDetail();
            EnterTaskSection();
        }

        // 失败时不留下这次新建的空目录；原本就存在的资源目录不动。
        var targetDirectory = Path.GetDirectoryName(target);
        var directoryExisted = targetDirectory is not null && Directory.Exists(targetDirectory);

        task.StatusText = "正在下载…";
        Status = $"正在下载{typeLabel}：{item.Title}";
        LauncherLog.Info($"下载{typeLabel} {item.Title} → {target}");

        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                if (report.Percent is double percent && percent > task.Percent)
                    task.Percent = Math.Min(percent, 99);

                if (report.BytesReceived > 0)
                    task.SpeedMeter.Feed(report.BytesReceived);

                task.StatusText = $"{report.BytesReceived / 1048576.0:0.0} MB";
                NotifyDownloadStats();
            });

            await Task.Run(
                () => DownloadClient.Shared.DownloadFileAsync(
                    version.DownloadUrls,
                    target,
                    version.Sha1,
                    version.Size > 0 ? version.Size : null,
                    progress,
                    task.Token),
                task.Token);

            task.Percent = 100;
            var fileName = Path.GetFileName(target);
            FinishTask(task, DownloadTaskState.Completed, $"已下载到 {Path.GetDirectoryName(target)}");
            ModDetailStatus = $"已下载：{fileName}";
            Status = $"{typeLabel} 已下载：{fileName}";

            var openFolder = type == ModProjectType.ResourcePack
                ? OpenFolderAfterResourcePackDownload
                : OpenFolderAfterModDownload;
            if (openFolder)
                RevealFile(target);
        }
        catch (OperationCanceledException)
        {
            FinishTask(task, DownloadTaskState.Canceled, "下载已取消");
            TryRemoveCreatedEmptyDirectory(target, directoryExisted);
        }
        catch (Exception exception)
        {
            LauncherLog.Error($"下载{typeLabel}失败：{version.FileName}", exception);
            ShowDownloadError($"下载{typeLabel}", exception);
            FinishTask(task, DownloadTaskState.Failed, "下载失败");
            TryRemoveCreatedEmptyDirectory(target, directoryExisted);
        }
    }

    /// <summary>本机的资源库：NCL/user/mods 或 NCL/user/resourcepacks，按名字分文件夹。</summary>
    private string UserContentDirectory(ModProjectType type)
    {
        var name = string.IsNullOrWhiteSpace(ModDetailTitle) ? "unknown" : ModDetailTitle.Trim();
        var folder = type == ModProjectType.ResourcePack ? "resourcepacks" : "mods";
        return Path.Combine(AppSettings.UserDirectory, folder, SanitizeModFileName(name));
    }

    private string ResolveUserModPath(ModVersion version, ModProjectType type)
    {
        // 不在下载前建目录：目录由下载器真正写文件时创建，失败时不会留下空文件夹。
        return Path.Combine(UserContentDirectory(type), SanitizeModFileName(version.FileName));
    }

    /// <summary>
    /// 文件应落入的资源目录（mods / resourcepacks）；版本不兼容或未指定目标时返回 null。
    /// 只算路径，不建目录，供界面展示和下载前解析共用。
    /// </summary>
    private string? ResolveModsDirectory(VersionSummary? target, ModVersion version, ModProjectType type)
    {
        if (target is null) return null;

        var gameVersion = target.ClientVersion;
        if (!string.IsNullOrWhiteSpace(gameVersion) && version.GameVersions.Count > 0 &&
            !version.GameVersions.Any(value => string.Equals(value, gameVersion, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // 材质包不区分加载器。
        if (type == ModProjectType.Mod)
        {
            var loaderTags = ModCatalog.LoaderTags(target.Loader);
            if (version.Loaders.Count > 0 &&
                !version.Loaders.Any(loader => loaderTags.Any(tag =>
                    string.Equals(tag, loader, StringComparison.OrdinalIgnoreCase))))
            {
                return null;
            }
        }

        var versionDirectory = Path.GetDirectoryName(target.JsonPath) ?? string.Empty;
        if (string.IsNullOrEmpty(versionDirectory)) return null;

        var isolated = VersionIsolationDetector.Detect(
            MinecraftDirectory,
            target.Id,
            target.Type,
            modable: true,
            _settings.DefaultIsolation).IsIsolated;

        var gameDirectory = isolated ? versionDirectory : MinecraftDirectory;
        var contentFolder = type == ModProjectType.ResourcePack ? "resourcepacks" : "mods";
        return Path.Combine(gameDirectory, contentFolder);
    }

    /// <summary>
    /// 计算文件该写入哪个路径：目标版本由调用方传入，不依赖“当前选中版本”。
    /// 版本不兼容（游戏版本或加载器对不上）、或未指定目标时返回 null，由调用方让用户选位置。
    /// </summary>
    private string? ResolveModTargetPath(VersionSummary? target, ModVersion version, ModProjectType type)
    {
        var directory = ResolveModsDirectory(target, version, type);
        if (directory is null) return null;

        // 目录由下载器真正写文件时创建；失败时不会在这里留下空文件夹。
        return Path.Combine(directory, SanitizeModFileName(version.FileName));
    }

    /// <summary>
    /// 下载失败或取消时，删掉这次新建的空目录（mods 目录原本就存在时不动）。
    /// </summary>
    private static void TryRemoveCreatedEmptyDirectory(string filePath, bool existed)
    {
        if (existed) return;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (directory is null || !Directory.Exists(directory)) return;

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception)
        {
            // 目录被占用或还有其它内容时保持原样。
        }
    }

    /// <summary>在文件管理器里打开目录并选中刚下载的文件；失败只记日志。</summary>
    private static void RevealFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // explorer 的 /select 会打开所在目录并高亮该文件。
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetDirectoryName(filePath) ?? string.Empty,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"打开文件夹失败：{filePath}（{exception.Message}）");
        }
    }

    private static string SanitizeModFileName(string fileName)
    {
        var name = fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');

        // PCL2 的做法：~ 会导致 Mixin 加载失败。
        return name.Replace('~', '-');
    }
}
