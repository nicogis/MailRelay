using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MailRelay;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly int _retainedDays;
    private readonly bool _enabled;
    private DateOnly _lastCleanupDate;

    private DailyFileLoggerProvider(
        string directory,
        int retainedDays,
        bool enabled)
    {
        _directory = directory;
        _retainedDays = Math.Max(1, retainedDays);
        _enabled = enabled;

        if (_enabled)
        {
            Directory.CreateDirectory(_directory);
        }
    }

    public static DailyFileLoggerProvider Create(IConfiguration configuration)
    {
        var section = configuration.GetSection("FileLogging");

        var enabled = section.GetValue("Enabled", true);
        var retainedDays = section.GetValue("RetainedDays", 30);
        var configuredDirectory = section["Directory"];

        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MailRelay",
                "Logs")
            : configuredDirectory;

        return new DailyFileLoggerProvider(
            directory,
            retainedDays,
            enabled);
    }

    public ILogger CreateLogger(string categoryName)
        => new DailyFileLogger(this, categoryName);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    internal void Write(
        LogLevel logLevel,
        string categoryName,
        string message,
        Exception? exception)
    {
        if (!_enabled)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var filePath = Path.Combine(
            _directory,
            $"mailrelay-{now:yyyyMMdd}.log");

        var line =
            $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {categoryName}: {message}";

        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(
                filePath,
                line + Environment.NewLine);

            CleanupOldLogs(now);
        }
    }

    private void CleanupOldLogs(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);

        if (_lastCleanupDate == today)
        {
            return;
        }

        _lastCleanupDate = today;
        var cutoff = now.Date.AddDays(-_retainedDays);

        foreach (var file in Directory.EnumerateFiles(
                     _directory,
                     "mailrelay-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Logging cleanup must never stop the mail relay.
            }
        }
    }

    private sealed class DailyFileLogger(
        DailyFileLoggerProvider provider,
        string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(
                logLevel,
                categoryName,
                formatter(state, exception),
                exception);
        }
    }
}
