namespace ScreenRecorder.Core;

/// <summary>更新取得時に追跡できるリダイレクト先を判定する。</summary>
public static class UpdateRedirectPolicy
{
    public const int MaxRedirectCount = 5;
    public const string AllowedHost = "ktysne.info";
    private const string GitHubHost = "github.com";
    private const string GitHubUserContentSuffix = ".githubusercontent.com";

    /// <summary>zip 取得で追跡する HTTP ステータスかを返す。</summary>
    public static bool IsRedirectStatus(int statusCode) => statusCode is 301 or 302 or 303 or 307 or 308;

    /// <param name="currentUri">リダイレクト応答を返した URI。</param>
    /// <param name="location">応答の Location ヘッダー。</param>
    /// <param name="redirectsFollowed">この応答を受け取る前に追跡した回数。</param>
    public static bool TryResolve(Uri currentUri, Uri? location, int redirectsFollowed, out Uri? target, out string? error)
    {
        target = null;
        error = null;
        if (redirectsFollowed < 0) throw new ArgumentOutOfRangeException(nameof(redirectsFollowed));
        if (redirectsFollowed >= MaxRedirectCount)
        {
            error = "配布サーバーの転送回数が上限を超えました。";
            return false;
        }
        if (location is null || !currentUri.IsAbsoluteUri || !Uri.TryCreate(currentUri, location, out var resolved))
        {
            error = "配布サーバーの転送先を確認できませんでした。";
            return false;
        }
        // 転送先も update-v2.json の URL と同じ規則で、書かれたとおりの文字列を検証する。相対の転送先は検証済みの URL を土台に解決したものを見る。
        // ホストを含む相対の書き方(//host/...)は、解決で正規化されて検証をすり抜けるため受け付けない。
        var isNetworkPath = !location.IsAbsoluteUri && (location.OriginalString.StartsWith("//", StringComparison.Ordinal) || location.OriginalString.StartsWith(@"\\", StringComparison.Ordinal));
        var candidate = location.IsAbsoluteUri ? location.OriginalString : resolved.AbsoluteUri;
        if (isNetworkPath || !UpdateManifestParser.IsAllowedDownloadUrl(candidate))
        {
            error = "配布サーバーが許可されていない転送先を指定したため、中止しました。";
            return false;
        }
        target = resolved;
        return true;
    }

    /// <summary>zip の転送先を検証し、GitHub の署名付き URL は受信した文字列のまま返す。</summary>
    public static bool TryResolvePackage(Uri currentUri, string? location, int redirectsFollowed, out string? target, out string? error)
    {
        target = null;
        error = null;
        if (redirectsFollowed < 0) throw new ArgumentOutOfRangeException(nameof(redirectsFollowed));
        if (redirectsFollowed >= MaxRedirectCount)
        {
            error = "配布サーバーの転送回数が上限を超えました。";
            return false;
        }
        if (!currentUri.IsAbsoluteUri || string.IsNullOrEmpty(location))
        {
            error = "配布サーバーの転送先を確認できませんでした。";
            return false;
        }

        var currentIsLegacy = IsLegacyUri(currentUri);
        if (currentIsLegacy)
        {
            if (!Uri.TryCreate(location, UriKind.RelativeOrAbsolute, out var legacyLocation)
                || !TryResolve(currentUri, legacyLocation, redirectsFollowed, out var legacyTarget, out error))
            {
                error ??= "配布サーバーが許可されていない転送先を指定したため、中止しました。";
                return false;
            }
            target = legacyTarget!.AbsoluteUri;
            return true;
        }

        if (ContainsForbiddenCharacters(location)
            || !location.StartsWith("https://", StringComparison.Ordinal)
            || !Uri.TryCreate(location, UriKind.Absolute, out var githubTarget)
            || !IsGitHubUri(currentUri)
            || !IsGitHubUri(githubTarget))
        {
            error = "配布サーバーが許可されていない転送先を指定したため、中止しました。";
            return false;
        }

        target = location;
        return true;
    }

    /// <summary>要求用の URI を入力文字列から直接作り、転送 URL のクエリ表記を保つ。</summary>
    public static Uri CreateRequestUri(string requestUrl) => new(requestUrl, UriKind.Absolute);

    private static bool IsLegacyUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, AllowedHost, StringComparison.Ordinal)
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0;

    private static bool IsGitHubUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0
        && IsGitHubHost(uri.Host);

    private static bool IsGitHubHost(string host) =>
        string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase)
        || (host.EndsWith(GitHubUserContentSuffix, StringComparison.OrdinalIgnoreCase)
            && host.Length > GitHubUserContentSuffix.Length);

    private static bool ContainsForbiddenCharacters(string value) =>
        value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '#');
}
