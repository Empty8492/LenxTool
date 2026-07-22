using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.Infrastructure.Tests.SystemServices;

public sealed class OpmlFileServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"LenxTool.Opml.{Guid.NewGuid():N}");

    public OpmlFileServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SaveAndLoadRoundTripUsesAtomicDestinationFile()
    {
        string path = Path.Combine(_directory, "共享订阅.opml");
        var service = new OpmlFileService(new OpmlCodec());
        var document = new OpmlDocument(
            "共享订阅",
            [new("示例", "https://example.com/feed.xml", "https://example.com/", ["技术"])]);

        await service.SaveAsync(path, document, CancellationToken.None);
        OpmlDocument loaded = await service.LoadAsync(path, CancellationToken.None);

        Assert.Equal("共享订阅", loaded.Title);
        Assert.Equal("https://example.com/feed.xml", Assert.Single(loaded.Feeds).XmlUrl);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task InvalidExportPreservesExistingDestination()
    {
        string path = Path.Combine(_directory, "existing.opml");
        await File.WriteAllTextAsync(path, "original");
        var service = new OpmlFileService(new OpmlCodec());
        var invalid = new OpmlDocument("", []);

        await Assert.ThrowsAsync<AppException>(() => service.SaveAsync(path, invalid, CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
