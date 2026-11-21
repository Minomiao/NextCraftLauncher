using System;
using System.Collections.Generic;
using System.Globalization;

namespace NextCraftLauncher.Core.Minecraft;

/// <summary>
/// Minecraft 版本号的排序。
/// <para>
/// 正式版是 <c>1.21.1</c>，预发布与候选版是 <c>1.21-pre1</c> / <c>1.21-rc1</c>，
/// 快照是 <c>24w14a</c>，远古版本是 <c>a1.2.6</c> / <c>b1.7.3</c>，都不是 semver。
/// 这里把它们压成一个可比较的键：同一基础版本内 pre &lt; rc &lt; 正式版。
/// </para>
/// </summary>
public static class MinecraftVersionOrder
{
    /// <summary>供 LINQ 使用的比较器（版本号越低越靠前）。</summary>
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    /// <summary>按版本高低比较；无法识别的部分退回序号字符串比较。</summary>
    public static int Compare(string? x, string? y)
        => Parse(x).CompareTo(Parse(y));

    /// <summary>
    /// 版本线：筛选条上的一格。<c>1.21.1</c> → <c>1.21</c>，<c>26.3.2</c> → <c>26.3</c>，
    /// <c>24w14a</c> → <c>24w14</c>，<c>a1.2.6</c> → <c>a1.2</c>；解析不出时返回原值。
    /// </summary>
    public static string Line(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;

        var key = Parse(id);
        return key.Kind switch
        {
            VersionKind.Release => $"{key.Major}.{key.Minor}",
            VersionKind.Snapshot => $"{key.Year}w{key.Week:00}",
            VersionKind.Legacy => $"{key.Prefix}{key.Major}.{key.Minor}",
            _ => id.Trim(),
        };
    }

    private enum VersionKind
    {
        Legacy = 0,
        Snapshot = 1,
        Release = 2,
    }

    private readonly record struct Key(
        VersionKind Kind,
        int Year,
        int Week,
        int Major,
        int Minor,
        int Patch,
        char Prefix,
        int Stage,
        int StageNumber,
        string Raw) : IComparable<Key>
    {
        public int CompareTo(Key other)
        {
            var kind = Kind.CompareTo(other.Kind);
            if (kind != 0) return kind;

            switch (Kind)
            {
                case VersionKind.Snapshot:
                    var snapshot = Year.CompareTo(other.Year);
                    if (snapshot != 0) return snapshot;
                    snapshot = Week.CompareTo(other.Week);
                    return snapshot != 0 ? snapshot : string.CompareOrdinal(Raw, other.Raw);

                case VersionKind.Legacy:
                    var prefix = Prefix.CompareTo(other.Prefix);
                    if (prefix != 0) return prefix;
                    var legacy = Major.CompareTo(other.Major);
                    if (legacy != 0) return legacy;
                    legacy = Minor.CompareTo(other.Minor);
                    return legacy != 0 ? legacy : Patch.CompareTo(other.Patch);

                default:
                    var major = Major.CompareTo(other.Major);
                    if (major != 0) return major;
                    var minor = Minor.CompareTo(other.Minor);
                    if (minor != 0) return minor;
                    var patch = Patch.CompareTo(other.Patch);
                    if (patch != 0) return patch;

                    // pre1 < pre2 < rc1 < rc2 < 正式版
                    var stage = Stage.CompareTo(other.Stage);
                    return stage != 0 ? stage : StageNumber.CompareTo(other.StageNumber);
            }
        }
    }

    private static Key Parse(string? id)
    {
        var text = id?.Trim() ?? string.Empty;
        if (text.Length == 0) return new Key(VersionKind.Release, 0, 0, 0, 0, 0, '\0', 0, 0, text);

        if (TryParseSnapshot(text, out var year, out var week))
            return new Key(VersionKind.Snapshot, year, week, 0, 0, 0, '\0', 0, 0, text);

        if (TryParseLegacy(text, out var prefix, out var parts))
            return new Key(VersionKind.Legacy, 0, 0, parts.Major, parts.Minor, parts.Patch, prefix, 0, 0, text);

        if (TryParseRelease(text, out var major, out var minor, out var patch, out var stage, out var stageNumber))
            return new Key(VersionKind.Release, 0, 0, major, minor, patch, '\0', stage, stageNumber, text);

        return new Key(VersionKind.Release, 0, 0, 0, 0, 0, '\0', 0, 0, text);
    }

    /// <summary><c>24w14a</c>：两位年份 + w + 周数 + 可选字母。</summary>
    private static bool TryParseSnapshot(string text, out int year, out int week)
    {
        year = week = 0;

        var w = text.IndexOf('w');
        if (w <= 0 || w > 2) return false;
        if (!int.TryParse(text.AsSpan(0, w), NumberStyles.None, CultureInfo.InvariantCulture, out year))
            return false;

        var start = w + 1;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        if (end == start) return false;

        return int.TryParse(text.AsSpan(start, end - start), NumberStyles.None,
            CultureInfo.InvariantCulture, out week);
    }

    /// <summary><c>a1.2.6</c> / <c>b1.7.3</c> 之类的远古版本。</summary>
    private static bool TryParseLegacy(string text, out char prefix, out (int Major, int Minor, int Patch) parts)
    {
        prefix = '\0';
        parts = default;

        if (text.Length < 2 || !char.IsLetter(text[0])) return false;
        if (char.ToLowerInvariant(text[0]) is not ('a' or 'b')) return false;

        prefix = char.ToLowerInvariant(text[0]);
        var rest = text[1..];
        if (rest.StartsWith('-')) rest = rest[1..];

        var numbers = ParseNumbers(rest);
        if (numbers.Length == 0) return false;

        parts = (
            numbers[0],
            numbers.Length > 1 ? numbers[1] : 0,
            numbers.Length > 2 ? numbers[2] : 0);
        return true;
    }

    /// <summary><c>1.21</c> / <c>1.21.1</c> / <c>1.21-pre2</c> / <c>1.21.1-rc1</c>。</summary>
    private static bool TryParseRelease(
        string text, out int major, out int minor, out int patch, out int stage, out int stageNumber)
    {
        major = minor = patch = 0;
        stage = 300;
        stageNumber = 0;

        var dash = text.IndexOf('-');
        var core = dash >= 0 ? text[..dash] : text;
        var suffix = dash >= 0 ? text[(dash + 1)..] : null;

        var numbers = ParseNumbers(core);
        if (numbers.Length == 0) return false;

        major = numbers[0];
        minor = numbers.Length > 1 ? numbers[1] : 0;
        patch = numbers.Length > 2 ? numbers[2] : 0;

        if (suffix is null) return true;

        var isPre = suffix.StartsWith("pre", StringComparison.OrdinalIgnoreCase);
        var isRc = suffix.StartsWith("rc", StringComparison.OrdinalIgnoreCase);
        if (!isPre && !isRc) return true;

        stage = isPre ? 100 : 200;

        var digits = suffix[(isPre ? 3 : 2)..];
        stageNumber = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

        return true;
    }

    private static int[] ParseNumbers(string text)
    {
        var result = new List<int>(3);
        var index = 0;

        while (index < text.Length)
        {
            if (!char.IsDigit(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsDigit(text[index])) index++;

            result.Add(int.TryParse(text.AsSpan(start, index - start), NumberStyles.None,
                CultureInfo.InvariantCulture, out var value)
                ? value
                : 0);

            if (result.Count == 3) break;
        }

        return result.ToArray();
    }
}
