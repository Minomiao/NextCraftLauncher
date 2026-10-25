using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NextCraftLauncher.Core.Java;

public sealed record JavaRuntime(
    string Path,
    int MajorVersion,
    string VersionText,
    string Architecture,
    string? Vendor,
    bool IsBundled)
{
    public string DisplayName
    {
        get
        {
            var vendor = string.IsNullOrEmpty(Vendor) ? string.Empty : $" · {Vendor}";
            var bundled = IsBundled ? " · 官方运行时" : string.Empty;
            return $"Java {MajorVersion}（{VersionText}）{vendor}{bundled}";
        }
    }
}

public static class JavaLocator
{
    /// <summary>
    /// 从 JAVA_HOME、.minecraft/runtime、常见安装目录和 PATH 中寻找可用的 Java。
    /// </summary>
    public static async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(
        string minecraftDirectory,
        string? preferredPath = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            candidates.Add(preferredPath);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            candidates.Add(System.IO.Path.Combine(javaHome, "bin", "java.exe"));

        var runtimeDirectory = System.IO.Path.Combine(minecraftDirectory, "runtime");
        if (Directory.Exists(runtimeDirectory))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(runtimeDirectory, "java.exe", SearchOption.AllDirectories));
            }
            catch (Exception)
            {
                // 运行时目录不可读时忽略。
            }
        }

        // 官方启动器默认的 %APPDATA%\.minecraft\runtime，即使用户选了别的游戏目录也仍然可用。
        var defaultRuntimeDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft",
            "runtime");
        if (!string.Equals(defaultRuntimeDirectory, runtimeDirectory, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(defaultRuntimeDirectory))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(defaultRuntimeDirectory, "java.exe", SearchOption.AllDirectories));
            }
            catch (Exception)
            {
                // 忽略不可读的默认运行时目录。
            }
        }

        foreach (var baseDirectory in CommonJavaDirectories())
            AddJavaFromTree(candidates, baseDirectory, depth: 2);

        foreach (var pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var executable = System.IO.Path.Combine(pathEntry.Trim('"'), "java.exe");
            if (File.Exists(executable)) candidates.Add(executable);
        }

        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
            try
            {
                var fullPath = System.IO.Path.GetFullPath(candidate);
                if (seen.Add(fullPath)) distinct.Add(fullPath);
            }
            catch (Exception)
            {
                // 非法路径直接跳过。
            }
        }

        var probed = await Task.WhenAll(distinct.Select(path => ProbeAsync(path, minecraftDirectory, cancellationToken)));
        return probed
            .Where(runtime => runtime is not null)
            .Select(runtime => runtime!)
            .OrderByDescending(runtime => runtime.MajorVersion)
            .ThenBy(runtime => runtime.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Task<JavaRuntime?> ProbeAsync(
        string javaPath,
        string minecraftDirectory,
        CancellationToken cancellationToken = default)
    {
        return ProbeCoreAsync(javaPath, minecraftDirectory, cancellationToken);
    }

    /// <summary>
    /// 优先选择主版本完全匹配的 Java，其次是最接近且更高的版本，最后退回最高版本。
    /// </summary>
    public static JavaRuntime? SelectBest(IEnumerable<JavaRuntime> runtimes, int requiredMajor)
    {
        var list = runtimes.ToList();
        if (list.Count == 0) return null;

        return list.Where(runtime => runtime.MajorVersion == requiredMajor)
                   .OrderBy(runtime => runtime.Architecture == "x86_64" ? 0 : 1)
                   .FirstOrDefault()
               ?? list.Where(runtime => runtime.MajorVersion > requiredMajor)
                      .OrderBy(runtime => runtime.MajorVersion)
                      .ThenBy(runtime => runtime.Architecture == "x86_64" ? 0 : 1)
                      .FirstOrDefault()
               ?? list.OrderByDescending(runtime => runtime.MajorVersion).First();
    }

    /// <summary>
    /// 按 PCL2 的版本范围（例如 "17"、"17-21"、"-21"）选择 Java。
    /// </summary>
    public static JavaRuntime? SelectInRange(IEnumerable<JavaRuntime> runtimes, string range)
    {
        var (minimum, maximum) = ParseMajorRange(range);
        if (minimum is null && maximum is null) return null;

        return runtimes
            .Where(runtime => (minimum is null || runtime.MajorVersion >= minimum) &&
                              (maximum is null || runtime.MajorVersion <= maximum))
            .OrderBy(runtime => runtime.Architecture == "x86_64" ? 0 : 1)
            .ThenBy(runtime => runtime.MajorVersion)
            .FirstOrDefault();
    }

    private static (int? Minimum, int? Maximum) ParseMajorRange(string range)
    {
        var text = range.Trim().Replace("∞", string.Empty).Replace(" ", string.Empty);
        var numbers = Regex.Matches(text, @"\d+")
            .Select(match => int.Parse(match.Value))
            .ToArray();

        if (numbers.Length == 0) return (null, null);
        if (numbers.Length == 1)
            return text.StartsWith('-') ? (null, numbers[0]) : (numbers[0], null);

        return (numbers[0], numbers[1]);
    }

    private static async Task<JavaRuntime?> ProbeCoreAsync(
        string javaPath,
        string minecraftDirectory,
        CancellationToken cancellationToken)
    {
        string? versionText = null;
        string? vendor = null;
        string? architecture = null;

        var home = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(javaPath));
        var releaseFile = home is null ? null : System.IO.Path.Combine(home, "release");
        if (releaseFile is not null && File.Exists(releaseFile))
        {
            try
            {
                foreach (var line in File.ReadLines(releaseFile))
                {
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;

                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim().Trim('"');
                    switch (key)
                    {
                        case "JAVA_VERSION": versionText = value; break;
                        case "IMPLEMENTOR": vendor = value; break;
                        case "OS_ARCH": architecture = value; break;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到 release 文件时退回执行 java -version。
            }
        }

        if (versionText is null)
        {
            var output = await TryRunVersionAsync(javaPath, cancellationToken);
            if (output is null) return null;

            versionText = ParseVersionFromOutput(output);
            vendor ??= ParseVendor(output);
        }

        if (versionText is null) return null;

        var major = ParseMajor(versionText);
        if (major <= 0) return null;

        var bundledRoot = System.IO.Path.Combine(minecraftDirectory, "runtime");
        var defaultBundledRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft",
            "runtime");
        var isBundled = javaPath.StartsWith(bundledRoot, StringComparison.OrdinalIgnoreCase) ||
                        javaPath.StartsWith(defaultBundledRoot, StringComparison.OrdinalIgnoreCase);

        return new JavaRuntime(
            javaPath,
            major,
            versionText,
            LaunchEnvironmentNormalize(architecture),
            vendor,
            isBundled);
    }

    private static async Task<string?> TryRunVersionAsync(string javaPath, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var startInfo = new ProcessStartInfo(javaPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-version");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return null;

            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return null;
            }

            return await standardError + await standardOutput;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ParseVersionFromOutput(string output)
    {
        var match = Regex.Match(output, "version \"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseVendor(string output)
    {
        if (output.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Adoptium", StringComparison.OrdinalIgnoreCase)) return "Adoptium";
        if (output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
        if (output.Contains("Zulu", StringComparison.OrdinalIgnoreCase)) return "Zulu";
        if (output.Contains("Corretto", StringComparison.OrdinalIgnoreCase)) return "Amazon Corretto";
        if (output.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase)) return "OpenJDK";
        if (output.Contains("Apple", StringComparison.OrdinalIgnoreCase)) return "Apple";
        return null;
    }

    private static int ParseMajor(string version)
    {
        var match = Regex.Match(version.Trim().Trim('"'), @"(\d+)");
        if (!match.Success) return 0;

        var major = int.Parse(match.Groups[1].Value);
        if (major != 1) return major;

        var legacy = Regex.Match(version, @"1\.(\d+)");
        return legacy.Success ? int.Parse(legacy.Groups[1].Value) : major;
    }

    private static string LaunchEnvironmentNormalize(string? architecture)
    {
        return architecture is null ? "x86_64" : Minecraft.LaunchEnvironment.NormalizeArchitecture(architecture);
    }

    private static IEnumerable<string> CommonJavaDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return System.IO.Path.Combine(root, "Java");
            yield return System.IO.Path.Combine(root, "Eclipse Adoptium");
            yield return System.IO.Path.Combine(root, "Microsoft");
            yield return System.IO.Path.Combine(root, "Zulu");
            yield return System.IO.Path.Combine(root, "BellSoft");
            yield return System.IO.Path.Combine(root, "Amazon Corretto");
            yield return System.IO.Path.Combine(root, "AdoptOpenJDK");
            yield return System.IO.Path.Combine(root, "Semeru");
            yield return System.IO.Path.Combine(root, "OpenJDK");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return System.IO.Path.Combine(localAppData, "Programs", "Eclipse Adoptium");
            yield return System.IO.Path.Combine(localAppData, "Programs", "Microsoft");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) yield return System.IO.Path.Combine(profile, ".jdks");
    }

    private static void AddJavaFromTree(List<string> candidates, string root, int depth)
    {
        if (!Directory.Exists(root)) return;

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, currentDepth) = queue.Dequeue();
            var executable = System.IO.Path.Combine(directory, "bin", "java.exe");
            if (File.Exists(executable))
            {
                candidates.Add(executable);
                continue;
            }

            if (currentDepth >= depth) continue;

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                    queue.Enqueue((child, currentDepth + 1));
            }
            catch (Exception)
            {
                // 忽略无权限目录。
            }
        }
    }
}
