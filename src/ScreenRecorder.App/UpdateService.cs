using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var text = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }
}

internal static class UpdatePaths
{
    public const string SingletonMutexName = "Local\\ScreenRecorder.Singleton";
    public const string ApplierMutexName = "Local\\ScreenRecorder.UpdateApplier";
    public static string UpdateDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder", "update");
    public static string GetCleanupRecordPath(string updateId) => Path.Combine(UpdateDirectory, UpdateCleanupRecord.GetFileName(updateId));
    public static string LegacyCleanupRecordPath => Path.Combine(UpdateDirectory, UpdateCleanupRecord.FileName);
    public static string GetZipPath(UpdateVersion version) => Path.Combine(UpdateDirectory, $"ScreenRecorder-{version}-win-x64.zip");
    public static string GetExtractDirectory(UpdateVersion version) => Path.Combine(UpdateDirectory, $"ScreenRecorder-{version}");
}

internal sealed record UpdateDownloadProgress(long ReceivedBytes, long? TotalBytes);

internal sealed record PreparedUpdate(UpdateManifest Manifest, string ExtractedDirectory)
{
    public string ExecutablePath => Path.Combine(ExtractedDirectory, UpdateApplyPlanner.ExecutableName);
}

/// <summary>update.json の取得と、配布 zip のダウンロード、照合、展開を行う。</summary>
internal sealed class UpdateService
{
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);
    private const int MaxManifestBytes = 64 * 1024;
    private const long MaxPackageBytes = 1024L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    public async Task<UpdateCheckResult> CheckAsync(string? skippedVersion, UpdateCheckTrigger trigger, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await FetchManifestAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(DescribeNetworkFailure(exception), exception.ToString());
        }
        return UpdateCheckEvaluator.Evaluate(AppVersion.Current, json, skippedVersion, trigger);
    }

    /// <summary>ダウンロード、SHA-256 の照合、展開を行う。失敗の理由は <see cref="UpdatePackageException"/> で返す。</summary>
    public async Task<PreparedUpdate> PrepareAsync(UpdateManifest manifest, IProgress<UpdateDownloadProgress> progress, Action onVerifying, CancellationToken cancellationToken)
    {
        var zipPath = UpdatePaths.GetZipPath(manifest.Version);
        var extractDirectory = UpdatePaths.GetExtractDirectory(manifest.Version);
        try
        {
            Directory.CreateDirectory(UpdatePaths.UpdateDirectory);
            DeleteIfExists(zipPath);
            DeleteDirectoryIfExists(extractDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新用ファイルを準備できませんでした: {exception}");
            throw new UpdatePackageException("更新用の一時フォルダーを準備できませんでした。", exception);
        }

        try
        {
            await DownloadAsync(manifest.Url, zipPath, progress, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新ファイルをダウンロードできませんでした: URL={manifest.Url}; {exception}");
            TryDelete(zipPath);
            throw exception as UpdatePackageException ?? new UpdatePackageException($"ダウンロードできませんでした。{DescribeNetworkFailure(exception)}", exception);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Info(DiagnosticLogTags.Update, "更新のダウンロードをキャンセルしました。");
            TryDelete(zipPath);
            throw;
        }

        onVerifying();
        return await Task.Run(() =>
        {
            try
            {
                if (!UpdatePackage.MatchesSha256(zipPath, manifest.Sha256))
                {
                    DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新ファイルのハッシュが一致しません: 期待値={manifest.Sha256}、実際の値={UpdatePackage.ComputeSha256(zipPath)}。");
                    TryDelete(zipPath);
                    throw new UpdatePackageException("ダウンロードしたファイルが最新版情報と一致しないため、中止しました。");
                }
                var files = UpdatePackage.Extract(zipPath, extractDirectory);
                DiagnosticLog.Info(DiagnosticLogTags.Update, $"更新ファイルを展開しました: フォルダー={extractDirectory}、ファイル数={files.Count}。");
                return new PreparedUpdate(manifest, extractDirectory);
            }
            catch (Exception exception) when (exception is not UpdatePackageException)
            {
                DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新ファイルを展開できませんでした: {exception}");
                TryDeleteDirectory(extractDirectory);
                throw new UpdatePackageException("ダウンロードしたファイルを展開できませんでした。", exception);
            }
            catch (UpdatePackageException exception)
            {
                DiagnosticLog.Error(DiagnosticLogTags.Update, $"更新ファイルを受け付けませんでした: {exception.Message}");
                TryDeleteDirectory(extractDirectory);
                throw;
            }
        }, CancellationToken.None);
    }

    private static async Task<string> FetchManifestAsync(CancellationToken cancellationToken)
    {
        // ResponseHeadersRead では HttpClient.Timeout が本文の読み取りに効かないため、読み切りまでを自前で打ち切る。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ManifestTimeout);
        try
        {
            using var response = await SendWithAllowedRedirectsAsync(UpdateManifestParser.ManifestUrl, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            var buffer = new byte[MaxManifestBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return Encoding.UTF8.GetString(buffer, 0, total).TrimStart((char)0xFEFF);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("15 秒以内に応答がありませんでした。");
        }
    }

    private async Task DownloadAsync(string url, string zipPath, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(DownloadStallTimeout);
        try
        {
            using var response = await SendWithAllowedRedirectsAsync(url, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > MaxPackageBytes) throw new UpdatePackageException("ダウンロードするファイルが大きすぎるため、中止しました。");
            var partialPath = zipPath + ".partial";
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                var reported = Stopwatch.StartNew();
                progress.Report(new UpdateDownloadProgress(0, total));
                while (true)
                {
                    stall.CancelAfter(DownloadStallTimeout);
                    var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > MaxPackageBytes) throw new UpdatePackageException("ダウンロードするファイルが大きすぎるため、中止しました。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    if (reported.ElapsedMilliseconds >= 200)
                    {
                        progress.Report(new UpdateDownloadProgress(received, total));
                        reported.Restart();
                    }
                }
                progress.Report(new UpdateDownloadProgress(received, total));
            }
            File.Move(partialPath, zipPath, overwrite: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(zipPath + ".partial");
            throw new TimeoutException("60 秒以上データが届きませんでした。");
        }
        catch
        {
            TryDelete(zipPath + ".partial");
            throw;
        }
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ScreenRecorder", UpdateVersion.TryParseApplicationVersion(AppVersion.Current, out var version) ? version.ToString() : "0.0.0"));
        return request;
    }

    private static async Task<HttpResponseMessage> SendWithAllowedRedirectsAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var currentUri))
            throw new UpdatePackageException("更新サーバーの URL が正しくありません。");

        for (var redirectsFollowed = 0; ; redirectsFollowed++)
        {
            using var request = CreateRequest(currentUri.AbsoluteUri);
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399) return response;

            Uri? location;
            try { location = response.Headers.Location; }
            catch (FormatException exception)
            {
                response.Dispose();
                throw new UpdatePackageException("配布サーバーの転送先を確認できませんでした。", exception);
            }
            if (!UpdateRedirectPolicy.TryResolve(currentUri, location, redirectsFollowed, out var target, out var error))
            {
                response.Dispose();
                throw new UpdatePackageException(error ?? "配布サーバーの転送先を確認できませんでした。");
            }
            response.Dispose();
            currentUri = target!;
        }
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static string DescribeNetworkFailure(Exception exception) => exception switch
    {
        TimeoutException timeout => timeout.Message,
        HttpRequestException { StatusCode: { } status } => $"配布サーバーが {(int)status} を返しました。",
        HttpRequestException => "配布サーバーに接続できませんでした。ネットワークの接続を確認してください。",
        UpdatePackageException package => package.Message,
        IOException or UnauthorizedAccessException => "ダウンロードしたファイルを保存できませんでした。",
        _ => "予期しないエラーが発生しました。"
    };

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private void TryDelete(string path)
    {
        try { DeleteIfExists(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新用ファイルを削除できませんでした: ファイル={path}; {exception.Message}"); }
    }

    private void TryDeleteDirectory(string path)
    {
        try { DeleteDirectoryIfExists(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DiagnosticLog.Warn(DiagnosticLogTags.Update, $"更新用フォルダーを削除できませんでした: フォルダー={path}; {exception.Message}"); }
    }

}
