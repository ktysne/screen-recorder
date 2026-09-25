using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal interface IVideoRecordingPostProcessor
{
    Task<string> ProcessAsync(string temporaryPath, Settings settings, CancellationToken cancellationToken);
}

internal sealed class PassthroughVideoRecordingPostProcessor : IVideoRecordingPostProcessor
{
    public Task<string> ProcessAsync(string temporaryPath, Settings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(temporaryPath);
    }
}
