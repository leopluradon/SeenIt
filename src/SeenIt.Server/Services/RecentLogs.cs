using System.Collections.Concurrent;
using SeenIt.Contracts;

namespace SeenIt.Server.Services;

/// <summary>Keeps the last few hundred warnings and errors in memory for the admin page.</summary>
public sealed class RecentLogs : ILoggerProvider
{
    private const int Max = 300;
    private readonly ConcurrentQueue<AdminLogEntry> _entries = new();

    public IReadOnlyList<AdminLogEntry> Snapshot() => _entries.Reverse().ToList();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private void Add(AdminLogEntry e)
    {
        _entries.Enqueue(e);
        while (_entries.Count > Max && _entries.TryDequeue(out _)) { }
    }

    private sealed class Logger(RecentLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var message = formatter(state, ex);
            if (ex is not null) message += $"\n{ex.GetType().Name}: {ex.Message}";
            owner.Add(new AdminLogEntry(DateTimeOffset.UtcNow, level.ToString(), category, message.Length > 4000 ? message[..4000] : message));
        }
    }
}
