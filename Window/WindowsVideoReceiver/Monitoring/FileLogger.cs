using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WindowsVideoReceiver.Monitoring;

public static class FileLogger
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> LastStatisticsWrite = new(StringComparer.Ordinal);
    private static string? _logDirectory;

    public static event Action<string>? WriteFailed;

    public static void Initialize()
    {
        lock (Sync)
        {
            _logDirectory = GetLogDirectory();
            Directory.CreateDirectory(_logDirectory);
            EnsureLogFileExists("video-diagnostics.log");
            EnsureLogFileExists("received-statistics.log");
        }

        Debug.WriteLine($"Application logs are written to: {_logDirectory}");
    }

    public static void WriteDiagnostic(string stage, string message) =>
        Append("video-diagnostics.log", $"[{stage}] {OneLine(message)}");

    public static void WriteConfiguration(string source, string messageType, JsonElement message) =>
        Append(
            "received-statistics.log",
            $"[{source}] {messageType}: {message.GetRawText()}");

    public static void WriteStatistics(string source, JsonElement message)
    {
        lock (Sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (LastStatisticsWrite.TryGetValue(source, out var previous) &&
                now - previous < TimeSpan.FromSeconds(5))
            {
                return;
            }

            LastStatisticsWrite[source] = now;
            AppendCore(
                "received-statistics.log",
                $"[{source}] STATS: {message.GetRawText()}");
        }
    }

    private static void Append(string fileName, string message)
    {
        lock (Sync)
        {
            AppendCore(fileName, message);
        }
    }

    private static void AppendCore(string fileName, string message)
    {
        try
        {
            var directory = _logDirectory ?? GetLogDirectory();
            Directory.CreateDirectory(directory);
            var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            var entry = $"{timestamp} {message}";
            File.AppendAllText(
                Path.Combine(directory, fileName),
                $"{entry}{Environment.NewLine}");
            Debug.WriteLine(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var error = $"Could not write application log: {exception.Message}";
            Debug.WriteLine(error);
            WriteFailed?.Invoke(error);
        }
    }

    private static void EnsureLogFileExists(string fileName) =>
        File.AppendAllText(Path.Combine(_logDirectory!, fileName), string.Empty);

    private static string GetLogDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WindowsVideoReceiver.csproj")))
            {
                return Path.Combine(directory.FullName, "log");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "log");
    }

    private static string OneLine(string value) =>
        value.ReplaceLineEndings(" ");
}
