using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCraftLauncher.Core.Minecraft;

public sealed class VersionInfo
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexInfo? AssetIndex { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionInfo? JavaVersion { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryInfo>? Libraries { get; set; }
    [JsonPropertyName("arguments")] public ArgumentsInfo? Arguments { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }

    public static VersionInfo Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<VersionInfo>(json, JsonOptions)
               ?? throw new InvalidDataException($"无法解析版本 JSON: {path}");
    }
}

public sealed class AssetIndexInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class JavaVersionInfo
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")] public DownloadArtifact? Client { get; set; }
}

public sealed class LibraryInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("rules")] public List<RuleInfo>? Rules { get; set; }
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadArtifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class DownloadArtifact
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class RuleInfo
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("os")] public OsRule? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("versionRange")] public OsVersionRange? VersionRange { get; set; }
}

public sealed class OsVersionRange
{
    [JsonPropertyName("min")] public string? Min { get; set; }
    [JsonPropertyName("max")] public string? Max { get; set; }
}

public sealed class ArgumentsInfo
{
    [JsonPropertyName("game")] public List<ArgumentEntry> Game { get; set; } = new();
    [JsonPropertyName("jvm")] public List<ArgumentEntry> Jvm { get; set; } = new();

    /// <summary>
    /// 新版版本 JSON 中的默认用户 JVM 参数（内存、GC 等），可由启动器覆盖。
    /// </summary>
    [JsonPropertyName("default-user-jvm")] public List<ArgumentEntry> DefaultUserJvm { get; set; } = new();
}

/// <summary>
/// 参数既可能是纯字符串，也可能是带 rules 的对象，value 还可能是字符串或字符串数组。
/// </summary>
[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RuleInfo>? Rules { get; init; }
}

public sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.String)
            return new ArgumentEntry { Values = new[] { root.GetString()! } };

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"无法识别的启动参数格式: {root.ValueKind}");

        var rules = root.TryGetProperty("rules", out var rulesElement)
            ? rulesElement.Deserialize<List<RuleInfo>>(options)
            : null;

        var values = new List<string>();
        if (root.TryGetProperty("value", out var valueElement))
        {
            switch (valueElement.ValueKind)
            {
                case JsonValueKind.String:
                    values.Add(valueElement.GetString()!);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in valueElement.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            values.Add(item.GetString()!);
                    break;
            }
        }

        return new ArgumentEntry { Values = values, Rules = rules };
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
        => throw new NotSupportedException("启动参数只用于反序列化。");
}
