namespace TaskBoard.Worker.Validation;

public enum ValidationSeverity { Error, Warning, Info }

public sealed record ValidationFinding(
    ValidationSeverity Severity,
    string Category,
    string Path,
    string Message,
    string? Hint = null);
