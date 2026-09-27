using System.Text;

namespace ScreenRecorder.Cli;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 既定のコンソールのコードページのままだと、パイプで受け取った側で日本語が化けるため UTF-8 に固定する。
        Console.OutputEncoding = new UTF8Encoding(false);
        return CliApplication.Run(args, Console.Out, Console.Error, CliEnvironment.Create());
    }
}
