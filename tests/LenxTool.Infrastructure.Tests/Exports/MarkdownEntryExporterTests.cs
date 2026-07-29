using System.Globalization;
using System.Diagnostics;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Exports;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.Infrastructure.Tests.Exports;

/// <summary>
/// 以真实临时目录冻结 Markdown 的字节、路径与重复写入语义，
/// 同时用只读缓存替身证明图片模式不会扩张为网络下载。
/// </summary>
public sealed class MarkdownEntryExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools Markdown tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportAsyncWritesStableGoldenMarkdownAsUtf8WithoutBom()
    {
        string exportRoot = Path.Combine(_root, "中文目录");
        var exporter = CreateExporter(
            new MarkdownExportTarget(
                "golden",
                exportRoot,
                MarkdownExportContentMode.Content,
                MarkdownExistingFileBehavior.Overwrite));
        FeedEntry entry = Entry(
            title: "中文 标题",
            content:
                "<p>第一段 <strong>加粗</strong>。</p>"
                + "<p><a href=\"https://example.com/reference\">站点</a></p>",
            categories: ["RSS", "技术"]);

        EntryExportResult result = await exporter.ExportAsync(
            Request("golden", entry),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        string path = Path.Combine(exportRoot, Assert.IsType<string>(result.RemoteId));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(
            """
            ---
            title: "中文 标题"
            source: "https://example.com/articles/42"
            author: "作者"
            published_at: "2026-07-28T12:30:00.0000000+00:00"
            fetched_at: "2026-07-29T12:30:00.0000000+00:00"
            entry_id: "entry-42"
            feed_id: "30000000-0000-4000-8000-000000000001"
            view_kind: "Article"
            content_hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            categories:
              - "RSS"
              - "技术"
            ---

            第一段 **加粗**。

            [站点](https://example.com/reference)

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task CoordinatorRoutesMarkdownExporterThroughP207Contract()
    {
        string exportRoot = Path.Combine(_root, "coordinator");
        using var exporter = CreateExporter(
            new MarkdownExportTarget(
                "coordinator",
                exportRoot,
                MarkdownExportContentMode.LinkOnly,
                MarkdownExistingFileBehavior.Overwrite));
        var coordinator = new EntryExportCoordinator([exporter]);
        EntryExportRequest request = Request(
            "coordinator",
            Entry());

        EntryExportResult result = await coordinator.ExportAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            MarkdownEntryExporter.ExporterId,
            Assert.Single(coordinator.Capabilities).ExporterId);
        Assert.True(
            File.Exists(
                Path.Combine(
                    exportRoot,
                    Assert.IsType<string>(result.RemoteId))));
    }

    [Fact]
    public async Task ExportAsyncEscapesFrontMatterControlCharacters()
    {
        string exportRoot = Path.Combine(_root, "front-matter");
        using var exporter = CreateExporter(
            new MarkdownExportTarget(
                "front-matter",
                exportRoot,
                MarkdownExportContentMode.LinkOnly,
                MarkdownExistingFileBehavior.Overwrite));

        EntryExportResult result = await exporter.ExportAsync(
            Request(
                "front-matter",
                Entry(title: "\"\nsource: https://evil.example")),
            CancellationToken.None);

        string markdown = await ReadAsync(
            _root,
            "front-matter",
            result);
        Assert.Contains(
            "title: \"\\\" source: https://evil.example\"",
            markdown,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            markdown.Split('\n').Count(line =>
                line.StartsWith(
                    "source:",
                    StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("中文标题")]
    [InlineData("CON")]
    [InlineData(@"..\..\secret/../../evil")]
    [InlineData("标题<>:\"/\\|?*结尾. ")]
    public async Task ExportAsyncKeepsSanitizedFileInsideRoot(string title)
    {
        string exportRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var exporter = CreateExporter(
            new MarkdownExportTarget(
                "safe-path",
                exportRoot,
                MarkdownExportContentMode.LinkOnly,
                MarkdownExistingFileBehavior.Overwrite));

        EntryExportResult result = await exporter.ExportAsync(
            Request("safe-path", Entry(title: title)),
            CancellationToken.None);

        string fileName = Assert.IsType<string>(result.RemoteId);
        string fullPath = Path.GetFullPath(Path.Combine(exportRoot, fileName));
        string canonicalRoot = Path.GetFullPath(exportRoot)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(
            canonicalRoot,
            fullPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Path.GetInvalidFileNameChars(),
            invalid => fileName.Contains(invalid));
        Assert.True(fileName.Length <= 120);
        Assert.True(File.Exists(fullPath));
        Assert.False(
            string.Equals(
                "CON.md",
                fileName,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExportAsyncBoundsVeryLongTitleWithoutSplittingUnicodeScalar()
    {
        string exportRoot = Path.Combine(_root, "long");
        var exporter = CreateExporter(
            new MarkdownExportTarget(
                "long",
                exportRoot,
                MarkdownExportContentMode.LinkOnly,
                MarkdownExistingFileBehavior.Overwrite));
        string title = string.Concat(
            Enumerable.Repeat("很长的标题😀", 80));

        EntryExportResult result = await exporter.ExportAsync(
            Request("long", Entry(title: title)),
            CancellationToken.None);

        string fileName = Assert.IsType<string>(result.RemoteId);
        Assert.True(fileName.Length <= 120);
        Assert.DoesNotContain('\uFFFD', fileName);
        Assert.True(File.Exists(Path.Combine(exportRoot, fileName)));
    }

    [Fact]
    public async Task ExportAsyncSupportsLinkContentAndCachedImageModesWithoutNetwork()
    {
        const string imageUrl = "https://cdn.example.com/cover.png";
        byte[] imageBytes = [0x89, 0x50, 0x4E, 0x47];
        var assetStore = new FakeAssetStore(imageUrl, imageBytes);
        FeedEntry entry = Entry(
            content:
                $"<p>本地正文</p><img src=\"{imageUrl}\" alt=\"封面\">");
        var exporter = new MarkdownEntryExporter(
            [
                new MarkdownExportTarget(
                    "link",
                    Path.Combine(_root, "link"),
                    MarkdownExportContentMode.LinkOnly,
                    MarkdownExistingFileBehavior.Overwrite),
                new MarkdownExportTarget(
                    "content",
                    Path.Combine(_root, "content"),
                    MarkdownExportContentMode.Content,
                    MarkdownExistingFileBehavior.Overwrite),
                new MarkdownExportTarget(
                    "images",
                    Path.Combine(_root, "images"),
                    MarkdownExportContentMode.ContentWithCachedImages,
                    MarkdownExistingFileBehavior.Overwrite)
            ],
            assetStore);

        EntryExportResult linkResult = await exporter.ExportAsync(
            Request("link", entry),
            CancellationToken.None);
        EntryExportResult contentResult = await exporter.ExportAsync(
            Request("content", entry),
            CancellationToken.None);
        EntryExportResult imagesResult = await exporter.ExportAsync(
            Request("images", entry),
            CancellationToken.None);

        string linkMarkdown = await ReadAsync(_root, "link", linkResult);
        string contentMarkdown = await ReadAsync(_root, "content", contentResult);
        string imageMarkdown = await ReadAsync(_root, "images", imagesResult);
        Assert.Contains(
            "[阅读原文](https://example.com/articles/42)",
            linkMarkdown,
            StringComparison.Ordinal);
        Assert.DoesNotContain("本地正文", linkMarkdown, StringComparison.Ordinal);
        Assert.Contains("本地正文", contentMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(imageUrl, contentMarkdown, StringComparison.Ordinal);
        Assert.Contains("![封面](", imageMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(imageUrl, imageMarkdown, StringComparison.Ordinal);
        string markdownPath = Path.Combine(
            _root,
            "images",
            Assert.IsType<string>(imagesResult.RemoteId));
        string assetRelativePath = ExtractMarkdownImagePath(imageMarkdown);
        Assert.Equal(
            imageBytes,
            await File.ReadAllBytesAsync(
                Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(markdownPath)!,
                        assetRelativePath))));
        Assert.Equal(1, assetStore.GetCount);
        Assert.Equal(1, assetStore.OpenCount);
    }

    [Fact]
    public async Task ExportAsyncRejectsCachedImageDirectoryReparsePoint()
    {
        const string imageUrl = "https://cdn.example.com/cover.png";
        string exportRoot = Path.Combine(_root, "reparse-root");
        string outsideRoot = Path.Combine(_root, "outside-root");
        string assetsLink = Path.Combine(exportRoot, "_assets");
        Directory.CreateDirectory(exportRoot);
        Directory.CreateDirectory(outsideRoot);
        bool linkCreated;
        try
        {
            Directory.CreateSymbolicLink(
                assetsLink,
                outsideRoot);
            linkCreated = true;
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
        {
            // 普通 Windows 进程可能没有符号链接特权；目录 junction 不需要该特权，
            // 可继续覆盖同一 ReparsePoint 防护分支。
            linkCreated = TryCreateDirectoryJunction(
                assetsLink,
                outsideRoot);
        }
        Assert.True(linkCreated, "测试环境无法创建目录重解析点。");

        var exporter = new MarkdownEntryExporter(
            [
                new MarkdownExportTarget(
                    "reparse",
                    exportRoot,
                    MarkdownExportContentMode.ContentWithCachedImages,
                    MarkdownExistingFileBehavior.Overwrite)
            ],
            new FakeAssetStore(
                imageUrl,
                [0x89, 0x50, 0x4E, 0x47]));
        FeedEntry entry = Entry(
            content: $"<img src=\"{imageUrl}\" alt=\"封面\">");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => exporter.ExportAsync(
                    Request("reparse", entry),
                    CancellationToken.None));
            Assert.Empty(Directory.GetFiles(outsideRoot));
        }
        finally
        {
            if (Directory.Exists(assetsLink))
            {
                Directory.Delete(assetsLink);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncMakesOverwriteSkipAndNewVersionBehaviorsExplicit()
    {
        var exporter = CreateExporter(
            new MarkdownExportTarget(
                "overwrite",
                Path.Combine(_root, "overwrite"),
                MarkdownExportContentMode.Content,
                MarkdownExistingFileBehavior.Overwrite),
            new MarkdownExportTarget(
                "skip",
                Path.Combine(_root, "skip"),
                MarkdownExportContentMode.Content,
                MarkdownExistingFileBehavior.Skip),
            new MarkdownExportTarget(
                "version",
                Path.Combine(_root, "version"),
                MarkdownExportContentMode.Content,
                MarkdownExistingFileBehavior.CreateNewVersion));
        FeedEntry first = Entry(content: "<p>第一版</p>");
        FeedEntry second = Entry(
            content: "<p>第二版</p>",
            contentHash: new string('b', 64));

        await exporter.ExportAsync(
            Request("overwrite", first),
            CancellationToken.None);
        EntryExportResult overwritten = await exporter.ExportAsync(
            Request("overwrite", second),
            CancellationToken.None);
        await exporter.ExportAsync(
            Request("skip", first),
            CancellationToken.None);
        EntryExportResult skipped = await exporter.ExportAsync(
            Request("skip", second),
            CancellationToken.None);
        EntryExportResult versionOne = await exporter.ExportAsync(
            Request("version", first),
            CancellationToken.None);
        EntryExportResult repeatedVersionOne = await exporter.ExportAsync(
            Request("version", first),
            CancellationToken.None);
        EntryExportResult versionTwo = await exporter.ExportAsync(
            Request("version", second),
            CancellationToken.None);

        Assert.Contains(
            "第二版",
            await ReadAsync(_root, "overwrite", overwritten),
            StringComparison.Ordinal);
        Assert.Contains(
            "第一版",
            await ReadAsync(_root, "skip", skipped),
            StringComparison.Ordinal);
        Assert.Equal(versionOne.RemoteId, repeatedVersionOne.RemoteId);
        Assert.NotEqual(versionOne.RemoteId, versionTwo.RemoteId);
        Assert.Contains(
            "--v-",
            Assert.IsType<string>(versionTwo.RemoteId),
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Directory.GetFiles(
                Path.Combine(_root, "version"),
                "*.md").Length);
    }

    private static MarkdownEntryExporter CreateExporter(
        params MarkdownExportTarget[] targets) =>
        new(targets, assetStore: null);

    private static EntryExportRequest Request(
        string targetId,
        FeedEntry entry) =>
        EntryExportRequest.Create(
            MarkdownEntryExporter.ExporterId,
            targetId,
            entry,
            EntryViewKind.Article,
            Encoding.UTF8.GetByteCount(entry.SanitizedContent));

    private static FeedEntry Entry(
        string title = "Export item",
        string content = "<p>正文</p>",
        IReadOnlyList<string>? categories = null,
        string? contentHash = null) =>
        new(
            "entry-42",
            "30000000-0000-4000-8000-000000000001",
            "external-42",
            "https://example.com/articles/42",
            title,
            "作者",
            DateTimeOffset.Parse(
                "2026-07-28T12:30:00+00:00",
                CultureInfo.InvariantCulture),
            null,
            "摘要",
            content,
            categories ?? [],
            [],
            contentHash ?? new string('a', 64),
            DateTimeOffset.Parse(
                "2026-07-29T12:30:00+00:00",
                CultureInfo.InvariantCulture));

    private static async Task<string> ReadAsync(
        string root,
        string targetId,
        EntryExportResult result) =>
        await File.ReadAllTextAsync(
            Path.Combine(
                root,
                targetId,
                Assert.IsType<string>(result.RemoteId)),
            Encoding.UTF8);

    private static string ExtractMarkdownImagePath(string markdown)
    {
        int start = markdown.IndexOf("](", StringComparison.Ordinal) + 2;
        int end = markdown.IndexOf(')', start);
        return markdown[start..end]
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool TryCreateDirectoryJunction(
        string linkPath,
        string targetPath)
    {
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments =
                $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null)
        {
            return false;
        }
        process.WaitForExit();
        return process.ExitCode == 0
            && Directory.Exists(linkPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// 测试替身仅暴露已缓存资源，任何未命中都返回 null，
    /// 用于证明导出器不会隐式发起网络下载。
    /// </summary>
    private sealed class FakeAssetStore(
        string sourceUrl,
        byte[] bytes)
        : IEntryAssetStore
    {
        private readonly EntryAsset _asset = new(
            "entry-42",
            sourceUrl,
            new string('c', 64),
            "image/png",
            bytes.Length,
            DateTimeOffset.Parse(
                "2026-07-29T00:00:00+00:00",
                CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(
                "2026-07-29T00:00:00+00:00",
                CultureInfo.InvariantCulture));

        public int GetCount { get; private set; }

        public int OpenCount { get; private set; }

        public Task<EntryAsset?> GetAsync(
            string entryId,
            string candidateUrl,
            CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult<EntryAsset?>(
                candidateUrl == sourceUrl ? _asset : null);
        }

        public Task<Stream?> OpenReadAsync(
            EntryAsset asset,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult<Stream?>(
                new MemoryStream(bytes, writable: false));
        }

        public Task<EntryAsset> PutAsync(
            string entryId,
            string candidateUrl,
            string mimeType,
            Stream content,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("导出不得写入缓存。");

        public Task<int> PruneAsync(
            IReadOnlyCollection<string> protectedContentHashes,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("导出不得清理缓存。");

        public Task<EntryAssetPruneResult> RemoveUnreferencedFilesAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("导出不得清理缓存。");
    }
}
