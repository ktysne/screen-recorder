using System.Text;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal static class OrphanDiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void Activate(DiagnosticLogLevel minimumLevel) =>
        DiagnosticLog.SetForwarder(minimumLevel, Append);

    private static void Append(DiagnosticLogLevel level, string tag, string message)
    {
        try
        {
            var directory = DiagnosticLog.LogsDirectory;
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "record-worker-orphan.log");
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                TruncateIfNeeded(stream);
                stream.Seek(0, SeekOrigin.End);
                var taggedMessage = tag == DiagnosticLogTags.RecordWorker ? message : $"[{tag}] {message}";
                var line = $"[pid:{Environment.ProcessId}] {DiagnosticLogFormatting.FormatLine(DateTime.Now, level, DiagnosticLogTags.RecordWorker, taggedMessage)}\r\n";
                var bytes = Utf8WithoutBom.GetBytes(line);
                stream.Write(bytes);
                stream.Flush();
            }
        }
        catch
        {
        }
    }

    private static void TruncateIfNeeded(FileStream stream)
    {
        var startOffset = DiagnosticLogOrphanPolicy.GetTruncationStartOffset(stream.Length);
        if (startOffset is null) return;

        stream.Position = startOffset.Value;
        var value = stream.ReadByte();
        while (value != -1 && value != '\n') value = stream.ReadByte();
        if (value == -1)
        {
            stream.SetLength(0);
            return;
        }

        var sourceOffset = stream.Position;
        var remainingBytes = stream.Length - sourceOffset;
        var buffer = new byte[8192];
        var destinationOffset = 0L;
        while (remainingBytes > 0)
        {
            var count = (int)Math.Min(buffer.Length, remainingBytes);
            stream.Position = sourceOffset;
            var read = stream.Read(buffer, 0, count);
            if (read == 0) break;
            stream.Position = destinationOffset;
            stream.Write(buffer, 0, read);
            sourceOffset += read;
            destinationOffset += read;
            remainingBytes -= read;
        }

        stream.SetLength(destinationOffset);
    }
}
