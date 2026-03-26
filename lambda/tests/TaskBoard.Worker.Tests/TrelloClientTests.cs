using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests;

public class TrelloClientTests
{
    private const string TestMarker = "<!-- agent-run:test-run-1 -->";

    private static readonly TrelloClientOptions DefaultOptions = new()
    {
        ApiKey = "test-key",
        ApiToken = "test-token"
    };

    private static (ITaskBoardClient Client, MockHttpMessageHandler Handler) CreateSut(TrelloClientOptions? options = null)
    {
        var opts = options ?? DefaultOptions;
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.trello.com") };
        var client = new TrelloClient(httpClient, Options.Create(opts), NullLogger<TrelloClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task GetCardAsync_ReturnsDeserializedCard()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK,
            """{"id":"c1","name":"My Card","desc":"Card description","idList":"list1"}""");

        var card = await sut.GetCardAsync("c1", CancellationToken.None);

        Assert.Equal("c1", card.Id);
        Assert.Equal("My Card", card.Title);
        Assert.Equal("Card description", card.Body);
        Assert.Equal("list1", card.ColumnId);

        var request = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/1/cards/c1", request.RequestUri!.ToString());
        Assert.Contains("fields=id,name,desc,idList", request.RequestUri.ToString());
        Assert.Contains("key=test-key", request.RequestUri.ToString());
        Assert.Contains("token=test-token", request.RequestUri.ToString());
    }

    [Fact]
    public async Task GetBoardCardsAsync_ReturnsDeserializedCards()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK,
            """[{"id":"c1","name":"Card One","desc":"Desc 1","idList":"list1"},{"id":"c2","name":"Card Two","desc":"Desc 2","idList":"list2"}]""");

        var cards = await sut.GetBoardCardsAsync("board-123", CancellationToken.None);

        Assert.Equal(2, cards.Count);
        Assert.Equal("c1", cards[0].Id);
        Assert.Equal("Card One", cards[0].Title);
        Assert.Equal("c2", cards[1].Id);
        Assert.Equal("Desc 1", cards[0].Body);
        Assert.Equal("list1", cards[0].ColumnId);

        var request = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/1/boards/board-123/cards", request.RequestUri!.ToString());
        Assert.Contains("fields=id,name,desc,idList", request.RequestUri.ToString());
        Assert.Contains("key=test-key", request.RequestUri.ToString());
    }

    [Fact]
    public async Task GetCardAsync_NonSuccess_ThrowsTrelloApiException()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.NotFound, "card not found");

        var ex = await Assert.ThrowsAsync<TrelloApiException>(
            () => sut.GetCardAsync("c1", CancellationToken.None));

        Assert.Equal("GetCard", ex.Operation);
        Assert.Equal("c1", ex.ResourceId);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("card not found", ex.ResponseBody);
    }

    [Fact]
    public async Task UpdateCardDescriptionAsync_SendsCorrectRequest()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, "{}");

        await sut.UpdateCardBodyAsync("c1", "new description", CancellationToken.None);

        var request = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Contains("/1/cards/c1", request.RequestUri!.ToString());
        Assert.DoesNotContain("/idList", request.RequestUri.ToString());
        Assert.Contains("key=test-key", request.RequestUri.ToString());
        Assert.Contains("token=test-token", request.RequestUri.ToString());

        var body = handler.RequestBodies[0];
        Assert.Contains("desc=new+description", body);
    }

    [Fact]
    public async Task MoveCardToListAsync_SendsCorrectRequest()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, "{}");

        await sut.MoveCardToColumnAsync("c1", "list-2", CancellationToken.None);

        var request = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Contains("/1/cards/c1/idList", request.RequestUri!.ToString());
        Assert.Contains("key=test-key", request.RequestUri.ToString());

        var body = handler.RequestBodies[0];
        Assert.Contains("value=list-2", body);
    }

    [Fact]
    public async Task UpsertComment_NoExisting_CreatesNew()
    {
        var (sut, handler) = CreateSut();

        // GET comments — empty array
        handler.EnqueueResponse(HttpStatusCode.OK, "[]");
        // POST new comment
        handler.EnqueueResponse(HttpStatusCode.OK, "{}");

        await sut.UpsertAgentCommentAsync("c1", "Agent summary here", TestMarker, CancellationToken.None);

        Assert.Equal(2, handler.SentRequests.Count);

        // First: GET search
        var getRequest = handler.SentRequests[0];
        Assert.Equal(HttpMethod.Get, getRequest.Method);
        Assert.Contains("/1/cards/c1/actions", getRequest.RequestUri!.ToString());
        Assert.Contains("filter=commentCard", getRequest.RequestUri.ToString());

        // Second: POST create
        var postRequest = handler.SentRequests[1];
        Assert.Equal(HttpMethod.Post, postRequest.Method);
        Assert.Contains("/1/cards/c1/actions/comments", postRequest.RequestUri!.ToString());

        var body = handler.RequestBodies[1];
        Assert.Contains("agent-run%3Atest-run-1", body);
        Assert.Contains("Agent+summary+here", body);
    }

    [Fact]
    public async Task UpsertComment_ExistingFound_UpdatesIt()
    {
        var (sut, handler) = CreateSut();

        // GET comments — one with marker
        handler.EnqueueResponse(HttpStatusCode.OK, """
            [
                {"id":"action-123","data":{"text":"<!-- agent-run:test-run-1 -->\nOld summary"}},
                {"id":"action-456","data":{"text":"A normal comment"}}
            ]
            """);
        // PUT update
        handler.EnqueueResponse(HttpStatusCode.OK, "{}");

        await sut.UpsertAgentCommentAsync("c1", "Updated summary", TestMarker, CancellationToken.None);

        Assert.Equal(2, handler.SentRequests.Count);

        // Second: PUT update to the found comment action
        var putRequest = handler.SentRequests[1];
        Assert.Equal(HttpMethod.Put, putRequest.Method);
        Assert.Contains("/1/actions/action-123/text", putRequest.RequestUri!.ToString());

        var body = handler.RequestBodies[1];
        Assert.Contains("agent-run%3Atest-run-1", body);
        Assert.Contains("Updated+summary", body);
    }

    [Fact]
    public async Task UpsertComment_SearchFails_Throws()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "server error");

        var ex = await Assert.ThrowsAsync<TrelloApiException>(
            () => sut.UpsertAgentCommentAsync("c1", "comment", TestMarker, CancellationToken.None));

        Assert.Equal("SearchComments", ex.Operation);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task AuthParams_AppendedToAllRequests()
    {
        var (sut, handler) = CreateSut();

        // Enqueue responses for GetCard, UpdateDescription, MoveCard
        handler.EnqueueResponse(HttpStatusCode.OK,
            """{"id":"c1","name":"Card","desc":"Desc","idList":"l1"}""");
        handler.EnqueueResponse(HttpStatusCode.OK, "{}");
        handler.EnqueueResponse(HttpStatusCode.OK, "{}");

        await sut.GetCardAsync("c1", CancellationToken.None);
        await sut.UpdateCardBodyAsync("c1", "desc", CancellationToken.None);
        await sut.MoveCardToColumnAsync("c1", "l2", CancellationToken.None);

        Assert.Equal(3, handler.SentRequests.Count);
        foreach (var request in handler.SentRequests)
        {
            var url = request.RequestUri!.ToString();
            Assert.Contains("key=test-key", url);
            Assert.Contains("token=test-token", url);
        }
    }
}
