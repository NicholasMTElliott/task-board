namespace TaskBoard.Worker.Processing;

/// <summary>
/// No-op IImageUploader for providers that do not support image upload (e.g. Trello, stub).
/// </summary>
public sealed class NullImageUploader : IImageUploader
{
    public static readonly NullImageUploader Instance = new();

    private NullImageUploader() { }

    public Task<string?> UploadImageAsync(string localFilePath, string cardId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
