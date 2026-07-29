using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.Infrastructure.Tests.Exports;

public sealed class ObsidianEntryExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools Obsidian tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportAsyncAllowsLegacyDefaultScopeToReloadCurrentTargetAndPolicy()
    {
        string firstVault = CreateDirectory("知识库一");
        string secondVault = CreateDirectory("知识库二");
        var store = new MutableTargetStore(Target(firstVault));
        var policies = new MutablePolicyService();
        using var exporter = new ObsidianEntryExporter(
            store,
            policies,
            assetStore: null);
        EntryExportRequest request = Request(Entry());

        EntryExportResult first = await exporter.ExportAsync(
            request,
            CancellationToken.None);
        store.Current = Target(secondVault);
        EntryExportResult second = await exporter.ExportAsync(
            request,
            CancellationToken.None);

        Assert.True(
            File.Exists(
                Path.Combine(
                    firstVault,
                    "Lenx",
                    Assert.IsType<string>(first.RemoteId))));
        Assert.True(
            File.Exists(
                Path.Combine(
                    secondVault,
                    "Lenx",
                    Assert.IsType<string>(second.RemoteId))));
        Assert.Equal(2, store.GetCount);
        Assert.Equal(2, policies.GetCount);
        Assert.All(
            policies.RequestedScopes,
            scope => Assert.Equal(
                EntryIntegrationPolicyScope.Active,
                scope));
    }

    [Fact]
    public async Task ExportAsyncAcceptsOpaqueVersionedQueueTargetId()
    {
        string vault = CreateDirectory("versioned-target");
        ObsidianExportTarget target = Target(vault);
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(target),
            new MutablePolicyService(),
            assetStore: null);
        EntryExportRequest request = EntryExportRequest.Create(
            ObsidianEntryExporter.ExporterId,
            target.CreateQueueTargetId(),
            Entry(),
            EntryViewKind.Article,
            Encoding.UTF8.GetByteCount(Entry().SanitizedContent));

        EntryExportResult result = await exporter.ExportAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(request.IdempotencyKey, result.IdempotencyKey);
        Assert.True(
            File.Exists(
                Path.Combine(
                    vault,
                    "Lenx",
                    Assert.IsType<string>(result.RemoteId))));
    }

    [Fact]
    public async Task ExportAsyncRejectsStaleVersionedScopeBeforeWritingAndAcceptsCurrentScope()
    {
        string firstVault = CreateDirectory("scope-a");
        string secondVault = CreateDirectory("scope-b");
        ObsidianExportTarget firstTarget = Target(firstVault);
        ObsidianExportTarget secondTarget = Target(secondVault);
        var store = new MutableTargetStore(firstTarget);
        using var exporter = new ObsidianEntryExporter(
            store,
            new MutablePolicyService(),
            assetStore: null);
        EntryExportRequest staleRequest = EntryExportRequest.Create(
            ObsidianEntryExporter.ExporterId,
            firstTarget.CreateQueueTargetId(),
            Entry(),
            EntryViewKind.Article,
            Encoding.UTF8.GetByteCount(Entry().SanitizedContent));
        EntryExportRequest currentRequest = EntryExportRequest.Create(
            ObsidianEntryExporter.ExporterId,
            secondTarget.CreateQueueTargetId(),
            Entry(),
            EntryViewKind.Article,
            Encoding.UTF8.GetByteCount(Entry().SanitizedContent));
        store.Current = secondTarget;

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    staleRequest,
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.Conflict,
            exception.Error.Code);
        Assert.False(exception.Error.IsRetryable);
        Assert.Empty(
            Directory.GetFiles(
                _root,
                "*.md",
                SearchOption.AllDirectories));

        EntryExportResult result = await exporter.ExportAsync(
            currentRequest,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(
            Directory.GetFiles(
                firstVault,
                "*.md",
                SearchOption.AllDirectories));
        Assert.Single(
            Directory.GetFiles(
                secondVault,
                "*.md",
                SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    public async Task ExportAsyncKeepsVersionedScopeSemanticallySafeAcrossAbsentTemplates(
        string? queuedTemplate,
        string? currentTemplate)
    {
        string vault = CreateDirectory("absent-template-scope");
        ObsidianExportTarget queuedTarget = Target(vault) with
        {
            TemplateMarkdown = queuedTemplate
        };
        ObsidianExportTarget currentTarget = Target(vault) with
        {
            TemplateMarkdown = currentTemplate
        };
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(currentTarget),
            new MutablePolicyService(),
            assetStore: null);
        FeedEntry entry = Entry(content: "<p>canonical template body</p>");
        EntryExportRequest request = EntryExportRequest.Create(
            ObsidianEntryExporter.ExporterId,
            queuedTarget.CreateQueueTargetId(),
            entry,
            EntryViewKind.Article,
            Encoding.UTF8.GetByteCount(entry.SanitizedContent));

        EntryExportResult result = await exporter.ExportAsync(
            request,
            CancellationToken.None);
        string markdown = await File.ReadAllTextAsync(
            Path.Combine(
                vault,
                "Lenx",
                Assert.IsType<string>(result.RemoteId)),
            Encoding.UTF8);

        Assert.Equal(
            queuedTarget.CreateQueueTargetId(),
            currentTarget.CreateQueueTargetId());
        Assert.Contains(
            "canonical template body",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsyncFailsClosedWhenPolicyIsRevoked()
    {
        string vault = CreateDirectory("revoked");
        var store = new MutableTargetStore(Target(vault));
        var policies = new MutablePolicyService();
        using var exporter = new ObsidianEntryExporter(
            store,
            policies,
            assetStore: null);
        await exporter.ExportAsync(
            Request(Entry()),
            CancellationToken.None);
        policies.Enabled = false;

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(
                        Entry(
                            id: "entry-revoked",
                            contentHash: new string('b', 64))),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.AccessDenied,
            exception.Error.Code);
        Assert.Single(
            Directory.GetFiles(
                Path.Combine(vault, "Lenx"),
                "*.md"));
    }

    [Fact]
    public async Task ExportAsyncRendersBoundedTemplateTagsAndSafeSourceLink()
    {
        string vault = CreateDirectory("中文知识库");
        var store = new MutableTargetStore(
            new(
                ObsidianEntryExporter.TargetId,
                vault,
                @"Lenx\中文收件箱",
                """
                # {{title}}

                {{content}}

                作者：{{author}}
                发布：{{published_at}}
                来源：{{source_url}}
                """,
                ["#RSS", "技术", "rss"],
                IncludeSourceLink: true));
        using var exporter = new ObsidianEntryExporter(
            store,
            new MutablePolicyService(),
            assetStore: null);

        EntryExportResult result = await exporter.ExportAsync(
            Request(
                Entry(
                    title: "中文标题\nsource: evil",
                    content: "<p>正文<strong>加粗</strong></p>"
                        + "<script>steal()</script>")),
            CancellationToken.None);

        string markdown = await File.ReadAllTextAsync(
            Path.Combine(
                vault,
                "Lenx",
                "中文收件箱",
                Assert.IsType<string>(result.RemoteId)),
            Encoding.UTF8);
        Assert.Contains("tags:\n  - \"RSS\"\n  - \"技术\"", markdown);
        Assert.Contains(@"# 中文标题 source\: evil", markdown);
        Assert.Contains("正文**加粗**", markdown);
        Assert.Contains(
            "来源：<https://example.com/articles/42>",
            markdown);
        Assert.Contains(
            "[阅读原文](<https://example.com/articles/42>)",
            markdown);
        Assert.DoesNotContain("steal()", markdown);
    }

    [Fact]
    public async Task ExportAsyncRejectsRelativeDirectoryEscapeWithoutWriting()
    {
        string vault = CreateDirectory("escape");
        var store = new MutableTargetStore(
            Target(vault) with
            {
                RelativeDirectory = @"..\outside"
            });
        using var exporter = new ObsidianEntryExporter(
            store,
            new MutablePolicyService(),
            assetStore: null);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.InvalidRequest,
            exception.Error.Code);
        Assert.Empty(
            Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportAsyncRejectsJunctionInsideVault()
    {
        string vault = CreateDirectory("junction-vault");
        string outside = CreateDirectory("junction-outside");
        string link = Path.Combine(vault, "Lenx");
        if (!TryCreateDirectoryJunction(link, outside))
        {
            return;
        }
        var store = new MutableTargetStore(Target(vault));
        using var exporter = new ObsidianEntryExporter(
            store,
            new MutablePolicyService(),
            assetStore: null);
        try
        {
            EntryExportException exception =
                await Assert.ThrowsAsync<EntryExportException>(
                    () => exporter.ExportAsync(
                        Request(Entry()),
                        CancellationToken.None));

            Assert.Equal(
                EntryExportErrorCode.AccessDenied,
                exception.Error.Code);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncIsIdempotentAndNeverOverwritesExistingVersion()
    {
        string vault = CreateDirectory("idempotent");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null);
        EntryExportRequest request = Request(Entry());

        EntryExportResult first = await exporter.ExportAsync(
            request,
            CancellationToken.None);
        string path = Path.Combine(
            vault,
            "Lenx",
            Assert.IsType<string>(first.RemoteId));
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(path);
        EntryExportResult second = await exporter.ExportAsync(
            request,
            CancellationToken.None);

        Assert.Equal(first.RemoteId, second.RemoteId);
        Assert.Single(
            Directory.GetFiles(
                Path.Combine(vault, "Lenx"),
                "*.md"));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ExportAsyncDoesNotRenderUnsafeSourceSchemes()
    {
        string vault = CreateDirectory("unsafe-source");
        var store = new MutableTargetStore(
            Target(vault) with
            {
                TemplateMarkdown = "{{source_url}}\n{{content}}",
                IncludeSourceLink = true
            });
        using var exporter = new ObsidianEntryExporter(
            store,
            new MutablePolicyService(),
            assetStore: null);

        EntryExportResult result = await exporter.ExportAsync(
            Request(Entry(normalizedUrl: "javascript:alert(1)")),
            CancellationToken.None);
        string markdown = await File.ReadAllTextAsync(
            Path.Combine(
                vault,
                "Lenx",
                Assert.IsType<string>(result.RemoteId)),
            Encoding.UTF8);

        Assert.DoesNotContain(
            "javascript:",
            markdown,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[阅读原文]", markdown);
    }

    [Fact]
    public async Task ExportAsyncEscapesRawMarkdownEmbedsLinksAndAutolinks()
    {
        string vault = CreateDirectory("markdown-injection");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null);

        EntryExportResult result = await exporter.ExportAsync(
            Request(
                Entry(
                    content:
                        "<p>![](https://attacker.example/pixel)"
                        + " [open](obsidian://open?vault=secret)"
                        + " &lt;https://attacker.example/auto&gt;</p>")),
            CancellationToken.None);
        string markdown = await File.ReadAllTextAsync(
            Path.Combine(
                vault,
                "Lenx",
                Assert.IsType<string>(result.RemoteId)),
            Encoding.UTF8);

        Assert.DoesNotContain(
            "![](https://attacker.example/pixel)",
            markdown,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[open](obsidian://",
            markdown,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "<https://attacker.example/auto>",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains(
            @"\!\[\]\(https\:\/\/attacker\.example\/pixel\)",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains(
            @"obsidian\:\/\/open\?vault\=secret",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsyncAngleDelimitsAllValidatedSourceDestinations()
    {
        const string craftedSource =
            "https://example.com/)obsidian://open?vault=secret";
        string vault = CreateDirectory("source-destination");
        var store = new MutableTargetStore(
            Target(vault) with
            {
                TemplateMarkdown = "来源：{{source_url}}\n\n{{content}}",
                IncludeSourceLink = true
            });
        using var exporter = new ObsidianEntryExporter(
            store,
            new MutablePolicyService(),
            assetStore: null);

        EntryExportResult result = await exporter.ExportAsync(
            Request(
                Entry(
                    normalizedUrl: craftedSource,
                    content:
                        $"<p><a href=\"{craftedSource}\">安全标签</a></p>")),
            CancellationToken.None);
        string markdown = await File.ReadAllTextAsync(
            Path.Combine(
                vault,
                "Lenx",
                Assert.IsType<string>(result.RemoteId)),
            Encoding.UTF8);

        Assert.Contains(
            $"来源：<{craftedSource}>",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains(
            $"[安全标签](<{craftedSource}>)",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains(
            $"[阅读原文](<{craftedSource}>)",
            markdown,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"]({craftedSource})",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsyncRejectsExcessiveHtmlDepthAsContentTooLarge()
    {
        string vault = CreateDirectory("html-depth");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null);
        string content =
            string.Concat(Enumerable.Repeat("<div>", 129))
            + "正文"
            + string.Concat(Enumerable.Repeat("</div>", 129));

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(content: content)),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.ContentTooLarge,
            exception.Error.Code);
        Assert.False(exception.Error.IsRetryable);
        Assert.Empty(
            Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportAsyncRejectsExcessiveHtmlNodeCountAsContentTooLarge()
    {
        string vault = CreateDirectory("html-nodes");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null);
        string content =
            "<p>"
            + string.Concat(
                Enumerable.Repeat("<span>x</span>", 16_385))
            + "</p>";

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(content: content)),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.ContentTooLarge,
            exception.Error.Code);
        Assert.False(exception.Error.IsRetryable);
        Assert.Empty(
            Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportAsyncPreservesInlineAndFencedCodeBoundaries()
    {
        string vault = CreateDirectory("code-fences");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null);
        const string content =
            "<p><code></code>|<code>   </code>|"
            + "<code>``value`</code></p>"
            + "<pre>line\n\n\n</pre>";

        EntryExportResult result = await exporter.ExportAsync(
            Request(Entry(content: content)),
            CancellationToken.None);
        string markdown = await File.ReadAllTextAsync(
            Path.Combine(
                vault,
                "Lenx",
                Assert.IsType<string>(result.RemoteId)),
            Encoding.UTF8);

        Assert.Contains(
            "<code></code>\\|`   `\\|``` ``value` ```",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains(
            "```\nline\n\n\n```\n",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsyncRejectsExpandedMarkdownBeyondFinalByteLimit()
    {
        string vault = CreateDirectory("bounded-output");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null);
        string punctuationHeavyContent =
            $"<p>{new string('*', 6 * 1024 * 1024 + 1)}</p>";

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(
                        Entry(
                            content: punctuationHeavyContent)),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.ContentTooLarge,
            exception.Error.Code);
        Assert.False(exception.Error.IsRetryable);
        Assert.Empty(
            Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportAsyncMapsTransientPolicyFailureToRetryableDestinationError()
    {
        string vault = CreateDirectory("policy-unavailable");
        var policies = new MutablePolicyService
        {
            Failure = new AppException(
                new(
                    AppErrorCode.NetworkUnavailable,
                    "策略不可用",
                    "暂时无法读取策略。",
                    "请稍后重试。",
                    IsRetryable: true))
        };
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            policies,
            assetStore: null);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.DestinationUnavailable,
            exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
        Assert.Empty(
            Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportAsyncMapsTransientTargetReadFailureToRetryableDestinationError()
    {
        string vault = CreateDirectory("target-store-unavailable");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault))
            {
                Failure = new IOException(
                    "simulated settings database failure")
            },
            new MutablePolicyService(),
            assetStore: null);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.DestinationUnavailable,
            exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
        Assert.Empty(
            Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportAsyncMapsDisappearedConfiguredVaultToRetryableDestinationError()
    {
        string disappearedVault = Path.Combine(
            _root,
            "disappeared-vault");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(disappearedVault)),
            new MutablePolicyService(),
            assetStore: null);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.DestinationUnavailable,
            exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
        Assert.False(Directory.Exists(disappearedVault));
    }

    [Fact]
    public Task ExportAsyncMapsContainedDirectoryAccessFailureToAccessDenied() =>
        AssertContainedDirectoryFailureAsync(
            new UnauthorizedAccessException(
                "simulated directory access failure"),
            EntryExportErrorCode.AccessDenied,
            isRetryable: false);

    [Fact]
    public Task ExportAsyncMapsContainedDirectoryIoFailureToRetryableDestinationUnavailable() =>
        AssertContainedDirectoryFailureAsync(
            new IOException(
                "simulated temporary directory I/O failure"),
            EntryExportErrorCode.DestinationUnavailable,
            isRetryable: true);

    [Fact]
    public Task ExportAsyncMapsContainedDirectoryArgumentFailureToInvalidRequest() =>
        AssertContainedDirectoryFailureAsync(
            new ArgumentException(
                "simulated invalid directory argument"),
            EntryExportErrorCode.InvalidRequest,
            isRetryable: false);

    [Fact]
    public async Task ExportAsyncMapsWriteDeniedVaultDirectoryToAccessDenied()
    {
        string vault = CreateDirectory("read-only");
        string destination = Path.Combine(vault, "Lenx");
        Directory.CreateDirectory(destination);
        if (!TrySetWriteDeny(destination, deny: true))
        {
            return;
        }
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null);
        try
        {
            EntryExportException exception =
                await Assert.ThrowsAsync<EntryExportException>(
                    () => exporter.ExportAsync(
                        Request(Entry()),
                        CancellationToken.None));

            Assert.Equal(
                EntryExportErrorCode.AccessDenied,
                exception.Error.Code);
        }
        finally
        {
            Assert.True(TrySetWriteDeny(destination, deny: false));
        }
        Assert.Empty(Directory.GetFiles(destination));
    }

    private async Task AssertContainedDirectoryFailureAsync(
        Exception directoryFailure,
        EntryExportErrorCode expectedCode,
        bool isRetryable)
    {
        string vault = CreateDirectory(
            $"resolve-failure-{expectedCode}");
        using var exporter = new ObsidianEntryExporter(
            new MutableTargetStore(Target(vault)),
            new MutablePolicyService(),
            assetStore: null,
            resolveExportDirectory: (_, _) =>
                throw directoryFailure);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(isRetryable, exception.Error.IsRetryable);
        Assert.Same(directoryFailure, exception.InnerException);
        Assert.Empty(
            Directory.GetFiles(
                _root,
                "*.md",
                SearchOption.AllDirectories));
    }

    private string CreateDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ObsidianExportTarget Target(string vault) =>
        new(
            ObsidianEntryExporter.TargetId,
            vault,
            "Lenx",
            TemplateMarkdown: null,
            Tags: [],
            IncludeSourceLink: true);

    private static EntryExportRequest Request(FeedEntry entry) =>
        EntryExportRequest.Create(
            ObsidianEntryExporter.ExporterId,
            ObsidianEntryExporter.TargetId,
            entry,
            EntryViewKind.Article,
            Encoding.UTF8.GetByteCount(entry.SanitizedContent));

    private static FeedEntry Entry(
        string id = "entry-42",
        string title = "Export item",
        string content = "<p>正文</p>",
        string? contentHash = null,
        string? normalizedUrl = "https://example.com/articles/42") =>
        new(
            id,
            "30000000-0000-4000-8000-000000000001",
            "external-42",
            normalizedUrl,
            title,
            "作者",
            DateTimeOffset.Parse(
                "2026-07-28T12:30:00+00:00",
                CultureInfo.InvariantCulture),
            null,
            "摘要",
            content,
            [],
            [],
            contentHash ?? new string('a', 64),
            DateTimeOffset.Parse(
                "2026-07-29T12:30:00+00:00",
                CultureInfo.InvariantCulture));

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

    private static bool TrySetWriteDeny(
        string path,
        bool deny)
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
        {
            return false;
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = "icacls.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(deny ? "/deny" : "/remove:d");
        startInfo.ArgumentList.Add(
            deny
                ? $"*{sid}:(OI)(CI)(W)"
                : $"*{sid}");
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class MutableTargetStore(
        ObsidianExportTarget? current)
        : IObsidianExportTargetStore
    {
        public ObsidianExportTarget? Current { get; set; } = current;

        public int GetCount { get; private set; }
        public Exception? Failure { get; init; }

        public Task<ObsidianExportTarget?> GetAsync(
            CancellationToken cancellationToken)
        {
            GetCount++;
            if (Failure is not null)
            {
                throw Failure;
            }
            return Task.FromResult(Current);
        }

        public Task SaveAsync(
            ObsidianExportTarget target,
            CancellationToken cancellationToken)
        {
            Current = target;
            return Task.CompletedTask;
        }
    }

    private sealed class MutablePolicyService
        : IEntryIntegrationPolicyService
    {
        public bool Enabled { get; set; } = true;

        public Exception? Failure { get; set; }

        public int GetCount { get; private set; }

        public List<EntryIntegrationPolicyScope> RequestedScopes { get; } =
            [];

        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken)
        {
            GetCount++;
            RequestedScopes.Add(scope);
            if (Failure is not null)
            {
                throw Failure;
            }
            IReadOnlyList<EntryIntegrationPolicy> policies = Enabled
                ? [new(EntryIntegrationKind.Obsidian, true, [])]
                : [];
            return Task.FromResult(
                new EntryIntegrationPolicySnapshot(
                    GetCount,
                    policies,
                    scope));
        }

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
