using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed partial class TrelloCrossReferenceResolver(
    HttpClient httpClient,
    IOptions<TrelloClientOptions> options,
    ILogger<TrelloCrossReferenceResolver> logger) : ICrossReferenceResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"https://trello\.com/c/([a-zA-Z0-9]+)(?:/[^\s)]*)?")]
    private static partial Regex TrelloCardUrlPattern();

    private sealed record TrelloCardInfo(string Id, string Name);

    public async Task<IReadOnlyList<CardReference>> ParseTextReferencesAsync(
        string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var matches = TrelloCardUrlPattern().Matches(text);
        var seen = new HashSet<string>();
        var refs = new List<CardReference>();

        foreach (Match match in matches)
        {
            var shortLink = match.Groups[1].Value;
            if (!seen.Add(shortLink))
                continue;

            try
            {
                var card = await ResolveShortLinkAsync(shortLink, cancellationToken);
                if (card is not null)
                {
                    refs.Add(new CardReference(card.Id, "mention", match.Value, card.Name));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve Trello shortLink {ShortLink}", shortLink);
            }
        }

        return refs;
    }

    public async Task<IReadOnlyList<CardReference>> GetStructuredReferencesAsync(
        string cardId, CancellationToken cancellationToken)
    {
        try
        {
            var url = ($"/1/cards/{cardId}/attachments");
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to fetch attachments for card {CardId}: {Status}",
                    cardId, response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var refs = new List<CardReference>();
            var seen = new HashSet<string>();

            foreach (var attachment in doc.RootElement.EnumerateArray())
            {
                if (!attachment.TryGetProperty("url", out var urlProp))
                    continue;

                var attachmentUrl = urlProp.GetString();
                if (attachmentUrl is null)
                    continue;

                var match = TrelloCardUrlPattern().Match(attachmentUrl);
                if (!match.Success)
                    continue;

                var shortLink = match.Groups[1].Value;
                if (!seen.Add(shortLink))
                    continue;

                try
                {
                    var card = await ResolveShortLinkAsync(shortLink, cancellationToken);
                    if (card is not null)
                    {
                        refs.Add(new CardReference(card.Id, "attachment", null, card.Name));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to resolve attachment shortLink {ShortLink}", shortLink);
                }
            }

            return refs;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch structured references for card {CardId}", cardId);
            return [];
        }
    }

    private async Task<TrelloCardInfo?> ResolveShortLinkAsync(
        string shortLink, CancellationToken cancellationToken)
    {
        var url = ($"/1/cards/{shortLink}?fields=id,name");
        using var response = await httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogDebug("Trello card shortLink {ShortLink} not found (deleted?)", shortLink);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Failed to resolve shortLink {ShortLink}: {Status}", shortLink, response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TrelloCardInfo>(json, JsonOptions);
    }

}
