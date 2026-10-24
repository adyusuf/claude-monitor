using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>A logger that keeps what it was asked to write, with the level, so a test can say "a warning was logged and it names the kind of error".</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Text)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Text)> Entries
    {
        get
        {
            lock (entries) return [.. entries];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (entries) entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>One thing that was logged: where from, how bad, the text, and every part of the state and the exception (so a test can look for a secret anywhere in it).</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Text, string Everything);

/// <summary>A logger factory and provider in one that keeps every entry of every category.</summary>
public sealed class TracingLog : ILoggerFactory, ILoggerProvider
{
    private readonly List<LogEntry> entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (entries) return [.. entries];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
        // entries come only from loggers made here
    }

    public void Dispose()
    {
        // nothing to release
    }

    private sealed class Sink(TracingLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var parts = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}")) : state?.ToString();
            var entry = new LogEntry(category, logLevel, formatter(state, exception), parts + " " + exception);
            lock (owner.entries) owner.entries.Add(entry);
        }
    }
}

/// <summary>A database failure that is a raw <see cref="DbException"/> (what the driver throws when the server is down), not one of EF's own.</summary>
public sealed class DatabaseDownException : DbException
{
    public DatabaseDownException()
        : base("the database is down")
    {
    }
}

/// <summary>Scopes that cannot be made: the service that asks for one meets this error first thing.</summary>
public sealed class FailingScopes(Exception error) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => throw error;
}

/// <summary>Scopes over a real provider that count how many are open at once (the most is <see cref="MostOpen"/>) and can be made to fail.</summary>
public sealed class CountingScopes(IServiceScopeFactory inner) : IServiceScopeFactory
{
    private int open;
    private int most;
    private int made;

    public int MostOpen => Volatile.Read(ref most);

    public int Made => Volatile.Read(ref made);

    /// <summary>While set, a scope cannot be made.</summary>
    public Exception? Fail { get; set; }

    public IServiceScope CreateScope()
    {
        if (Fail is { } error) throw error;
        Interlocked.Increment(ref made);
        var now = Interlocked.Increment(ref open);
        int seen;
        while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen)
        {
            // another thread raised it meanwhile: look again
        }

        return new Scope(inner.CreateScope(), () => Interlocked.Decrement(ref open));
    }

    private sealed class Scope(IServiceScope real, Action closed) : IServiceScope
    {
        public IServiceProvider ServiceProvider => real.ServiceProvider;

        public void Dispose()
        {
            real.Dispose();
            closed();
        }
    }
}
