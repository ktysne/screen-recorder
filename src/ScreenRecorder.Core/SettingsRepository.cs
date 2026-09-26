using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRecorder.Core;

public sealed class SettingsRepository(string? baseDirectory = null)
{
    private readonly string _directory = baseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder");
    private string FilePath => Path.Combine(_directory, "settings.json");

    public Settings Load()
    {
        if (!File.Exists(FilePath)) return new Settings();
        string json;
        try { json = File.ReadAllText(FilePath); }
        catch (IOException) { return new Settings(); }
        catch (UnauthorizedAccessException) { return new Settings(); }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var defaults = new Settings();
            var result = new Settings();
            var root = document.RootElement;
            result.DiagnosticLogLevel = DiagnosticLogLevels.FromSettingName(Property(root, nameof(result.DiagnosticLogLevel)) is { ValueKind: JsonValueKind.String } logLevel ? logLevel.GetString() : null);
            result.StartWithWindows = Bool(root, nameof(result.StartWithWindows), defaults.StartWithWindows);
            result.CheckForUpdatesAutomatically = Bool(root, nameof(result.CheckForUpdatesAutomatically), defaults.CheckForUpdatesAutomatically);
            result.NotifyWhenSaved = Bool(root, nameof(result.NotifyWhenSaved), defaults.NotifyWhenSaved);
            result.PlayCaptureSound = Bool(root, nameof(result.PlayCaptureSound), defaults.PlayCaptureSound);
            result.FileNameTemplate = Text(root, nameof(result.FileNameTemplate), defaults.FileNameTemplate);
            result.OrganizeByMonth = Bool(root, nameof(result.OrganizeByMonth), defaults.OrganizeByMonth);
            result.StillImageDirectory = String(root, nameof(result.StillImageDirectory), defaults.StillImageDirectory);
            result.ImageFormat = EnumValue(root, nameof(result.ImageFormat), defaults.ImageFormat);
            result.PngCompression = EnumValue(root, nameof(result.PngCompression), defaults.PngCompression);
            result.CopyImageToClipboard = Bool(root, nameof(result.CopyImageToClipboard), defaults.CopyImageToClipboard);
            result.CaptureImageCursor = Bool(root, nameof(result.CaptureImageCursor), defaults.CaptureImageCursor);
            result.AfterCaptureAction = EnumValue(root, nameof(result.AfterCaptureAction), defaults.AfterCaptureAction);
            result.VideoDirectory = String(root, nameof(result.VideoDirectory), defaults.VideoDirectory);
            result.CaptureVideoCursor = Bool(root, nameof(result.CaptureVideoCursor), defaults.CaptureVideoCursor);
            result.HighlightClicks = Bool(root, nameof(result.HighlightClicks), defaults.HighlightClicks);
            result.Encoder = EnumValue(root, nameof(result.Encoder), defaults.Encoder);
            result.CaptureSystemAudio = Bool(root, nameof(result.CaptureSystemAudio), defaults.CaptureSystemAudio);
            result.CaptureMicrophone = Bool(root, nameof(result.CaptureMicrophone), defaults.CaptureMicrophone);
            result.MicrophoneDeviceId = NullableString(root, nameof(result.MicrophoneDeviceId), defaults.MicrophoneDeviceId);
            result.AudioFormat = EnumValue(root, nameof(result.AudioFormat), defaults.AudioFormat);
            foreach (var setting in SettingsSchema.IntSettings)
            {
                var property = Property(root, setting.Name);
                var value = property is { ValueKind: JsonValueKind.Number } number
                    && number.TryGetInt32(out var parsed)
                    && setting.IsValid(parsed)
                        ? parsed
                        : setting.Default;
                setting.Set(result, value);
            }
            result.ScreenshotRegionShortcut = Shortcut(root, nameof(result.ScreenshotRegionShortcut), defaults.ScreenshotRegionShortcut);
            result.ScreenshotFullScreenShortcut = Shortcut(root, nameof(result.ScreenshotFullScreenShortcut), defaults.ScreenshotFullScreenShortcut);
            result.ScreenshotWindowShortcut = Shortcut(root, nameof(result.ScreenshotWindowShortcut), defaults.ScreenshotWindowShortcut);
            result.RecordingRegionShortcut = Shortcut(root, nameof(result.RecordingRegionShortcut), defaults.RecordingRegionShortcut);
            result.RecordingFullScreenShortcut = Shortcut(root, nameof(result.RecordingFullScreenShortcut), defaults.RecordingFullScreenShortcut);
            result.RecordingWindowShortcut = Shortcut(root, nameof(result.RecordingWindowShortcut), defaults.RecordingWindowShortcut);
            result.PauseRecordingShortcut = Shortcut(root, nameof(result.PauseRecordingShortcut), defaults.PauseRecordingShortcut);
            result.ScreenshotRegionEnabled = Bool(root, nameof(result.ScreenshotRegionEnabled), defaults.ScreenshotRegionEnabled);
            result.ScreenshotFullScreenEnabled = Bool(root, nameof(result.ScreenshotFullScreenEnabled), defaults.ScreenshotFullScreenEnabled);
            result.ScreenshotWindowEnabled = Bool(root, nameof(result.ScreenshotWindowEnabled), defaults.ScreenshotWindowEnabled);
            result.RecordingRegionEnabled = Bool(root, nameof(result.RecordingRegionEnabled), defaults.RecordingRegionEnabled);
            result.RecordingFullScreenEnabled = Bool(root, nameof(result.RecordingFullScreenEnabled), defaults.RecordingFullScreenEnabled);
            result.RecordingWindowEnabled = Bool(root, nameof(result.RecordingWindowEnabled), defaults.RecordingWindowEnabled);
            result.PauseRecordingEnabled = Bool(root, nameof(result.PauseRecordingEnabled), defaults.PauseRecordingEnabled);
            result.SkippedUpdateVersion = NullableString(root, nameof(result.SkippedUpdateVersion), defaults.SkippedUpdateVersion);
            return result;
        }
        catch (JsonException)
        {
            try { File.Copy(FilePath, FilePath + ".bak", true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new Settings();
        }
    }

    public void Save(Settings settings)
    {
        foreach (var setting in SettingsSchema.IntSettings)
        {
            if (!setting.IsValid(setting.Get(settings)))
                throw new ArgumentException($"設定 {setting.Name} の値が許容範囲外です。", nameof(settings));
        }

        Directory.CreateDirectory(_directory);
        // 書き込み途中で失敗しても既存の設定を残すため、一時ファイルに書き終えてから置き換える。
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new DiagnosticLogLevelJsonConverter(), new JsonStringEnumConverter() } }), new UTF8Encoding(false));
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    private static JsonElement? Property(JsonElement root, string name) => root.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(name), out var value) ? value : null;
    private static bool Bool(JsonElement root, string name, bool fallback) => Property(root, name) is { ValueKind: JsonValueKind.True } ? true : Property(root, name) is { ValueKind: JsonValueKind.False } ? false : fallback;
    private static string String(JsonElement root, string name, string fallback) => Property(root, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : fallback;
    private static string Text(JsonElement root, string name, string fallback) => Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : fallback;
    // 空文字は利用者が割り当てを外した状態なので、既定のキーへ戻さない。
    private static string Shortcut(JsonElement root, string name, string fallback) => Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : fallback;
    private static string? NullableString(JsonElement root, string name, string? fallback) => Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : fallback;
    private static T EnumValue<T>(JsonElement root, string name, T fallback) where T : struct, Enum => Property(root, name) is { ValueKind: JsonValueKind.String } value && Enum.TryParse<T>(value.GetString(), true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private sealed class DiagnosticLogLevelJsonConverter : JsonConverter<DiagnosticLogLevel>
    {
        public override DiagnosticLogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => DiagnosticLogLevels.FromSettingName(reader.TokenType == JsonTokenType.String ? reader.GetString() : null);

        public override void Write(Utf8JsonWriter writer, DiagnosticLogLevel value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToSettingName());
    }
}
