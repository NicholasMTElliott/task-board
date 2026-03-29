using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskBoard.Worker.Models;

/// <summary>
/// Deserializes a transition target from either:
///   - A plain string (legacy format): "Designing"  → ForColumn("Designing")
///   - An array of action objects (new format): [{ "type": "moveToColumn", "value": "Designing" }, ...]
/// Serializes as an action array.
/// </summary>
public sealed class TransitionTargetConverter : JsonConverter<TransitionTarget>
{
    public override TransitionTarget Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var column = reader.GetString()!;
                return TransitionTarget.ForColumn(column);
            }
            case JsonTokenType.StartArray:
            {
                var actions = JsonSerializer.Deserialize<List<TransitionAction>>(ref reader, options) ?? [];
                return new TransitionTarget(actions);
            }
            default:
                throw new JsonException(
                    $"Expected string or array for transition target, got {reader.TokenType}");
        }
    }

    public override void Write(
        Utf8JsonWriter writer, TransitionTarget value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.Actions, options);
    }
}
