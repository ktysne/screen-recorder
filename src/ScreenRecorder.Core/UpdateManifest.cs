using System.Globalization;
using System.Text.Json;

namespace ScreenRecorder.Core;

/// <summary>検証を通った update.json の内容。<see cref="Sha256"/> は小文字の 16 進にそろえてある。</summary>
public sealed record UpdateManifest(UpdateVersion Version, string Url, string Sha256, DateOnly? ReleasedAt);

public sealed record UpdateManifestParseResult(UpdateManifest? Manifest, string? Error)
{
    public static UpdateManifestParseResult Success(UpdateManifest manifest) => new(manifest, null);
    public static UpdateManifestParseResult Failure(string error) => new(null, error);
}

/// <summary>update.json を検証する。条件の正本は docs/design.md「最新版情報」。</summary>
public static class UpdateManifestParser
{
    public const string ManifestUrl = "https://ktysne.info/screen-recorder/update.json";
    public const string DistributionPageUrl = "https://ktysne.info/screen-recorder/";
    public const string AllowedHost = "ktysne.info";
    private const string HttpsPrefix = "https://";
    private const int Sha256HexLength = 64;

    public static UpdateManifestParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return UpdateManifestParseResult.Failure("最新版情報が空でした。");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return UpdateManifestParseResult.Failure("最新版情報を JSON として読めませんでした。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return UpdateManifestParseResult.Failure("最新版情報の形式が想定と異なります。");
            // 1.0 や 1e0 を受け付けないよう、数値の表記そのものを比べる。
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "1")
                return UpdateManifestParseResult.Failure("最新版情報の schema が 1 ではありません。");
            if (!root.TryGetProperty("latest", out var latest) || latest.ValueKind != JsonValueKind.Object)
                return UpdateManifestParseResult.Failure("最新版情報に latest がありません。");

            var versionText = ReadString(latest, "version");
            if (!UpdateVersion.TryParse(versionText, out var version))
                return UpdateManifestParseResult.Failure("最新版情報の version が X.Y.Z の形式ではありません。");

            var url = ReadString(latest, "url");
            if (!IsAllowedDownloadUrl(url))
                return UpdateManifestParseResult.Failure("最新版情報のダウンロード先が配布サーバーではありません。");

            var sha256 = ReadString(latest, "sha256");
            if (sha256 is null || sha256.Length != Sha256HexLength || !sha256.All(char.IsAsciiHexDigit))
                return UpdateManifestParseResult.Failure("最新版情報の sha256 が 16 進 64 文字ではありません。");

            var releasedAt = DateOnly.TryParseExact(ReadString(latest, "releasedAt"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : (DateOnly?)null;
            return UpdateManifestParseResult.Success(new UpdateManifest(version, url!, sha256.ToLowerInvariant(), releasedAt));
        }
    }

    /// <summary>
    /// <c>https://</c> の直後から最初の <c>/</c>、<c>?</c>、<c>#</c> までを一字一句比べる。
    /// 利用者情報やポートを挟んだ書き方では、接続先が見た目のホストと異なりうるため受け付けない。
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (url is null || !url.StartsWith(HttpsPrefix, StringComparison.Ordinal)) return false;
        if (url.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\')) return false;
        var rest = url[HttpsPrefix.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var host = end < 0 ? rest : rest[..end];
        if (!string.Equals(host, AllowedHost, StringComparison.Ordinal)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, AllowedHost, StringComparison.Ordinal)
            && uri.IsDefaultPort
            && uri.UserInfo.Length == 0;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
