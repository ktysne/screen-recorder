namespace ScreenRecorder.App;

internal static class ClipboardImageWriter
{
    // image は別のスレッドから読むため、呼び出し側は返した Task の完了まで破棄しない。
    internal static Task SetImageAsync(Image image)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Clipboard.SetImage(image);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
