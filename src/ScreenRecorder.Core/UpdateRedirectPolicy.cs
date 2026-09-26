namespace ScreenRecorder.Core;

/// <summary>更新取得時に追跡できるリダイレクト先を判定する。</summary>
public static class UpdateRedirectPolicy
{
    public const int MaxRedirectCount = 5;
    public const string AllowedHost = "ktysne.info";

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
        // 転送先も update.json の URL と同じ規則で、書かれたとおりの文字列を検証する。相対の転送先は検証済みの URL を土台に解決したものを見る。
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
}
