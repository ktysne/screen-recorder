namespace ScreenRecorder.App;

/// <summary>exe に埋め込んだアプリのアイコンを、表示先の大きさに合った画像で取り出す。</summary>
internal static class AppIcon
{
    private const string ResourceName = "ScreenRecorder.App.app.ico";

    public static Icon Create(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"埋め込みリソース {ResourceName} が見つかりません。");
        return new Icon(stream, size);
    }
}
