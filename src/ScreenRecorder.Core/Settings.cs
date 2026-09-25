namespace ScreenRecorder.Core;

public enum StillImageFormat { Jpeg, Png }
public enum PngCompression { Fast, Standard, Smallest }
public enum CaptureAfterAction { None, OpenFile, OpenFolder }
public enum AudioFormat { Aac, Mp3 }
public enum EncoderMode { Automatic, SoftwareOnly }

public sealed class Settings
{
    public bool StartWithWindows { get; set; } = true;
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public bool NotifyWhenSaved { get; set; } = true;
    public bool PlayCaptureSound { get; set; }
    public string FileNameTemplate { get; set; } = "ScreenRecorder_{date}_{time}";
    public bool OrganizeByMonth { get; set; }

    public string StillImageDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenRecorder");
    public StillImageFormat ImageFormat { get; set; } = StillImageFormat.Jpeg;
    public int JpegQuality { get; set; } = 98;
    public PngCompression PngCompression { get; set; } = PngCompression.Standard;
    public bool CopyImageToClipboard { get; set; } = true;
    public bool CaptureImageCursor { get; set; }
    public int CaptureDelaySeconds { get; set; }
    public CaptureAfterAction AfterCaptureAction { get; set; } = CaptureAfterAction.None;

    public string VideoDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
    public int FrameRate { get; set; } = 30;
    public int VideoBitrateMbps { get; set; } = 12;
    public bool CaptureVideoCursor { get; set; } = true;
    public int CountdownSeconds { get; set; } = 3;
    public int OutputScalePercent { get; set; } = 100;
    public bool HighlightClicks { get; set; }
    public EncoderMode Encoder { get; set; } = EncoderMode.Automatic;

    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Aac;
    public int AacBitrateKbps { get; set; } = 192;
    public int Mp3BitrateKbps { get; set; } = 192;

    public string ScreenshotRegionShortcut { get; set; } = "PrtSc";
    public string ScreenshotFullScreenShortcut { get; set; } = "Ctrl+PrtSc";
    public string ScreenshotWindowShortcut { get; set; } = "Alt+PrtSc";
    public string RecordingRegionShortcut { get; set; } = "Shift+PrtSc";
    public string RecordingFullScreenShortcut { get; set; } = "Ctrl+Shift+PrtSc";
    public string RecordingWindowShortcut { get; set; } = "Alt+Shift+PrtSc";
    public string PauseRecordingShortcut { get; set; } = "";

    public string? SkippedUpdateVersion { get; set; }

    public Settings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        CheckForUpdatesAutomatically = CheckForUpdatesAutomatically,
        NotifyWhenSaved = NotifyWhenSaved,
        PlayCaptureSound = PlayCaptureSound,
        FileNameTemplate = FileNameTemplate,
        OrganizeByMonth = OrganizeByMonth,
        StillImageDirectory = StillImageDirectory,
        ImageFormat = ImageFormat,
        JpegQuality = JpegQuality,
        PngCompression = PngCompression,
        CopyImageToClipboard = CopyImageToClipboard,
        CaptureImageCursor = CaptureImageCursor,
        CaptureDelaySeconds = CaptureDelaySeconds,
        AfterCaptureAction = AfterCaptureAction,
        VideoDirectory = VideoDirectory,
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
        SkippedUpdateVersion = SkippedUpdateVersion
    };
}
