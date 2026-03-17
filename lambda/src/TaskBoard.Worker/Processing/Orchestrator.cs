using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class Orchestrator(
    WorkflowConfig workflowConfig,
    ITrelloClient trelloClient,
    ILlmClient llmClient,
    ICardStateRepository cardStateRepository,
    IRunLogRepository runLogRepository,
    IOptions<QueueProcessingOptions> queueOptions,
    ILogger<Orchestrator> logger)
{
    private static readonly HashSet<string> ValidOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMPLETE", "NEEDS_INFO", "BLOCKED"
    };

    private readonly WorkflowConfig _workflowConfig = workflowConfig;
    private readonly ITrelloClient _trelloClient = trelloClient;
    private readonly ILlmClient _llmClient = llmClient;
    private readonly ICardStateRepository _cardStateRepository = cardStateRepository;
    private readonly IRunLogRepository _runLogRepository = runLogRepository;
    private readonly QueueProcessingOptions _queueOptions = queueOptions.Value;
    private readonly ILogger<Orchestrator> _logger = logger;

    public async Task ExecuteAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        var listId = ExtractListAfterId(message.PayloadJson);
        if (listId is null)
        {
            _logger.LogInformation("No listAfter.id in payload for action {ActionId}, skipping", message.ActionId);
            return;
        }

        if (!_workflowConfig.States.TryGetValue(listId, out var state))
        {
            _logger.LogInformation("List {ListId} not in workflow config, skipping", listId);
            return;
        }

        if (!string.Equals(state.GateType, "agent_run", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("State {StateName} is {GateType}, not agent_run — skipping", state.Name, state.GateType);
            return;
        }

        if (state.Role is null || !_workflowConfig.Roles.TryGetValue(state.Role, out var role))
        {
            _logger.LogWarning("State {StateName} has no valid role, skipping", state.Name);
            return;
        }

        var cardId = message.CardId ?? ExtractCardId(message.PayloadJson);
        if (cardId is null)
        {
            _logger.LogWarning("No card ID available for action {ActionId}, skipping", message.ActionId);
            return;
        }

        var existingState = await _cardStateRepository.GetStateAsync(cardId, cancellationToken);
        if (existingState is { WaitingOnHuman: true } && existingState.OriginListId != listId)
        {
            _logger.LogWarning("Card {CardId} returning from Questions to {ListId} but origin was {OriginListId}, skipping",
                cardId, listId, existingState.OriginListId);
            return;
        }

        var lockTtl = TimeSpan.FromSeconds(_queueOptions.LockTtlSeconds);
        var lockId = Guid.NewGuid().ToString("N");
        var lockAcquired = await _cardStateRepository.TryAcquireLockAsync(cardId, lockId, lockTtl, cancellationToken);
        if (!lockAcquired)
        {
            _logger.LogInformation("Card {CardId} is already locked, skipping", cardId);
            return;
        }

        try
        {
            var card = await _trelloClient.GetCardAsync(cardId, cancellationToken);

            var systemPrompt = role.SystemPrompt;
            var userPrompt = $"{state.TaskPrompt}\n\n---\n\n# Card: {card.Name}\n\n{card.Desc}";

            _logger.LogInformation("Calling LLM for card {CardId} state {StateName} role {Role}",
                cardId, state.Name, state.Role);

            var agentResponse = await _llmClient.GetCompletionAsync(
                role.Model, systemPrompt, userPrompt, cancellationToken);

            var outcome = ValidOutcomes.Contains(agentResponse.Outcome)
                ? agentResponse.Outcome.ToUpperInvariant()
                : "ERROR";

            if (!ValidOutcomes.Contains(agentResponse.Outcome))
            {
                _logger.LogWarning("Invalid LLM outcome '{Outcome}' for card {CardId}, treating as ERROR",
                    agentResponse.Outcome, cardId);
            }

            var mergedDescription = CardDescriptionParser.MergeSections(
                card.Desc, agentResponse.Updates, role.Sections);

            await _trelloClient.UpdateCardDescriptionAsync(cardId, mergedDescription, cancellationToken);
            await _trelloClient.UpsertAgentCommentAsync(cardId, agentResponse.SummaryComment, cancellationToken);

            string? targetListId;
            if (state.Transitions.TryGetValue(outcome, out targetListId))
            {
                await _trelloClient.MoveCardToListAsync(cardId, targetListId, cancellationToken);
            }
            else if (state.Transitions.TryGetValue("ERROR", out targetListId))
            {
                _logger.LogWarning("No transition for outcome {Outcome} in state {StateName}, falling back to ERROR",
                    outcome, state.Name);
                await _trelloClient.MoveCardToListAsync(cardId, targetListId, cancellationToken);
            }
            else
            {
                _logger.LogError("No transition for outcome {Outcome} or ERROR in state {StateName}, card stuck",
                    outcome, state.Name);
            }

            string? originListId = string.Equals(outcome, "NEEDS_INFO", StringComparison.OrdinalIgnoreCase)
                ? listId
                : null;
            var waitingOnHuman = string.Equals(outcome, "NEEDS_INFO", StringComparison.OrdinalIgnoreCase);

            await _cardStateRepository.UpdateStateAsync(
                cardId, message.ActionId, targetListId ?? listId,
                originListId, waitingOnHuman, cancellationToken);

            var inputHash = ComputeHash(card.Desc);
            var outputHash = ComputeHash(JsonSerializer.Serialize(agentResponse));

            await _runLogRepository.WriteAsync(
                cardId, state.Role, inputHash, outputHash, outcome, cancellationToken);

            _logger.LogInformation("Orchestration complete for card {CardId}: outcome={Outcome}", cardId, outcome);
        }
        finally
        {
            await _cardStateRepository.ReleaseLockAsync(cardId, lockId, cancellationToken);
        }
    }

    internal static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string? ExtractListAfterId(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("action", out var action)
                && action.TryGetProperty("data", out var data)
                && data.TryGetProperty("listAfter", out var listAfter)
                && listAfter.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed payload — return null
        }

        return null;
    }

    internal static string? ExtractCardId(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("action", out var action)
                && action.TryGetProperty("data", out var data)
                && data.TryGetProperty("card", out var card)
                && card.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
