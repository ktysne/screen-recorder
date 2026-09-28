using System.Text;

namespace ScreenRecorder.Cli;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "mcp")
        {
            Console.SetOut(Console.Error);
            Console.OutputEncoding = new UTF8Encoding(false);
            return McpServerHost.Run(args.Skip(1).ToArray(), Console.Error);
        }

        // 既定のコンソールのコードページのままだと、パイプで受け取った側で日本語が化けるため UTF-8 に固定する。
        Console.OutputEncoding = new UTF8Encoding(false);
        return CliApplication.Run(args, Console.Out, Console.Error, CliEnvironment.Create());
    }
}
