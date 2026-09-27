using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Capture.Tests;

public sealed class RecordingFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_UsesNextAvailablePathWhenDestinationExists()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.mp4");
            var occupied = Path.Combine(directory, "occupied.mp4");
            var available = Path.Combine(directory, "available.mp4");
            await File.WriteAllTextAsync(source, "recording");
            await File.WriteAllTextAsync(occupied, "existing");

            var result = await RecordingFinalizer.FinalizeAsync(source, new Settings(), occupied,
                () => available, new PassthroughProcessor(), CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(available, result.FinalPath);
            Assert.Equal("existing", await File.ReadAllTextAsync(occupied));
            Assert.Equal("recording", await File.ReadAllTextAsync(available));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeAsync_RetainsTemporaryFileWhenCompletedPathIsMissing()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var temporaryPath = Path.Combine(directory, "temporary.mp4");
            await File.WriteAllTextAsync(temporaryPath, "recording");
            var result = await RecordingFinalizer.FinalizeAsync(Path.Combine(directory, "missing.mp4"),
                new Settings(), Path.Combine(directory, "final.mp4"), null,
                new PassthroughProcessor(), CancellationToken.None, temporaryPath);

            Assert.NotNull(result.Error);
            Assert.Equal(temporaryPath, result.RetainedPath);
            Assert.True(File.Exists(temporaryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PassthroughProcessor : IVideoRecordingPostProcessor
    {
        public Task<VideoPostProcessResult> ProcessAsync(string temporaryPath, Settings settings, CancellationToken cancellationToken) =>
            Task.FromResult(new VideoPostProcessResult(temporaryPath, null));
    }
}
