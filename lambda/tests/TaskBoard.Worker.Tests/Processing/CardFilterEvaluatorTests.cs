using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class CardFilterEvaluatorTests
{
    private static BoardCard MakeCard(
        IReadOnlyList<string>? labels = null,
        IReadOnlyList<string>? assignees = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new("card-1", "Test Card", "Body", "col-1", metadata, labels, assignees);

    // --- PassesAll ---

    [Fact]
    public void PassesAll_NullFilters_ReturnsTrue()
    {
        var card = MakeCard();
        Assert.True(CardFilterEvaluator.PassesAll(card, null));
    }

    [Fact]
    public void PassesAll_EmptyFilters_ReturnsTrue()
    {
        var card = MakeCard();
        Assert.True(CardFilterEvaluator.PassesAll(card, []));
    }

    [Fact]
    public void PassesAll_AllFiltersPass_ReturnsTrue()
    {
        var card = MakeCard(labels: ["ai-ready"], assignees: []);
        var filters = new List<CardFilter>
        {
            new(FilterTypes.Label, FilterOperators.Exists, "ai-ready"),
            new(FilterTypes.Assignee, FilterOperators.IsEmpty),
        };
        Assert.True(CardFilterEvaluator.PassesAll(card, filters));
    }

    [Fact]
    public void PassesAll_OneFilterFails_ReturnsFalse()
    {
        var card = MakeCard(labels: ["ai-ready"], assignees: ["alice"]);
        var filters = new List<CardFilter>
        {
            new(FilterTypes.Label, FilterOperators.Exists, "ai-ready"),
            new(FilterTypes.Assignee, FilterOperators.IsEmpty), // fails — alice is assigned
        };
        Assert.False(CardFilterEvaluator.PassesAll(card, filters));
    }

    // --- Label: exists ---

    [Fact]
    public void Evaluate_LabelExists_LabelPresent_ReturnsTrue()
    {
        var card = MakeCard(labels: ["ai-ready", "urgent"]);
        var filter = new CardFilter(FilterTypes.Label, FilterOperators.Exists, "ai-ready");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_LabelExists_LabelAbsent_ReturnsFalse()
    {
        var card = MakeCard(labels: ["other"]);
        var filter = new CardFilter(FilterTypes.Label, FilterOperators.Exists, "ai-ready");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_LabelExists_NullLabels_ReturnsFalse()
    {
        var card = MakeCard(labels: null);
        var filter = new CardFilter(FilterTypes.Label, FilterOperators.Exists, "ai-ready");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_LabelExists_CaseInsensitive()
    {
        var card = MakeCard(labels: ["AI-Ready"]);
        var filter = new CardFilter(FilterTypes.Label, FilterOperators.Exists, "ai-ready");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Label: notExists ---

    [Fact]
    public void Evaluate_LabelNotExists_LabelAbsent_ReturnsTrue()
    {
        var card = MakeCard(labels: ["other"]);
        var filter = new CardFilter(FilterTypes.Label, FilterOperators.NotExists, "do-not-automate");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_LabelNotExists_LabelPresent_ReturnsFalse()
    {
        var card = MakeCard(labels: ["do-not-automate"]);
        var filter = new CardFilter(FilterTypes.Label, FilterOperators.NotExists, "do-not-automate");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_LabelNotExists_NullLabels_ReturnsTrue()
    {
        var card = MakeCard(labels: null);
        var filter = new CardFilter(FilterTypes.Label, FilterOperators.NotExists, "do-not-automate");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Label: invalid operator ---

    [Fact]
    public void Evaluate_LabelInvalidOperator_ThrowsInvalidOperation()
    {
        var card = MakeCard(labels: ["x"]);
        var filter = new CardFilter(FilterTypes.Label, "invalid-op", "x");
        Assert.Throws<InvalidOperationException>(() => CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Assignee: isEmpty ---

    [Fact]
    public void Evaluate_AssigneeIsEmpty_NoAssignees_ReturnsTrue()
    {
        var card = MakeCard(assignees: []);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty);
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_AssigneeIsEmpty_NullAssignees_ReturnsTrue()
    {
        var card = MakeCard(assignees: null);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty);
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_AssigneeIsEmpty_HasAssignee_ReturnsFalse()
    {
        var card = MakeCard(assignees: ["alice"]);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty);
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Assignee: isNotEmpty ---

    [Fact]
    public void Evaluate_AssigneeIsNotEmpty_HasAssignee_ReturnsTrue()
    {
        var card = MakeCard(assignees: ["alice"]);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.IsNotEmpty);
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_AssigneeIsNotEmpty_Empty_ReturnsFalse()
    {
        var card = MakeCard(assignees: []);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.IsNotEmpty);
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Assignee: equals ---

    [Fact]
    public void Evaluate_AssigneeEquals_MatchingAssignee_ReturnsTrue()
    {
        var card = MakeCard(assignees: ["alice", "bob"]);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.Equals, "alice");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_AssigneeEquals_NoMatch_ReturnsFalse()
    {
        var card = MakeCard(assignees: ["bob"]);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.Equals, "alice");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_AssigneeEquals_CaseInsensitive()
    {
        var card = MakeCard(assignees: ["Alice"]);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.Equals, "alice");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Assignee: notEquals ---

    [Fact]
    public void Evaluate_AssigneeNotEquals_DifferentAssignee_ReturnsTrue()
    {
        var card = MakeCard(assignees: ["bob"]);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.NotEquals, "alice");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_AssigneeNotEquals_MatchingAssignee_ReturnsFalse()
    {
        var card = MakeCard(assignees: ["alice"]);
        var filter = new CardFilter(FilterTypes.Assignee, FilterOperators.NotEquals, "alice");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Field: equals ---

    [Fact]
    public void Evaluate_FieldEquals_MatchingValue_ReturnsTrue()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["team"] = "backend" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.Equals, "backend", Field: "team");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldEquals_NonMatchingValue_ReturnsFalse()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["team"] = "frontend" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.Equals, "backend", Field: "team");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldEquals_FieldAbsent_ReturnsFalse()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["other"] = "backend" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.Equals, "backend", Field: "team");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldEquals_CaseInsensitiveKey()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["Team"] = "backend" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.Equals, "backend", Field: "team");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldEquals_CaseInsensitiveValue()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["team"] = "Backend" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.Equals, "backend", Field: "team");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Field: notEquals ---

    [Fact]
    public void Evaluate_FieldNotEquals_DifferentValue_ReturnsTrue()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["team"] = "frontend" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.NotEquals, "backend", Field: "team");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldNotEquals_MatchingValue_ReturnsFalse()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["team"] = "backend" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.NotEquals, "backend", Field: "team");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Field: isEmpty ---

    [Fact]
    public void Evaluate_FieldIsEmpty_FieldAbsent_ReturnsTrue()
    {
        var card = MakeCard(metadata: new Dictionary<string, string>());
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.IsEmpty, Field: "priority");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldIsEmpty_NullMetadata_ReturnsTrue()
    {
        var card = MakeCard(metadata: null);
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.IsEmpty, Field: "priority");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldIsEmpty_FieldPresent_ReturnsFalse()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["priority"] = "P1" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.IsEmpty, Field: "priority");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Field: isNotEmpty ---

    [Fact]
    public void Evaluate_FieldIsNotEmpty_FieldPresent_ReturnsTrue()
    {
        var card = MakeCard(metadata: new Dictionary<string, string> { ["priority"] = "P1" });
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.IsNotEmpty, Field: "priority");
        Assert.True(CardFilterEvaluator.Evaluate(card, filter));
    }

    [Fact]
    public void Evaluate_FieldIsNotEmpty_FieldAbsent_ReturnsFalse()
    {
        var card = MakeCard(metadata: new Dictionary<string, string>());
        var filter = new CardFilter(FilterTypes.Field, FilterOperators.IsNotEmpty, Field: "priority");
        Assert.False(CardFilterEvaluator.Evaluate(card, filter));
    }

    // --- Unknown filter type ---

    [Fact]
    public void Evaluate_UnknownFilterType_ThrowsInvalidOperation()
    {
        var card = MakeCard();
        var filter = new CardFilter("unknown-type", FilterOperators.Exists, "value");
        Assert.Throws<InvalidOperationException>(() => CardFilterEvaluator.Evaluate(card, filter));
    }
}
