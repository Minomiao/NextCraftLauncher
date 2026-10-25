using System.Text.Json.Serialization;

namespace NextCraftLauncher.Download.Java;

public sealed class JavaRuntimeIndexEntry
{
    [JsonPropertyName("manifest")] public JavaRuntimeManifestReference? Manifest { get; set; }
    [JsonPropertyName("version")] public JavaRuntimeVersion? Version { get; set; }
}

public sealed class JavaRuntimeManifestReference
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class JavaRuntimeVersion
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("released")] public string? Released { get; set; }
}

public sealed class JavaRuntimeManifest
{
    [JsonPropertyName("files")] public Dictionary<string, JavaRuntimeFile> Files { get; set; } = new();
}

public sealed class JavaRuntimeFile
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("executable")] public bool Executable { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("downloads")] public JavaRuntimeDownloads? Downloads { get; set; }
}

public sealed class JavaRuntimeDownloads
{
    [JsonPropertyName("raw")] public JavaRuntimeArtifact? Raw { get; set; }
}

public sealed class JavaRuntimeArtifact
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
