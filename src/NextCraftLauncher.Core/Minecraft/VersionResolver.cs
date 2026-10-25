namespace NextCraftLauncher.Core.Minecraft;

public sealed record ResolvedLibrary(string Name, string Path, string? Url);

public sealed class ResolvedVersion
{
    public required string Id { get; init; }
    public required string MinecraftDirectory { get; init; }
    public required string VersionDirectory { get; init; }
    public required VersionInfo Merged { get; init; }
    public required string ClientJarPath { get; init; }
    public required string NativesDirectory { get; init; }
    public required string AssetsDirectory { get; init; }
    public required string AssetsIndexId { get; init; }
    public required int RequiredJavaMajor { get; init; }
    public required VersionIsolation Isolation { get; init; }
    public required IReadOnlyList<ResolvedLibrary> Libraries { get; init; }
    public required IReadOnlyList<ResolvedLibrary> NativeLibraries { get; init; }
    public required IReadOnlyList<string> MissingFiles { get; init; }

    public string? MainClass => Merged.MainClass;

    public bool IsComplete => MissingFiles.Count == 0;

    public string Classpath => string.Join(
        Path.PathSeparator,
        Libraries.Select(library => library.Path).Append(ClientJarPath));
}

/// <summary>
/// 读取版本 JSON（含 inheritsFrom 继承链），合并出实际要用的主类、库和参数。
/// </summary>
public static class VersionResolver
{
    public static ResolvedVersion Resolve(string minecraftDirectory, string versionId)
    {
        var chain = LoadChain(minecraftDirectory, versionId);
        var merged = Merge(chain);
        var environment = LaunchEnvironment.Current;

        var libraries = new List<ResolvedLibrary>();
        var nativeLibraries = new List<ResolvedLibrary>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in merged.Libraries ?? new List<LibraryInfo>())
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment)) continue;

            var artifact = library.Downloads?.Artifact;
            var relativePath = artifact?.Path;

            if (library.Natives is { Count: > 0 })
            {
                // 仅用于 natives 的库不把基础 jar 放进 classpath（与 PCL2 一致）。
                relativePath = null;
            }
            else if (string.IsNullOrEmpty(relativePath) && library.Downloads?.Classifiers is null)
            {
                // 没有 downloads 信息的库（Fabric / Forge 的 maven 条目）按坐标推导路径。
                relativePath = DeriveMavenPath(library.Name);
            }

            if (!string.IsNullOrEmpty(relativePath))
            {
                var fullPath = Path.Combine(
                    minecraftDirectory,
                    "libraries",
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (seenPaths.Add(fullPath))
                    libraries.Add(new ResolvedLibrary(library.Name ?? string.Empty, fullPath, artifact?.Url ?? library.Url));
            }

            // 老版本（1.18 及以前）把 natives 放在 classifiers 里，需要解压到 natives 目录。
            if (library.Natives is { Count: > 0 } natives &&
                library.Downloads?.Classifiers is { Count: > 0 } classifiers)
            {
                var native = SelectNativeArtifact(natives, classifiers, environment);
                if (native?.Path is { Length: > 0 } nativePath)
                {
                    var fullPath = Path.Combine(
                        minecraftDirectory,
                        "libraries",
                        nativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (seenPaths.Add(fullPath))
                        nativeLibraries.Add(new ResolvedLibrary(library.Name ?? string.Empty, fullPath, native.Url));
                }
            }
        }

        var clientJarCandidates = chain
            .Select(entry => Path.Combine(minecraftDirectory, "versions", entry.FolderId, entry.FolderId + ".jar"))
            .ToList();
        var clientJarPath = clientJarCandidates.FirstOrDefault(File.Exists) ?? clientJarCandidates[0];

        var assetsDirectory = Path.Combine(minecraftDirectory, "assets");
        var assetsIndexId = merged.AssetIndex?.Id ?? merged.Assets ?? "legacy";
        var nativesDirectory = Path.Combine(minecraftDirectory, "versions", versionId, "natives");

        var missingFiles = new List<string>();
        if (!File.Exists(clientJarPath)) missingFiles.Add(clientJarPath);
        foreach (var library in libraries)
            if (!File.Exists(library.Path)) missingFiles.Add(library.Path);
        foreach (var library in nativeLibraries)
            if (!File.Exists(library.Path)) missingFiles.Add(library.Path);

        if (!string.Equals(assetsIndexId, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            var indexPath = Path.Combine(assetsDirectory, "indexes", assetsIndexId + ".json");
            if (!File.Exists(indexPath)) missingFiles.Add(indexPath);
        }

        return new ResolvedVersion
        {
            Id = versionId,
            MinecraftDirectory = minecraftDirectory,
            VersionDirectory = Path.Combine(minecraftDirectory, "versions", versionId),
            Merged = merged,
            ClientJarPath = clientJarPath,
            NativesDirectory = nativesDirectory,
            AssetsDirectory = assetsDirectory,
            AssetsIndexId = assetsIndexId,
            RequiredJavaMajor = merged.JavaVersion is { MajorVersion: > 0 } javaVersion ? javaVersion.MajorVersion : 8,
            Isolation = VersionIsolationDetector.Detect(
                minecraftDirectory,
                versionId,
                merged.Type,
                !string.IsNullOrEmpty(merged.InheritsFrom)),
            Libraries = libraries,
            NativeLibraries = nativeLibraries,
            MissingFiles = missingFiles,
        };
    }

    private static List<(string FolderId, VersionInfo Info)> LoadChain(string minecraftDirectory, string versionId)
    {
        var chain = new List<(string, VersionInfo)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = versionId;

        while (true)
        {
            if (!visited.Add(current))
                throw new InvalidDataException($"版本继承链存在循环: {versionId}");

            var jsonPath = Path.Combine(minecraftDirectory, "versions", current, current + ".json");
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"找不到版本定义: {jsonPath}", jsonPath);

            var info = VersionInfo.Load(jsonPath);
            chain.Add((current, info));

            if (string.IsNullOrEmpty(info.InheritsFrom)) break;
            current = info.InheritsFrom;

            if (chain.Count > 32)
                throw new InvalidDataException("版本继承链过深");
        }

        return chain;
    }

    private static VersionInfo Merge(IReadOnlyList<(string FolderId, VersionInfo Info)> chain)
    {
        var merged = new VersionInfo();

        // 从最底层父版本开始覆盖，越靠近子版本优先级越高。
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var info = chain[i].Info;
            merged.Id = info.Id ?? merged.Id;
            merged.Type = info.Type ?? merged.Type;
            merged.InheritsFrom = info.InheritsFrom ?? merged.InheritsFrom;
            merged.MainClass = info.MainClass ?? merged.MainClass;
            merged.Assets = info.Assets ?? merged.Assets;
            merged.AssetIndex = info.AssetIndex ?? merged.AssetIndex;
            merged.JavaVersion = info.JavaVersion ?? merged.JavaVersion;
            merged.MinecraftArguments = info.MinecraftArguments ?? merged.MinecraftArguments;
        }

        // 依赖库：子版本优先，按 group:artifact:classifier 去重。
        var libraries = new List<LibraryInfo>();
        var libraryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, info) in chain)
        {
            foreach (var library in info.Libraries ?? new List<LibraryInfo>())
            {
                var key = LibraryKey(library.Name) ?? library.Name ?? Guid.NewGuid().ToString();
                if (libraryKeys.Add(key)) libraries.Add(library);
            }
        }
        merged.Libraries = libraries;

        // 参数：父版本在前，子版本在后（加载器追加的参数排在原版之后）。
        var arguments = new ArgumentsInfo();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var source = chain[i].Info.Arguments;
            if (source is null) continue;
            arguments.Game.AddRange(source.Game);
            arguments.Jvm.AddRange(source.Jvm);
            arguments.DefaultUserJvm.AddRange(source.DefaultUserJvm);
        }
        merged.Arguments = arguments;

        return merged;
    }

    private static string? LibraryKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var parts = name.Split(':');
        if (parts.Length < 3) return name;

        var classifier = parts.Length > 3 ? parts[3] : string.Empty;
        return $"{parts[0]}:{parts[1]}:{classifier}";
    }

    private static string? DeriveMavenPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var parts = name.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : string.Empty;
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    private static DownloadArtifact? SelectNativeArtifact(
        IReadOnlyDictionary<string, string> natives,
        IReadOnlyDictionary<string, DownloadArtifact> classifiers,
        LaunchEnvironment environment)
    {
        if (!natives.TryGetValue(environment.OsName, out var template) || string.IsNullOrEmpty(template))
            return null;

        var archToken = environment.Arch switch
        {
            "x86_64" => "64",
            "x86" => "32",
            "arm64" => "arm64",
            _ => "64",
        };

        if (classifiers.TryGetValue(template.Replace("${arch}", archToken), out var artifact))
            return artifact;

        // 少数老版本使用不带 ${arch} 的 classifiers 名称。
        if (classifiers.TryGetValue(template.Replace("${arch}", string.Empty), out artifact))
            return artifact;

        return null;
    }
}
