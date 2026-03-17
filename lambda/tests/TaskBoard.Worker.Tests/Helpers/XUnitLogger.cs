using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace TaskBoard.Worker.Tests.Helpers;

public sealed class XUnitLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        if (exception is not null)
            output.WriteLine(exception.ToString());
    }
}
