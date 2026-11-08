using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Logging;

/// <summary>
/// Minimal daily-rolling file logger (logs/app-yyyyMMdd.log, 14 days kept). Callers never log
/// credentials or authorization headers; messages are written as given.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        PruneOldLogs();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level.ToString().ToUpperInvariant()[..4]).Append("] ")
            .Append(category[(category.LastIndexOf('.') + 1)..]).Append(": ")
            .Append(message);
        if (exception is not null) line.AppendLine().Append(exception);
        line.AppendLine();

        var file = Path.Combine(_directory, $"app-{now:yyyyMMdd}.log");
        lock (_lock)
        {
            try
            {
                File.AppendAllText(file, line.ToString());
            }
            catch (IOException)
            {
                // Logging must never crash the app.
            }
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                    File.Delete(file);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
