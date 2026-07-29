using System.Diagnostics;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.Infrastructure.Tests.Exports;

public sealed class ObsidianExportTargetStoreTests
{
    [Fact]
    public void QueueTargetIdIsStableOpaqueAndChangesWithConfiguration()
    {
        ObsidianExportTarget target = ValidTarget();

        string first = target.CreateQueueTargetId();
        string repeated = target.CreateQueueTargetId();
        string changed = (target with
        {
            RelativeDirectory = "Lenx-Changed"
        }).CreateQueueTargetId();

        Assert.Equal(first, repeated);
        Assert.StartsWith("default.", first, StringComparison.Ordinal);
        Assert.Equal(32, first.Length);
        Assert.All(
            first["default.".Length..],
            character => Assert.True(
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f'));
        Assert.NotEqual(first, changed);
        Assert.DoesNotContain(
            target.VaultRootPath,
            first,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            target.RelativeDirectory,
            first,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueueTargetIdUsesWindowsSemanticsForEquivalentPaths()
    {
        string vault = Path.Combine(
            Path.GetTempPath(),
            "Lenx Tools Obsidian scope",
            "Vault");
        ObsidianExportTarget target = ValidTarget() with
        {
            VaultRootPath = vault,
            RelativeDirectory = @"Lenx\Articles"
        };
        ObsidianExportTarget equivalent = target with
        {
            VaultRootPath =
                vault.ToUpperInvariant()
                + Path.DirectorySeparatorChar,
            RelativeDirectory = "lenx/articles"
        };

        Assert.Equal(
            target.CreateQueueTargetId(),
            equivalent.CreateQueueTargetId());
    }

    [Fact]
    public void QueueTargetIdChangesWithDestinationOrRenderedOutput()
    {
        ObsidianExportTarget target = ValidTarget();
        string original = target.CreateQueueTargetId();
        ObsidianExportTarget[] changedTargets =
        [
            target with
            {
                VaultRootPath = Path.Combine(
                    target.VaultRootPath,
                    "different-vault")
            },
            target with
            {
                RelativeDirectory = "Lenx-Changed"
            },
            target with
            {
                TemplateMarkdown = "# {{title}}\n\n{{content}}"
            },
            target with
            {
                Tags = ["RSS"]
            },
            target with
            {
                IncludeSourceLink = !target.IncludeSourceLink
            }
        ];

        Assert.All(
            changedTargets,
            changed => Assert.NotEqual(
                original,
                changed.CreateQueueTargetId()));
    }

    [Fact]
    public async Task SaveAsyncPersistsOneVersionedDocumentAndRoundTripsNormalizedTarget()
    {
        var settings = new RecordingSettingsRepository();
        var store = new AppSettingsObsidianExportTargetStore(settings);
        string vault = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "知识库"));
        Directory.CreateDirectory(vault);
        var target = new ObsidianExportTarget(
            "default",
            vault,
            @"Lenx\收件箱",
            "# {{title}}\n\n{{content}}",
            [" #RSS ", "技术", "rss"],
            IncludeSourceLink: true);

        await store.SaveAsync(target, CancellationToken.None);
        ObsidianExportTarget? restored = await store.GetAsync(
            CancellationToken.None);

        KeyValuePair<string, string> write = Assert.Single(settings.Writes);
        Assert.Equal(
            AppSettingsObsidianExportTargetStore.SettingsKey,
            write.Key);
        Assert.Contains("\"version\":1", write.Value, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal("default", restored.TargetId);
        Assert.Equal(vault, restored.VaultRootPath);
        Assert.Equal(@"Lenx\收件箱", restored.RelativeDirectory);
        Assert.Equal(["RSS", "技术"], restored.Tags);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SaveAsyncCanonicalizesAbsentTemplateToNull(
        string? template)
    {
        var settings = new RecordingSettingsRepository();
        var store = new AppSettingsObsidianExportTargetStore(settings);

        await store.SaveAsync(
            ValidTarget() with
            {
                TemplateMarkdown = template
            },
            CancellationToken.None);
        ObsidianExportTarget? restored = await store.GetAsync(
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Null(restored.TemplateMarkdown);
        Assert.Contains(
            "\"templateMarkdown\":null",
            Assert.Single(settings.Writes).Value,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"version":2,"target":null}""")]
    [InlineData("""{"version":1,"target":null}""")]
    public async Task GetAsyncFailsClosedForMalformedOrUnsupportedDocuments(
        string stored)
    {
        var settings = new RecordingSettingsRepository
        {
            StoredValue = stored
        };
        var store = new AppSettingsObsidianExportTargetStore(settings);

        ObsidianExportTarget? result = await store.GetAsync(
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsyncRejectsUnsafeTemplateTagsAndRelativeDirectory()
    {
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());
        string vault = Path.GetFullPath(Path.GetTempPath());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveAsync(
                new(
                    "default",
                    vault,
                    @"..\outside",
                    "{{unknown}}",
                    ["123", "contains space"],
                    IncludeSourceLink: false),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("CON.txt")]
    [InlineData(@"nested\AUX.md")]
    [InlineData(@"nested\\child")]
    [InlineData(@"folder\..\outside")]
    public async Task SaveAsyncRejectsUnsafeWindowsDirectorySegments(
        string relativeDirectory)
    {
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveAsync(
                ValidTarget() with
                {
                    RelativeDirectory = relativeDirectory
                },
                CancellationToken.None));
    }

    [Theory]
    [InlineData("/rss")]
    [InlineData("rss/")]
    [InlineData("rss//tech")]
    [InlineData("contains space")]
    [InlineData("123")]
    public async Task SaveAsyncRejectsUnsafeObsidianTags(string tag)
    {
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveAsync(
                ValidTarget() with
                {
                    Tags = [tag]
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsyncBoundsTemplateByUtf8Bytes()
    {
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SaveAsync(
                ValidTarget() with
                {
                    TemplateMarkdown = new string('中', 40_000)
                },
                CancellationToken.None));
    }

    [Theory]
    [InlineData("{{content}}\n{{content}}")]
    [InlineData("{{title}} / {{title}}")]
    public async Task SaveAsyncRejectsRepeatedTemplatePlaceholders(
        string template)
    {
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(
                ValidTarget() with
                {
                    TemplateMarkdown = template
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task GetAsyncFailsClosedWhenStoredVaultIsAReparsePoint()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Lenx Tools Obsidian store tests",
            Guid.NewGuid().ToString("N"));
        string actualVault = Path.Combine(root, "actual");
        string linkedVault = Path.Combine(root, "linked");
        Directory.CreateDirectory(actualVault);
        if (!TryCreateDirectoryJunction(linkedVault, actualVault))
        {
            Directory.Delete(root, recursive: true);
            return;
        }
        var settings = new RecordingSettingsRepository
        {
            StoredValue = JsonSerializer.Serialize(
                new
                {
                    version = 1,
                    target = new
                    {
                        targetId = "default",
                        vaultRootPath = linkedVault,
                        relativeDirectory = "Lenx",
                        templateMarkdown = (string?)null,
                        tags = Array.Empty<string>(),
                        includeSourceLink = true
                    }
                })
        };
        var store = new AppSettingsObsidianExportTargetStore(settings);
        try
        {
            ObsidianExportTarget? result = await store.GetAsync(
                CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(linkedVault);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsyncRejectsDeviceNamespaceEvenWhenItPointsToLocalDirectory()
    {
        string localPath = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar);
        string devicePath = $@"\\?\{localPath}";
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());

        ArgumentException exception =
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => store.SaveAsync(
                    ValidTarget() with
                    {
                        VaultRootPath = devicePath
                    },
                    CancellationToken.None));

        Assert.Contains("本地", exception.Message);
    }

    [Fact]
    public async Task SaveAsyncRejectsUncVaultBeforeCheckingRemoteDirectory()
    {
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());

        ArgumentException exception =
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => store.SaveAsync(
                    ValidTarget() with
                    {
                        VaultRootPath = @"\\server\vault"
                    },
                    CancellationToken.None));

        Assert.Contains("UNC", exception.Message);
    }

    [Fact]
    public async Task SaveAsyncRejectsDriveRootAsOverbroadVault()
    {
        string driveRoot = Assert.IsType<string>(
            Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())));
        var store = new AppSettingsObsidianExportTargetStore(
            new RecordingSettingsRepository());

        ArgumentException exception =
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => store.SaveAsync(
                    ValidTarget() with
                    {
                        VaultRootPath = driveRoot
                    },
                    CancellationToken.None));

        Assert.Contains("根目录", exception.Message);
    }

    [Fact]
    public async Task GetAsyncRejectsOversizedDocumentBeforeAcceptingValidTarget()
    {
        var settings = new RecordingSettingsRepository
        {
            StoredValue = JsonSerializer.Serialize(
                new
                {
                    version = 1,
                    target = new
                    {
                        targetId = "default",
                        vaultRootPath =
                            Path.GetFullPath(Path.GetTempPath()),
                        relativeDirectory = "Lenx",
                        templateMarkdown = (string?)null,
                        tags = Array.Empty<string>(),
                        includeSourceLink = true
                    },
                    padding = new string('a', 300_000)
                })
        };
        var store = new AppSettingsObsidianExportTargetStore(settings);

        ObsidianExportTarget? result = await store.GetAsync(
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsyncSurfacesSettingsStorageFailureAsTransientIo()
    {
        var settings = new RecordingSettingsRepository
        {
            ReadException = new InvalidOperationException(
                "simulated database read failure")
        };
        var store = new AppSettingsObsidianExportTargetStore(settings);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => store.GetAsync(CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task SaveAsyncSurfacesSettingsStorageFailureAsTransientIo()
    {
        var settings = new RecordingSettingsRepository
        {
            WriteException = new InvalidOperationException(
                "simulated database write failure")
        };
        var store = new AppSettingsObsidianExportTargetStore(settings);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => store.SaveAsync(
                ValidTarget(),
                CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task GetAsyncSurfacesPreviouslySavedMissingVaultAsTransientIo()
    {
        string missingVault = Path.Combine(
            Path.GetTempPath(),
            "Lenx Tools missing Obsidian vault",
            Guid.NewGuid().ToString("N"));
        var settings = new RecordingSettingsRepository
        {
            StoredValue = JsonSerializer.Serialize(
                new
                {
                    version = 1,
                    target = new
                    {
                        targetId = "default",
                        vaultRootPath = missingVault,
                        relativeDirectory = "Lenx",
                        templateMarkdown = (string?)null,
                        tags = Array.Empty<string>(),
                        includeSourceLink = true
                    }
                })
        };
        var store = new AppSettingsObsidianExportTargetStore(settings);

        await Assert.ThrowsAsync<IOException>(
            () => store.GetAsync(CancellationToken.None));
    }

    private static ObsidianExportTarget ValidTarget() =>
        new(
            "default",
            Path.GetFullPath(Path.GetTempPath()),
            "Lenx",
            TemplateMarkdown: null,
            Tags: [],
            IncludeSourceLink: true);

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

    private sealed class RecordingSettingsRepository
        : IAppSettingsRepository
    {
        public string? StoredValue { get; set; }
        public Exception? ReadException { get; init; }
        public Exception? WriteException { get; init; }

        public List<KeyValuePair<string, string>> Writes { get; } = [];

        public Task<string?> GetAsync(
            string key,
            CancellationToken cancellationToken)
        {
            if (ReadException is not null)
            {
                throw ReadException;
            }
            return Task.FromResult(StoredValue);
        }

        public Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken)
        {
            if (WriteException is not null)
            {
                throw WriteException;
            }
            Writes.Add(new(key, value));
            StoredValue = value;
            return Task.CompletedTask;
        }
    }
}
