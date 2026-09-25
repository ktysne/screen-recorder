namespace ScreenRecorder.Core;

public enum UpdateCheckTrigger { Automatic, Manual }

public enum UpdateCheckKind { UpToDate, Available, Skipped, Failed }

/// <summary><see cref="Manifest"/> は <see cref="UpdateCheckKind.Failed"/> 以外で、<see cref="Error"/> は失敗のときだけ値を持つ。</summary>
public sealed record UpdateCheckResult(UpdateCheckKind Kind, UpdateManifest? Manifest, string? Error)
{
    public static UpdateCheckResult Failed(string error) => new(UpdateCheckKind.Failed, null, error);
}

public static class UpdateCheckEvaluator
{
    /// <summary>スキップした版は自動の確認でだけ <see cref="UpdateCheckKind.Skipped"/> にし、手動の確認では通知の対象に残す。</summary>
    public static UpdateCheckResult Evaluate(string? currentVersionText, string? manifestJson, string? skippedVersion, UpdateCheckTrigger trigger)
    {
        if (!UpdateVersion.TryParseApplicationVersion(currentVersionText, out var current))
            return UpdateCheckResult.Failed("実行中の版を判別できませんでした。");
        var parsed = UpdateManifestParser.Parse(manifestJson);
        if (parsed.Manifest is not { } manifest) return UpdateCheckResult.Failed(parsed.Error ?? "最新版情報を読めませんでした。");
        if (manifest.Version <= current) return new UpdateCheckResult(UpdateCheckKind.UpToDate, manifest, null);
        if (trigger == UpdateCheckTrigger.Automatic && IsSkipped(skippedVersion, manifest.Version))
            return new UpdateCheckResult(UpdateCheckKind.Skipped, manifest, null);
        return new UpdateCheckResult(UpdateCheckKind.Available, manifest, null);
    }

    /// <summary>自動の確認は新しい版があるときだけ知らせる。失敗を毎回知らせると、オフラインの間に通知が繰り返されるため。</summary>
    public static bool ShouldNotify(UpdateCheckResult result, UpdateCheckTrigger trigger) =>
        trigger == UpdateCheckTrigger.Manual || result.Kind == UpdateCheckKind.Available;

    private static bool IsSkipped(string? skippedVersion, UpdateVersion latest) =>
        UpdateVersion.TryParse(skippedVersion?.Trim(), out var skipped) && skipped == latest;
}

/// <summary>自動の確認の時期。時刻は起動からの経過時間(スリープ中も進む単調な時計)で渡す。</summary>
public static class UpdateCheckSchedule
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static bool IsAutomaticCheckDue(bool enabled, TimeSpan elapsedSinceStart, TimeSpan? lastAutomaticCheckAt)
    {
        if (!enabled) return false;
        var due = lastAutomaticCheckAt is { } last ? last + Interval : InitialDelay;
        return elapsedSinceStart >= due;
    }
}
