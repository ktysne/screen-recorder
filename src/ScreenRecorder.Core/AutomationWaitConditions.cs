using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenRecorder.Core;

public static partial class AutomationWaitConditions
{
    public static bool Matches(string recordingState, DateTimeOffset? lastCaptureAt, AutomationWaitForParams wait) =>
        string.Equals(recordingState, wait.State, StringComparison.Ordinal)
        && (wait.CaptureAfter is null || lastCaptureAt > wait.CaptureAfter.Value);

    // 日付だけや地域の表記を受け付けると、時刻の抜けた条件で待ってしまうため、時刻と時差を含む ISO 8601 に限る。
    public static bool TryParseCaptureAfter(string? value, out DateTimeOffset captureAfter)
    {
        captureAfter = default;
        return value is not null
            && Iso8601DateTimeWithOffset().IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out captureAfter);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Iso8601DateTimeWithOffset();
}
