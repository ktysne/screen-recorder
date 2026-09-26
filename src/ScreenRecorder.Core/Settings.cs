namespace ScreenRecorder.Core;

public enum StillImageFormat { Jpeg, Png }
public enum PngCompression { Fast, Standard, Smallest }
public enum CaptureAfterAction { None, OpenFile, OpenFolder }
public enum WindowScreenshotShortcutTarget { ActiveWindow, SelectWindow }
public enum AudioFormat { Aac, Mp3 }
public enum EncoderMode { Automatic, SoftwareOnly }

public sealed class Settings
{
    public DiagnosticLogLevel DiagnosticLogLevel { get; set; } = global::ScreenRecorder.Core.DiagnosticLogLevel.Info;
    public bool StartWithWindows { get; set; } = true;
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public bool NotifyWhenSaved { get; set; } = true;
    public bool PlayCaptureSound { get; set; }
    public string FileNameTemplate { get; set; } = "ScreenRecorder_{date}_{time}";
    public bool OrganizeByMonth { get; set; }

    public string StillImageDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenRecorder");
    public string? ConfirmedStillImageDirectory { get; set; }
    public StillImageFormat ImageFormat { get; set; } = StillImageFormat.Jpeg;
    public int JpegQuality { get; set; } = SettingsSchema.JpegQuality.Default;
    public PngCompression PngCompression { get; set; } = PngCompression.Standard;
    public bool CopyImageToClipboard { get; set; } = true;
    public bool CaptureImageCursor { get; set; }
    public WindowScreenshotShortcutTarget WindowScreenshotShortcutTarget { get; set; } = WindowScreenshotShortcutTarget.ActiveWindow;
    public int CaptureDelaySeconds { get; set; } = SettingsSchema.CaptureDelaySeconds.Default;
    public CaptureAfterAction AfterCaptureAction { get; set; } = CaptureAfterAction.None;

    public string VideoDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
    public string? ConfirmedVideoDirectory { get; set; }
    public int FrameRate { get; set; } = SettingsSchema.FrameRate.Default;
    public int VideoBitrateMbps { get; set; } = SettingsSchema.VideoBitrateMbps.Default;
    public bool CaptureVideoCursor { get; set; } = true;
    public int CountdownSeconds { get; set; } = SettingsSchema.CountdownSeconds.Default;
    public int OutputScalePercent { get; set; } = SettingsSchema.OutputScalePercent.Default;
    public bool HighlightClicks { get; set; }
    public EncoderMode Encoder { get; set; } = EncoderMode.Automatic;

    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Aac;
    public int AacBitrateKbps { get; set; } = SettingsSchema.AacBitrateKbps.Default;
    public int Mp3BitrateKbps { get; set; } = SettingsSchema.Mp3BitrateKbps.Default;

    public string ScreenshotRegionShortcut { get; set; } = "PrtSc";
    public string ScreenshotFullScreenShortcut { get; set; } = "Ctrl+PrtSc";
    public string ScreenshotWindowShortcut { get; set; } = "Alt+PrtSc";
    public string RecordingRegionShortcut { get; set; } = "Shift+PrtSc";
    public string RecordingFullScreenShortcut { get; set; } = "Ctrl+Shift+PrtSc";
    public string RecordingWindowShortcut { get; set; } = "Alt+Shift+PrtSc";
    public string PauseRecordingShortcut { get; set; } = "";
    public bool ScreenshotRegionEnabled { get; set; } = true;
    public bool ScreenshotFullScreenEnabled { get; set; } = true;
    public bool ScreenshotWindowEnabled { get; set; } = true;
    public bool RecordingRegionEnabled { get; set; } = true;
    public bool RecordingFullScreenEnabled { get; set; } = true;
    public bool RecordingWindowEnabled { get; set; } = true;
    public bool PauseRecordingEnabled { get; set; } = true;

    public string? SkippedUpdateVersion { get; set; }

    public Settings Clone() => new()
    {
        DiagnosticLogLevel = DiagnosticLogLevel,
        StartWithWindows = StartWithWindows,
        CheckForUpdatesAutomatically = CheckForUpdatesAutomatically,
        NotifyWhenSaved = NotifyWhenSaved,
        PlayCaptureSound = PlayCaptureSound,
        FileNameTemplate = FileNameTemplate,
        OrganizeByMonth = OrganizeByMonth,
        StillImageDirectory = StillImageDirectory,
        ConfirmedStillImageDirectory = ConfirmedStillImageDirectory,
        ImageFormat = ImageFormat,
        JpegQuality = JpegQuality,
        PngCompression = PngCompression,
        CopyImageToClipboard = CopyImageToClipboard,
        CaptureImageCursor = CaptureImageCursor,
        WindowScreenshotShortcutTarget = WindowScreenshotShortcutTarget,
        CaptureDelaySeconds = CaptureDelaySeconds,
        AfterCaptureAction = AfterCaptureAction,
        VideoDirectory = VideoDirectory,
        ConfirmedVideoDirectory = ConfirmedVideoDirectory,
        FrameRate = FrameRate,
        VideoBitrateMbps = VideoBitrateMbps,
        CaptureVideoCursor = CaptureVideoCursor,
        CountdownSeconds = CountdownSeconds,
        OutputScalePercent = OutputScalePercent,
        HighlightClicks = HighlightClicks,
        Encoder = Encoder,
        CaptureSystemAudio = CaptureSystemAudio,
        CaptureMicrophone = CaptureMicrophone,
        MicrophoneDeviceId = MicrophoneDeviceId,
        AudioFormat = AudioFormat,
        AacBitrateKbps = AacBitrateKbps,
        Mp3BitrateKbps = Mp3BitrateKbps,
        ScreenshotRegionShortcut = ScreenshotRegionShortcut,
        ScreenshotFullScreenShortcut = ScreenshotFullScreenShortcut,
        ScreenshotWindowShortcut = ScreenshotWindowShortcut,
        RecordingRegionShortcut = RecordingRegionShortcut,
        RecordingFullScreenShortcut = RecordingFullScreenShortcut,
        RecordingWindowShortcut = RecordingWindowShortcut,
        PauseRecordingShortcut = PauseRecordingShortcut,
        ScreenshotRegionEnabled = ScreenshotRegionEnabled,
        ScreenshotFullScreenEnabled = ScreenshotFullScreenEnabled,
        ScreenshotWindowEnabled = ScreenshotWindowEnabled,
        RecordingRegionEnabled = RecordingRegionEnabled,
        RecordingFullScreenEnabled = RecordingFullScreenEnabled,
        RecordingWindowEnabled = RecordingWindowEnabled,
        PauseRecordingEnabled = PauseRecordingEnabled,
        SkippedUpdateVersion = SkippedUpdateVersion
    };
}
