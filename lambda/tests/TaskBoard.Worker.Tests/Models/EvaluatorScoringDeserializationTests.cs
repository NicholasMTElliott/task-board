using System.Text.Json;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests.Models;

/// <summary>
/// Regression tests for <see cref="EvaluatorScoring"/> JSON deserialization.
/// Without <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c> on the
/// enum, <see cref="JsonSerializer"/> rejects string values like
/// <c>"WinnerWithScores"</c> with
/// <c>JsonException: The JSON value could not be converted to EvaluatorScoring</c> —
/// despite every Agent.md and docs/CandidateEvaluation.md example using the
/// string spelling. example-project hit this on a real workflow config and crashed at
/// load time.
/// </summary>
public class EvaluatorScoringDeserializationTests
{
    private static readonly JsonSerializerOptions LoaderOptions = new()
    {
        // Mirrors Program.cs's deserialization options for the workflow config.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string MinimalWorkflowJsonTemplate = """
    {
      "states": {
        "Designing": {
          "name": "Designing",
          "gateType": "agent_run",
          "gitBehavior": "discard",
          "steps": [
            {
              "name": "create_design",
              "role": "designer",
              "taskPrompt": "design it",
              "candidates": [
                { "provider": "docker-claude-cli" },
                { "provider": "docker-opencode" }
              ],
              "evaluator": {
                "role": "evaluator",
                "taskPrompt": "evaluate them",
                "scoring": "{SCORING}"
              }
            }
          ],
          "transitions": {}
        }
      },
      "roles": {
        "designer":  { "model": "m", "systemPrompt": "p", "sections": [] },
        "evaluator": { "model": "m", "systemPrompt": "p", "sections": [] }
      }
    }
    """;

    [Theory]
    [InlineData("WinnerWithScores", EvaluatorScoring.WinnerWithScores)]
    [InlineData("WinnerOnly",       EvaluatorScoring.WinnerOnly)]
    // Case-insensitive matching is the default for JsonStringEnumConverter,
    // so all of these must round-trip too. example-project experimented with multiple
    // casings to debug the original crash; this pins that behavior.
    [InlineData("winnerWithScores", EvaluatorScoring.WinnerWithScores)]
    [InlineData("winneronly",       EvaluatorScoring.WinnerOnly)]
    [InlineData("WINNERWITHSCORES", EvaluatorScoring.WinnerWithScores)]
    public void EvaluatorConfigScoring_DeserializesFromString(
        string scoringValue, EvaluatorScoring expected)
    {
        var json = MinimalWorkflowJsonTemplate.Replace("{SCORING}", scoringValue);

        var config = JsonSerializer.Deserialize<WorkflowConfig>(json, LoaderOptions);

        Assert.NotNull(config);
        var step = config!.States["Designing"].Steps!.Single();
        Assert.NotNull(step.Evaluator);
        Assert.Equal(expected, step.Evaluator!.Scoring);
    }

    [Fact]
    public void EvaluatorConfigScoring_OmittedField_DefaultsToWinnerWithScores()
    {
        // The record's default value is WinnerWithScores; verify omitting
        // the field still produces that default.
        var json = """
        {
          "states": {
            "Designing": {
              "name": "Designing",
              "gateType": "agent_run",
              "gitBehavior": "discard",
              "steps": [
                {
                  "name": "create_design",
                  "role": "designer",
                  "taskPrompt": "design it",
                  "candidates": [{ "provider": "docker-opencode" }],
                  "evaluator": { "role": "evaluator", "taskPrompt": "judge" }
                }
              ],
              "transitions": {}
            }
          },
          "roles": {
            "designer":  { "model": "m", "systemPrompt": "p", "sections": [] },
            "evaluator": { "model": "m", "systemPrompt": "p", "sections": [] }
          }
        }
        """;

        var config = JsonSerializer.Deserialize<WorkflowConfig>(json, LoaderOptions);

        Assert.Equal(
            EvaluatorScoring.WinnerWithScores,
            config!.States["Designing"].Steps!.Single().Evaluator!.Scoring);
    }
}
