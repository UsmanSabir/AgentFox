using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AgentFox.Helpers;


internal class LoggingConfig
{
    public bool UseFileLogger { get; set; } = true;
    public string FilePath { get; set; } = "logs/agentfox.log";
    public LogLevel MinLevel { get; set; } = LogLevel.Warning;
    /// <summary>Log files older than this many days are deleted on startup. 0 = disabled.</summary>
    public int RetentionDays { get; set; } = 3;
}

// ─────────────────────────────────────────────────────────────────────────────
// Loggers
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Spectre.Console-backed logger for structured, colorized console output.
/// </summary>
internal class ConsoleLogger : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var (prefix, style) = logLevel switch
        {
            LogLevel.Error => ("[[ERR]]", "bold red"),
            LogLevel.Warning => ("[[WARN]]", "bold yellow"),
            LogLevel.Debug => ("[[DBG]]", "grey"),
            _ => ("[[INF]]", "dim blue"),
        };
        AnsiConsole.MarkupLine($"[{style}]{prefix}[/] {Markup.Escape(message)}");
        if (exception != null)
            AnsiConsole.MarkupLine($"  [red]↳ {Markup.Escape(exception.Message)}[/]");
    }
}

internal class ConsoleLogger<T> : ConsoleLogger, ILogger<T> where T : class { }

internal sealed class ConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger();
    public void Dispose() { }
}

/// <summary>
/// Thread-safe file logger. Call <see cref="Configure"/> once at startup before DI registration.
/// </summary>
internal class FileLogger : ILogger
{
    private static readonly object _fileLock = new();
    private static string _filePath = "logs/agentfox.log";
    private static LogLevel _minLevel = LogLevel.Warning;

    public static void Configure(string filePath, LogLevel minLevel)
    {
        _filePath = filePath;
        _minLevel = minLevel;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public static void DeleteOldLogs(string filePath, int retentionDays)
    {
        if (retentionDays <= 0) return;
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { /* best-effort */ }
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        var prefix = logLevel switch
        {
            LogLevel.Error => "[ERR] ",
            LogLevel.Warning => "[WARN]",
            LogLevel.Information => "[INF] ",
            LogLevel.Debug => "[DBG] ",
            _ => "[TRC] ",
        };
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {prefix} {message}";
        if (exception != null)
            line += $"\n       ↳ {exception}";
        lock (_fileLock)
            File.AppendAllText(_filePath, line + "\n");
    }
}

internal class FileLogger<T> : FileLogger, ILogger<T> where T : class { }

internal sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger();
    public void Dispose() { }
}

