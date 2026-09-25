using System.Drawing;
using System.Drawing.Imaging;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class PngWriterTests
{
    [Theory]
    [InlineData(PngCompression.Fast)]
    [InlineData(PngCompression.Standard)]
    [InlineData(PngCompression.Smallest)]
    public void Write_CanBeReadBackWithMatchingRgbPixels(PngCompression compression)
    {
        byte[] pixels =
        [
            30, 20, 10, 255, 60, 50, 40, 0,
            90, 80, 70, 128, 120, 110, 100, 255
        ];
        using var png = new MemoryStream();
        PngWriter.Write(png, 2, 2, pixels, compression);
        png.Position = 0;

        using var bitmap = new Bitmap(png);

        Assert.Equal(Color.FromArgb(10, 20, 30), bitmap.GetPixel(0, 0));
        Assert.Equal(Color.FromArgb(40, 50, 60), bitmap.GetPixel(1, 0));
        Assert.Equal(Color.FromArgb(70, 80, 90), bitmap.GetPixel(0, 1));
        Assert.Equal(Color.FromArgb(100, 110, 120), bitmap.GetPixel(1, 1));
    }

    [Fact]
    public void Write_EmitsRgbPngChunks()
    {
        using var png = new MemoryStream();
        PngWriter.Write(png, 1, 1, [3, 2, 1, 255], PngCompression.Fast);

        var bytes = png.ToArray();
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(2, bytes[25]);
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));
    }
}
