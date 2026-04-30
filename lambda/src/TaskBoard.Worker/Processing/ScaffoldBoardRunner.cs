using TaskBoard.Worker.Models;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// "Make the board match the workflow" — the write-side companion to
/// <see cref="ValidationRunner"/>. Walks the workflow config, introspects the
/// board, and either prints a plan (default) or applies the safe-to-create
/// subset of changes (with <c>--apply</c>).
///
/// <para>
/// Plan-vs-apply is deliberate: scaffold writes to a shared resource (the
/// project board, the repo's labels). Default dry-run lets the operator
/// review and re-invoke; no interactive y/n confirmation that's easy to
/// misclick. Mirrors `terraform plan` / `terraform apply`.
/// </para>
///
/// <para>
/// v1 surface: creates new single-select fields and creates labels. Adding
/// options to existing fields and creating Status columns (project columns)
/// are surfaced as warnings — the operator does these via the project UI in
/// seconds, and the GitHub Projects v2 GraphQL surface for option mutation
/// has enough rough edges that doing it automatically wasn't justified for
/// the v1 cut.
/// </para>
/// </summary>
public sealed class ScaffoldBoardRunner
{
    private readonly WorkflowConfig _workflow;
    private readonly IBoardShapeProbe _probe;
    private readonly IBoardShapeApplier _applier;
    private readonly ILogger _logger;
    private readonly TextWriter _stdout;

    public ScaffoldBoardRunner(
        WorkflowConfig workflow,
        IBoardShapeProbe probe,
        IBoardShapeApplier applier,
        ILogger logger,
        TextWriter? stdout = null)
    {
        _workflow = workflow;
        _probe = probe;
        _applier = applier;
        _logger = logger;
        _stdout = stdout ?? Console.Out;
    }

    public async Task<int> RunAsync(string boardId, bool apply, CancellationToken cancellationToken)
    {
        BoardShape? shape;
        try
        {
            shape = await _probe.ProbeAsync(boardId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to introspect board {BoardId}.", boardId);
            return 1;
        }

        if (shape is null)
        {
            _logger.LogError(
                "Board introspection isn't available for the current provider. " +
                "Scaffold currently supports BoardProvider=github only.");
            return 1;
        }

        var plan = BoardScaffoldDiff.Compute(_workflow, shape);

        WritePlan(boardId, plan);

        if (plan.IsEmpty)
            return 0;

        if (!apply)
        {
            _stdout.WriteLine();
            if (plan.HasCreatableActions)
                _stdout.WriteLine("Re-run with --apply to perform the CREATE actions.");
            if (plan.HasManualActions)
                _stdout.WriteLine("Manual actions (WARN) require operator work in the project UI; --apply does not handle them.");
            return 0;
        }

        if (!_applier.CanApply)
        {
            _logger.LogError(
                "--apply requested but no applier is registered for the current board provider.");
            return 1;
        }

        if (!plan.HasCreatableActions)
        {
            _stdout.WriteLine();
            _stdout.WriteLine("Nothing to apply. The plan has only manual actions; complete those in the project UI.");
            return 0;
        }

        return await ApplyPlanAsync(boardId, plan, cancellationToken);
    }

    private void WritePlan(string boardId, BoardScaffoldPlan plan)
    {
        _stdout.WriteLine($"Scaffold plan for board {boardId}:");
        _stdout.WriteLine();

        if (plan.IsEmpty)
        {
            _stdout.WriteLine("  Nothing to do — board shape matches the workflow.");
            return;
        }

        if (plan.MissingFields.Count > 0)
        {
            _stdout.WriteLine($"  CREATE {plan.MissingFields.Count} field(s):");
            foreach (var f in plan.MissingFields)
            {
                if (f.Options.Count == 0)
                    _stdout.WriteLine($"    + {f.Name}: (no options inferable from the workflow)");
                else
                    _stdout.WriteLine($"    + {f.Name}: single-select with options [{string.Join(", ", f.Options)}]");
            }
            _stdout.WriteLine();
        }

        if (plan.MissingLabels.Count > 0)
        {
            _stdout.WriteLine($"  CREATE {plan.MissingLabels.Count} label(s):");
            foreach (var l in plan.MissingLabels)
                _stdout.WriteLine($"    + {l}");
            _stdout.WriteLine();
        }

        if (plan.FieldOptionGaps.Count > 0)
        {
            _stdout.WriteLine($"  WARN {plan.FieldOptionGaps.Count} existing field(s) need option(s) added manually:");
            foreach (var g in plan.FieldOptionGaps)
            {
                _stdout.WriteLine(
                    $"    {g.FieldName} (existing: [{string.Join(", ", g.ExistingOptions)}]) " +
                    $"missing: [{string.Join(", ", g.MissingOptions)}]");
            }
            _stdout.WriteLine("    → Open the project's field settings (single-select edit) to add these options.");
            _stdout.WriteLine();
        }

        if (plan.MissingColumns.Count > 0)
        {
            _stdout.WriteLine($"  WARN {plan.MissingColumns.Count} column(s) need to be added to the Status field manually:");
            _stdout.WriteLine($"    {string.Join(", ", plan.MissingColumns)}");
            _stdout.WriteLine("    → Open the project, click the Status header → field settings → add options.");
            _stdout.WriteLine();
        }
    }

    private async Task<int> ApplyPlanAsync(
        string boardId, BoardScaffoldPlan plan, CancellationToken cancellationToken)
    {
        _stdout.WriteLine();
        _stdout.WriteLine("Applying...");
        _stdout.WriteLine();

        var failures = 0;

        foreach (var f in plan.MissingFields)
        {
            var result = await _applier.CreateSingleSelectFieldAsync(
                boardId, f.Name, f.Options, cancellationToken);
            WriteResult(result);
            if (result.Outcome == ApplyOutcome.Failed) failures++;
        }

        foreach (var l in plan.MissingLabels)
        {
            var result = await _applier.CreateLabelAsync(l, cancellationToken);
            WriteResult(result);
            if (result.Outcome == ApplyOutcome.Failed) failures++;
        }

        _stdout.WriteLine();
        if (failures == 0)
        {
            _stdout.WriteLine($"Apply complete. {plan.MissingFields.Count + plan.MissingLabels.Count} action(s) processed.");
            if (plan.HasManualActions)
                _stdout.WriteLine("Note: WARN items remain — complete those in the project UI.");
            return 0;
        }

        _stdout.WriteLine($"Apply finished with {failures} failure(s). Re-run after addressing them.");
        return 1;
    }

    private void WriteResult(ApplyResult result)
    {
        var marker = result.Outcome switch
        {
            ApplyOutcome.Created       => "  +",
            ApplyOutcome.AlreadyExists => "  =",
            ApplyOutcome.Failed        => "  !",
            _ => "  ?",
        };
        _stdout.Write($"{marker} {result.Description}");
        if (result.Error is not null)
            _stdout.Write($"  [{result.Error}]");
        _stdout.WriteLine();
    }
}
