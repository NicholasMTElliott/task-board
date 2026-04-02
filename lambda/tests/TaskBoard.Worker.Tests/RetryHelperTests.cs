using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class RetryHelperTests
{
    [Fact]
    public async Task ExecuteWithRetry_SucceedsOnFirstAttempt_ReturnsResult()
    {
        var callCount = 0;

        var result = await RetryHelper.ExecuteWithRetryAsync(
            () => { callCount++; return Task.FromResult(42); },
            _ => true,
            maxRetries: 2,
            null,
            "test",
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetry_TransientFailure_RetriesAndSucceeds()
    {
        var callCount = 0;

        var result = await RetryHelper.ExecuteWithRetryAsync(
            () =>
            {
                callCount++;
                if (callCount < 3)
                    throw new InvalidOperationException("transient");
                return Task.FromResult("ok");
            },
            ex => ex is InvalidOperationException,
            maxRetries: 2,
            null,
            "test",
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, callCount); // 1 initial + 2 retries
    }

    [Fact]
    public async Task ExecuteWithRetry_ExhaustsRetries_ThrowsFinalException()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    callCount++;
                    throw new InvalidOperationException($"fail #{callCount}");
                    return Task.FromResult(0); // unreachable but needed for type inference
                },
                ex => ex is InvalidOperationException,
                maxRetries: 2,
                null,
                "test",
                CancellationToken.None));

        Assert.Equal(3, callCount); // 1 initial + 2 retries
    }

    [Fact]
    public async Task ExecuteWithRetry_NonTransientError_DoesNotRetry()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    callCount++;
                    throw new ArgumentException("not transient");
                    return Task.FromResult(0);
                },
                ex => ex is InvalidOperationException, // only retries IOE
                maxRetries: 2,
                null,
                "test",
                CancellationToken.None));

        Assert.Equal(1, callCount); // no retry
    }

    [Fact]
    public async Task ExecuteWithRetry_ZeroMaxRetries_NoRetry()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    callCount++;
                    throw new InvalidOperationException("fail");
                    return Task.FromResult(0);
                },
                _ => true,
                maxRetries: 0,
                null,
                "test",
                CancellationToken.None));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetry_VoidOverload_Works()
    {
        var callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(
            () => { callCount++; return Task.CompletedTask; },
            _ => true,
            maxRetries: 2,
            null,
            "test",
            CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetry_CancellationToken_Respected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    throw new InvalidOperationException("transient");
                    return Task.FromResult(0);
                },
                _ => true,
                maxRetries: 5,
                null,
                "test",
                cts.Token));
    }
}
