namespace TaskBoard.Worker.Processing;

/// <summary>
/// Uploads an image file to remote hosting and returns a URL suitable for use in GitHub issue markdown.
/// </summary>
public interface IImageUploader
{
    /// <summary>
    /// Uploads the image at <paramref name="localFilePath"/> and returns a hosted URL.
    /// Returns null if upload fails, is not supported, or the file is invalid.
    /// </summary>
    Task<string?> UploadImageAsync(string localFilePath, string cardId, CancellationToken cancellationToken);
}
