using System.Net;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// HTTP delegating handler that retries requests on transient failures (5xx, request timeout)
/// with exponential backoff and jitter. Designed for use with <see cref="TrelloClient"/>.
/// </summary>
internal sealed class TransientRetryHandler(
    ILogger<TransientRetryHandler> logger,
    int maxRetries = 2) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken);

                if (!IsTransientStatusCode(response.StatusCode) || attempt >= maxRetries)
                    return response;

                logger.LogWarning(
                    "Transient HTTP {StatusCode} from {Method} {Uri} (attempt {Attempt}/{MaxRetries}), retrying",
                    (int)response.StatusCode, request.Method, request.RequestUri?.AbsolutePath, attempt + 1, maxRetries);

                response.Dispose();
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                logger.LogWarning(ex,
                    "Transient HTTP error on {Method} {Uri} (attempt {Attempt}/{MaxRetries}), retrying",
                    request.Method, request.RequestUri?.AbsolutePath, attempt + 1, maxRetries);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxRetries)
            {
                // HttpClient throws TaskCanceledException on timeout (not user cancellation)
                logger.LogWarning(ex,
                    "HTTP timeout on {Method} {Uri} (attempt {Attempt}/{MaxRetries}), retrying",
                    request.Method, request.RequestUri?.AbsolutePath, attempt + 1, maxRetries);
            }

            var baseDelay = Math.Min(Math.Pow(2, attempt), 8);
            var jitter = Random.Shared.NextDouble() * 0.5;
            await Task.Delay(TimeSpan.FromSeconds(baseDelay + jitter), cancellationToken);
        }
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout;
}
