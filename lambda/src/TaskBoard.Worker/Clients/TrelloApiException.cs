using System.Net;

namespace TaskBoard.Worker.Clients;

public sealed class TrelloApiException(
    string operation,
    string resourceId,
    HttpStatusCode statusCode,
    string responseBody)
    : Exception($"Trello API error during {operation} for {resourceId}: {statusCode} - {responseBody}")
{
    public string Operation { get; } = operation;
    public string ResourceId { get; } = resourceId;
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
