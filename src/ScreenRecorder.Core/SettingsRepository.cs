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
            result.StartWithWindows = Bool(root, nameof(result.StartWithWindows), defaults.StartWithWindows);
            result.CheckForUpdatesAutomatically = Bool(root, nameof(result.CheckForUpdatesAutomatically), defaults.CheckForUpdatesAutomatically);
            result.NotifyWhenSaved = Bool(root, nameof(result.NotifyWhenSaved), defaults.NotifyWhenSaved);
            result.PlayCaptureSound = Bool(root, nameof(result.PlayCaptureSound), defaults.PlayCaptureSound);
            result.FileNameTemplate = String(root, nameof(result.FileNameTemplate), defaults.FileNameTemplate);
            result.OrganizeByMonth = Bool(root, nameof(result.OrganizeByMonth), defaults.OrganizeByMonth);
            result.StillImageDirectory = String(root, nameof(result.StillImageDirectory), defaults.StillImageDirectory);
            result.ImageFormat = EnumValue(root, nameof(result.ImageFormat), defaults.ImageFormat);
            result.JpegQuality = Ranged(root, nameof(result.JpegQuality), defaults.JpegQuality, 1, 100);
            result.PngCompression = EnumValue(root, nameof(result.PngCompression), defaults.PngCompression);
            result.CopyImageToClipboard = Bool(root, nameof(result.CopyImageToClipboard), defaults.CopyImageToClipboard);
            result.CaptureImageCursor = Bool(root, nameof(result.CaptureImageCursor), defaults.CaptureImageCursor);
            result.CaptureDelaySeconds = Choice(root, nameof(result.CaptureDelaySeconds), defaults.CaptureDelaySeconds, 0, 3, 5, 10);
            result.AfterCaptureAction = EnumValue(root, nameof(result.AfterCaptureAction), defaults.AfterCaptureAction);
            result.VideoDirectory = String(root, nameof(result.VideoDirectory), defaults.VideoDirectory);
            result.FrameRate = Choice(root, nameof(result.FrameRate), defaults.FrameRate, 15, 24, 30, 60);
            result.VideoBitrateMbps = Ranged(root, nameof(result.VideoBitrateMbps), defaults.VideoBitrateMbps, 1, 100);
            result.CaptureVideoCursor = Bool(root, nameof(result.CaptureVideoCursor), defaults.CaptureVideoCursor);
            result.CountdownSeconds = Choice(root, nameof(result.CountdownSeconds), defaults.CountdownSeconds, 0, 3, 5);
            result.OutputScalePercent = Choice(root, nameof(result.OutputScalePercent), defaults.OutputScalePercent, 100, 75, 50);
            result.HighlightClicks = Bool(root, nameof(result.HighlightClicks), defaults.HighlightClicks);
            result.Encoder = EnumValue(root, nameof(result.Encoder), defaults.Encoder);
            result.CaptureSystemAudio = Bool(root, nameof(result.CaptureSystemAudio), defaults.CaptureSystemAudio);
            result.CaptureMicrophone = Bool(root, nameof(result.CaptureMicrophone), defaults.CaptureMicrophone);
            result.MicrophoneDeviceId = NullableString(root, nameof(result.MicrophoneDeviceId), defaults.MicrophoneDeviceId);
            result.AudioFormat = EnumValue(root, nameof(result.AudioFormat), defaults.AudioFormat);
            result.AacBitrateKbps = Choice(root, nameof(result.AacBitrateKbps), defaults.AacBitrateKbps, 96, 128, 160, 192);
            result.Mp3BitrateKbps = Choice(root, nameof(result.Mp3BitrateKbps), defaults.Mp3BitrateKbps, 128, 192, 256, 320);
            result.ScreenshotRegionShortcut = String(root, nameof(result.ScreenshotRegionShortcut), defaults.ScreenshotRegionShortcut);
            result.ScreenshotFullScreenShortcut = String(root, nameof(result.ScreenshotFullScreenShortcut), defaults.ScreenshotFullScreenShortcut);
            result.ScreenshotWindowShortcut = String(root, nameof(result.ScreenshotWindowShortcut), defaults.ScreenshotWindowShortcut);
            result.RecordingRegionShortcut = String(root, nameof(result.RecordingRegionShortcut), defaults.RecordingRegionShortcut);
            result.RecordingFullScreenShortcut = String(root, nameof(result.RecordingFullScreenShortcut), defaults.RecordingFullScreenShortcut);
            result.RecordingWindowShortcut = String(root, nameof(result.RecordingWindowShortcut), defaults.RecordingWindowShortcut);
            result.PauseRecordingShortcut = String(root, nameof(result.PauseRecordingShortcut), defaults.PauseRecordingShortcut);
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
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } }), new UTF8Encoding(false));
    }

    private static JsonElement? Property(JsonElement root, string name) => root.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(name), out var value) ? value : null;
    private static bool Bool(JsonElement root, string name, bool fallback) => Property(root, name) is { ValueKind: JsonValueKind.True } ? true : Property(root, name) is { ValueKind: JsonValueKind.False } ? false : fallback;
    private static string String(JsonElement root, string name, string fallback) => Property(root, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : fallback;
    private static string? NullableString(JsonElement root, string name, string? fallback) => Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : fallback;
    private static int Ranged(JsonElement root, string name, int fallback, int min, int max) => Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) && number >= min && number <= max ? number : fallback;
    private static int Choice(JsonElement root, string name, int fallback, params int[] choices) => Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) && choices.Contains(number) ? number : fallback;
    private static T EnumValue<T>(JsonElement root, string name, T fallback) where T : struct, Enum => Property(root, name) is { ValueKind: JsonValueKind.String } value && Enum.TryParse<T>(value.GetString(), true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}
