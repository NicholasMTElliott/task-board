using System.Net;
using System.Text;

namespace TaskBoard.Worker.Tests.Helpers;

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responseFactories = new();
    private readonly List<HttpRequestMessage> _sentRequests = new();
    private readonly List<string?> _requestBodies = new();

    public IReadOnlyList<HttpRequestMessage> SentRequests => _sentRequests;
    public IReadOnlyList<string?> RequestBodies => _requestBodies;

    public void EnqueueResponse(HttpStatusCode statusCode, string content, string contentType = "application/json")
    {
        _responseFactories.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, contentType)
        });
    }

    public void EnqueueBinaryResponse(HttpStatusCode statusCode, byte[] content, string contentType = "image/png")
    {
        _responseFactories.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            var byteContent = new ByteArrayContent(content);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            response.Content = byteContent;
            return response;
        });
    }

    public void EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responseFactories.Enqueue(factory);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _sentRequests.Add(request);

        // Read content eagerly before it may be disposed
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;
        _requestBodies.Add(body);

        if (_responseFactories.Count == 0)
            throw new InvalidOperationException($"No response configured for request: {request.Method} {request.RequestUri}");

        var factory = _responseFactories.Dequeue();
        return factory(request);
    }
}
