using ScreenRecorder.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace ScreenRecorder.Capture;

public sealed record MediaVideoInfo(string Subtype, uint Width, uint Height, uint FrameRateNumerator, uint FrameRateDenominator, uint Bitrate);
public sealed record MediaAudioInfo(string Subtype, uint ChannelCount, uint SampleRate, uint Bitrate);
public sealed record MediaFileInfo(MediaVideoInfo Video, MediaAudioInfo? Audio, TimeSpan Duration);

public static class MediaFileProbe
{
    public static Task<MediaFileInfo> InspectAsync(string path) => Task.Run(async () =>
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
        var video = profile.Video;
        var audio = profile.Audio;
        var properties = await file.Properties.GetVideoPropertiesAsync();
        return new MediaFileInfo(
            new(video.Subtype, video.Width, video.Height, video.FrameRate.Numerator, video.FrameRate.Denominator, video.Bitrate),
            audio is null ? null : new(audio.Subtype, audio.ChannelCount, audio.SampleRate, audio.Bitrate),
            properties.Duration);
    });

    public static Task WriteFramePngAsync(string path, TimeSpan position, string outputPath) => Task.Run(async () =>
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(position, 0, 0, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        await using var output = File.Create(outputPath);
        PngWriter.Write(output, checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight),
            pixels.DetachPixelData(), PngCompression.Standard);
    });
}
