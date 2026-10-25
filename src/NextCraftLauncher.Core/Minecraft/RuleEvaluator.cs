using System.Runtime.InteropServices;

namespace NextCraftLauncher.Core.Minecraft;

/// <summary>
/// 当前运行环境，用于求值版本 JSON 中的 rules。
/// </summary>
public sealed class LaunchEnvironment
{
    public required string OsName { get; init; }
    public required string Arch { get; init; }
    public required Version OsVersion { get; init; }
    public required IReadOnlyDictionary<string, bool> Features { get; init; }

    public static LaunchEnvironment Current { get; } = new()
    {
        OsName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        Arch = NormalizeArchitecture(RuntimeInformation.ProcessArchitecture),
        OsVersion = Environment.OSVersion.Version,
        Features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
    };

    public static string NormalizeArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "x86_64",
    };

    public static string NormalizeArchitecture(string architecture) => architecture.ToLowerInvariant() switch
    {
        "amd64" or "x64" or "x86_64" => "x86_64",
        "aarch64" or "arm64" => "arm64",
        "x86" or "i386" or "i686" => "x86",
        "arm" => "arm",
        _ => architecture.ToLowerInvariant(),
    };
}

public static class RuleEvaluator
{
    /// <summary>
    /// Mojang 的规则语义：初始为不允许，按顺序应用匹配到的规则，最后一条匹配规则决定结果。
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<RuleInfo>? rules, LaunchEnvironment environment)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, environment)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(RuleInfo rule, LaunchEnvironment environment)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !os.Name.Equals(environment.OsName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (os.Arch is not null &&
                !LaunchEnvironment.NormalizeArchitecture(os.Arch).Equals(environment.Arch, StringComparison.OrdinalIgnoreCase))
                return false;

            if (os.VersionRange is { } range)
            {
                if (range.Min is not null && Version.TryParse(range.Min, out var min) && environment.OsVersion < min)
                    return false;
                if (range.Max is not null && Version.TryParse(range.Max, out var max) && environment.OsVersion >= max)
                    return false;
            }
        }

        if (rule.Features is { Count: > 0 } features)
        {
            foreach (var (key, expected) in features)
            {
                // 启动器目前只支持关闭所有可选特性（Demo、自定义分辨率、快速游玩等）。
                var actual = environment.Features.TryGetValue(key, out var value) && value;
                if (actual != expected) return false;
            }
        }

        return true;
    }
}
