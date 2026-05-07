using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace TaskBoard.Worker.Tests.Helpers;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{TOptions}"/> implementation for unit
/// tests. Wraps a single value; <see cref="CurrentValue"/> always returns it.
/// Change-notification callbacks are accepted but never fired.
/// </summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    where T : class
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Convenience factory — mirrors <see cref="Options.Create{T}"/> ergonomics.
/// <code>
/// var monitor = TestOptionsMonitor.Create(new MyOptions { X = 1 });
/// </code>
/// </summary>
public static class TestOptionsMonitor
{
    public static TestOptionsMonitor<T> Create<T>(T value) where T : class
        => new(value);
}
