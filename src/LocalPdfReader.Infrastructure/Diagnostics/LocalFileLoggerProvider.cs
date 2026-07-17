using Microsoft.Extensions.Logging;

namespace LocalPdfReader.Infrastructure.Diagnostics;

public sealed class LocalFileLoggerProvider(string logDirectoryPath) : ILoggerProvider
{
    private readonly object _writeLock = new();

    public ILogger CreateLogger(string categoryName) => new LocalFileLogger(categoryName, logDirectoryPath, _writeLock);

    public void Dispose()
    {
    }

    private sealed class LocalFileLogger(string categoryName, string logDirectoryPath, object writeLock) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            var message = formatter(state, exception);
            var exceptionDetails = exception is null
                ? string.Empty
                : $"{Environment.NewLine}{exception}";
            var beijingTime = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
            var entry = $"{beijingTime:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {categoryName} {eventId.Name ?? eventId.Id.ToString()} {message}{exceptionDetails}{Environment.NewLine}";
            var logFilePath = Path.Combine(logDirectoryPath, $"local-pdf-reader-{DateTime.UtcNow:yyyyMMdd}.log");

            lock (writeLock)
            {
                Directory.CreateDirectory(logDirectoryPath);
                File.AppendAllText(logFilePath, entry);
            }
        }
    }
}
