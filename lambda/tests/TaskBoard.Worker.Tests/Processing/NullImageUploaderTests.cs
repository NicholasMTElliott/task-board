using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class NullImageUploaderTests
{
    [Fact]
    public async Task UploadImageAsync_AlwaysReturnsNull()
    {
        var result = await NullImageUploader.Instance.UploadImageAsync(
            "any/path.png", "42", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UploadImageAsync_AnyCardId_ReturnsNull()
    {
        var result = await NullImageUploader.Instance.UploadImageAsync(
            "image.png", "99", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(NullImageUploader.Instance, NullImageUploader.Instance);
    }
}
