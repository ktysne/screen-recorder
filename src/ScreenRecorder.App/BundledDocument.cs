using System.Diagnostics;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class BundledDocument
{
    public const string ManualFileName = "manual.html";
    public const string LicenseFileName = "license.html";

    // 同梱の HTML が無いとき(開発中のビルドなど)は、配布サイトの同名のページを開く。
    public static void Open(string fileName)
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, fileName);
        var target = File.Exists(localPath) ? localPath : UpdateManifestParser.DistributionPageUrl + fileName;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
