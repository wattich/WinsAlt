using System.Collections.Concurrent;

namespace WinsAlt.Infrastructure;

/// <summary>
/// A bounded in-memory ring buffer of recent log entries so the dashboard can show
/// logs without a file. Fed by <see cref="InMemoryLoggerProvider"/>. Thread-safe.
/// </summary>
public sealed class InMemoryLogStore
{
    public sealed record Entry(long T, string Level, string Category, string Message);

    private const int Capacity = 500;
    private readonly ConcurrentQueue<Entry> _entries = new();
    private int _count;

    public void Add(Entry e)
    {
        _entries.Enqueue(e);
        if (Interlocked.Increment(ref _count) > Capacity)
        {
            if (_entries.TryDequeue(out _)) Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>Returns entries oldest-first.</summary>
    public IReadOnlyList<Entry> Snapshot() => _entries.ToArray();
}

/// <summary>ILoggerProvider that mirrors log records into <see cref="InMemoryLogStore"/>.</summary>
public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly InMemoryLogStore _store;
    public InMemoryLoggerProvider(InMemoryLogStore store) => _store = store;

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, _store);
    public void Dispose() { }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly string _category;
        private readonly InMemoryLogStore _store;

        public InMemoryLogger(string category, InMemoryLogStore store)
        {
            // Keep only the last namespace segment for readability.
            int dot = category.LastIndexOf('.');
            _category = dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
            _store = store;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Skip Trace/Debug to keep the buffer focused on operationally useful entries.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += " - " + exception.Message;

            _store.Add(new InMemoryLogStore.Entry(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                logLevel.ToString(),
                _category,
                msg));
        }
    }
}
