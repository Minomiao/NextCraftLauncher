using System.Text.Json.Serialization;

namespace NextCraftLauncher.Download.Minecraft;

public sealed class MinecraftVersionManifest
{
    [JsonPropertyName("latest")] public MinecraftVersionLatest? Latest { get; set; }
    [JsonPropertyName("versions")] public List<MinecraftVersionEntry> Versions { get; set; } = new();
}

public sealed class MinecraftVersionLatest
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
}

public sealed class MinecraftVersionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("time")] public DateTimeOffset? Time { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset? ReleaseTime { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("complianceLevel")] public int ComplianceLevel { get; set; }
}

public sealed record MinecraftVersionManifestResult(
    IReadOnlyList<MinecraftVersionEntry> Versions,
    string? LatestRelease,
    string? LatestSnapshot);
