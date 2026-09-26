namespace ScreenRecorder.Core;

public enum UpdateCheckTrigger { Automatic, Manual }

public enum UpdateCheckKind { UpToDate, Available, Skipped, Failed }

/// <summary><see cref="Manifest"/> は <see cref="UpdateCheckKind.Failed"/> 以外で、<see cref="Error"/> は失敗のときだけ値を持つ。</summary>
public sealed record UpdateCheckResult(UpdateCheckKind Kind, UpdateManifest? Manifest, string? Error)
{
    public string? DiagnosticDetail { get; init; }

    public static UpdateCheckResult Failed(string error, string? diagnosticDetail = null) =>
        new(UpdateCheckKind.Failed, null, error) { DiagnosticDetail = diagnosticDetail };
}

public static class UpdateCheckLog
{
    public static string TriggerName(UpdateCheckTrigger trigger) => trigger == UpdateCheckTrigger.Manual ? "手動" : "自動";

    public static DiagnosticLogLevel FailureLevel(UpdateCheckTrigger trigger) =>
        trigger == UpdateCheckTrigger.Manual ? DiagnosticLogLevel.Error : DiagnosticLogLevel.Warn;

    public static string FailureMessage(UpdateCheckTrigger trigger, string? detail) =>
        $"更新の確認に失敗しました: きっかけ={TriggerName(trigger)}; {detail}";

    public static string CompletedMessage(UpdateCheckTrigger trigger, UpdateCheckResult result, string? currentVersion)
    {
        var hasNewVersion = result.Kind is UpdateCheckKind.Available or UpdateCheckKind.Skipped;
        return $"更新の確認が完了しました: きっかけ={TriggerName(trigger)}、新しい版={(hasNewVersion ? "あり" : "なし")}、現在の版={currentVersion}、最新の版={result.Manifest?.Version}。";
    }

    public static void Log(UpdateCheckTrigger trigger, UpdateCheckResult result, string? currentVersion)
    {
        if (result.Kind == UpdateCheckKind.Failed)
        {
            LogFailure(trigger, result.DiagnosticDetail ?? result.Error);
            return;
        }

        DiagnosticLog.Info(DiagnosticLogTags.Update, CompletedMessage(trigger, result, currentVersion));
    }

    public static void LogFailure(UpdateCheckTrigger trigger, string? detail)
    {
        var message = FailureMessage(trigger, detail);
        if (FailureLevel(trigger) == DiagnosticLogLevel.Error) DiagnosticLog.Error(DiagnosticLogTags.Update, message);
        else DiagnosticLog.Warn(DiagnosticLogTags.Update, message);
    }
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

    /// <summary>失敗の多くは起動直後の未接続のような一時的なものなので、失敗したときだけ短い間隔で試し直す。</summary>
    public static readonly TimeSpan RetryIntervalAfterFailure = TimeSpan.FromHours(1);

    /// <param name="lastAutomaticCheckFailed">直前の自動の確認が「確認できなかった」で終わったか。確認中は false として渡す。</param>
    public static bool IsAutomaticCheckDue(bool enabled, TimeSpan elapsedSinceStart, TimeSpan? lastAutomaticCheckAt, bool lastAutomaticCheckFailed)
    {
        if (!enabled) return false;
        var due = lastAutomaticCheckAt is { } last
            ? last + (lastAutomaticCheckFailed ? RetryIntervalAfterFailure : Interval)
            : InitialDelay;
        return elapsedSinceStart >= due;
    }
}
