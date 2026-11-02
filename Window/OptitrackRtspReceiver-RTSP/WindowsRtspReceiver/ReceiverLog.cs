using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WindowsRtspReceiver;

internal static class ReceiverLog
{
    private static readonly object Sync = new();
    private static readonly Lazy<string> LogFilePath = new(FindLogFilePath);
    private static int _writeFailureReported;

    public static string FilePath => LogFilePath.Value;

    public static void Write(string category, string details)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{category}] {details}{Environment.NewLine}";
            lock (Sync)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (Interlocked.Exchange(ref _writeFailureReported, 1) == 0)
            {
                Debug.WriteLine($"Could not write receiver log '{FilePath}': {exception}");
            }
        }
    }

    private static string FindLogFilePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")) &&
                Directory.Exists(Path.Combine(directory.FullName, "WindowsRtspReceiver")))
            {
                return Path.Combine(directory.FullName, "log", "receiver_ui.log");
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "log", "receiver_ui.log");
    }
}
