using System.Globalization;
using System.Text.RegularExpressions;

namespace LenxTool.Core.Updates;

public sealed partial class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? preRelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        BuildMetadata = buildMetadata;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? BuildMetadata { get; }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out SemanticVersion? version)
            ? version!
            : throw new FormatException($"无效的语义版本：{value}");

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        EqualityComparer<SemanticVersion>.Default.Equals(left, right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        Match match = VersionPattern().Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(match.Groups["patch"].Value, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        version = new(
            major, minor, patch,
            EmptyToNull(match.Groups["pre"].Value),
            EmptyToNull(match.Groups["build"].Value));
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        int core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        return core != 0 ? core : ComparePreRelease(PreRelease, other.PreRelease);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public override string ToString()
    {
        string value = $"{Major}.{Minor}.{Patch}";
        if (PreRelease is not null) value += $"-{PreRelease}";
        if (BuildMetadata is not null) value += $"+{BuildMetadata}";
        return value;
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');
        for (int index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length) return -1;
            if (index >= rightParts.Length) return 1;

            bool leftNumeric = int.TryParse(leftParts[index], CultureInfo.InvariantCulture, out int leftNumber);
            bool rightNumeric = int.TryParse(rightParts[index], CultureInfo.InvariantCulture, out int rightNumber);
            int comparison = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[index], rightParts[index])
            };
            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    [GeneratedRegex("^(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-(?<pre>(?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\\+(?<build>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex VersionPattern();
}
