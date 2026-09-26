using System.Globalization;

namespace ScreenRecorder.Core;

/// <summary>更新の判定に使う <c>X.Y.Z</c> の版。</summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    /// <summary>各要素が ASCII の数字だけからなる <c>X.Y.Z</c> を受け付ける。前後の空白や接尾辞は受け付けない。</summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || !part.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])) return false;
        }
        version = new UpdateVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>実行中のアプリの版を読む。InformationalVersion に付く <c>+</c> 以降のビルド情報は無視する。</summary>
    public static bool TryParseApplicationVersion(string? text, out UpdateVersion version)
    {
        var trimmed = text?.Trim();
        var plus = trimmed?.IndexOf('+') ?? -1;
        return TryParse(plus >= 0 ? trimmed![..plus] : trimmed, out version);
    }

    public int CompareTo(UpdateVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
