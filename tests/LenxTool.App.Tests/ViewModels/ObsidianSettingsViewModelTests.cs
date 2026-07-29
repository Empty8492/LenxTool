using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.Tests.ViewModels;

public sealed class ObsidianSettingsViewModelTests
{
    [Fact]
    public async Task InitializeRestoresTheDefaultTarget()
    {
        var store = new FakeObsidianExportTargetStore
        {
            Current = new(
                "default",
                @"C:\笔记库",
                "Lenx/稍后阅读",
                "# {{title}}\n\n{{content}}",
                ["lenx", "稍后阅读"],
                true)
        };
        var viewModel = new ObsidianSettingsViewModel(
            store,
            new FakeDesktopFileDialogService());

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(@"C:\笔记库", viewModel.VaultRootPath);
        Assert.Equal("Lenx/稍后阅读", viewModel.RelativeDirectory);
        Assert.Equal("lenx, 稍后阅读", viewModel.TagsText);
        Assert.Equal("# {{title}}\n\n{{content}}", viewModel.TemplateMarkdown);
        Assert.True(viewModel.IncludeSourceLink);
        Assert.Contains("已加载", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Obsidian 导出设置暂时无法读取。")]
    [InlineData(1, "当前用户无权读取该 Vault 设置。")]
    public async Task InitializeMapsTargetReadFailuresToClosedStatus(
        int failureKind,
        string expectedStatus)
    {
        var viewModel = new ObsidianSettingsViewModel(
            new FakeObsidianExportTargetStore
            {
                GetException = failureKind == 0
                    ? new IOException("private database details")
                    : new UnauthorizedAccessException("private path")
            },
            new FakeDesktopFileDialogService());

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(expectedStatus, viewModel.Status);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void PickingVaultFolderOnlyFillsTheInput()
    {
        var store = new FakeObsidianExportTargetStore();
        var dialog = new FakeDesktopFileDialogService
        {
            Folder = @"D:\知识库"
        };
        var viewModel = new ObsidianSettingsViewModel(store, dialog);

        viewModel.PickVaultFolderCommand.Execute(null);

        Assert.Equal(@"D:\知识库", viewModel.VaultRootPath);
        Assert.Equal(1, dialog.PickFolderCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task SavePersistsNormalizedDefaultTarget()
    {
        var store = new FakeObsidianExportTargetStore();
        var viewModel = new ObsidianSettingsViewModel(
            store,
            new FakeDesktopFileDialogService())
        {
            VaultRootPath = @" C:\笔记库 ",
            RelativeDirectory = @" Lenx\稍后阅读 ",
            TagsText = " lenx, 稍后阅读\nfeed ",
            TemplateMarkdown = "# {{title}}\n\n{{content}}",
            IncludeSourceLink = false
        };

        await viewModel.SaveCommand.ExecuteAsync();

        ObsidianExportTarget saved = Assert.IsType<ObsidianExportTarget>(
            store.Saved);
        Assert.Equal("default", saved.TargetId);
        Assert.Equal(@"C:\笔记库", saved.VaultRootPath);
        Assert.Equal(@"Lenx\稍后阅读", saved.RelativeDirectory);
        Assert.Equal(["lenx", "稍后阅读", "feed"], saved.Tags);
        Assert.Equal("# {{title}}\n\n{{content}}", saved.TemplateMarkdown);
        Assert.False(saved.IncludeSourceLink);
        Assert.Equal("设置已保存，后续导出立即使用新配置。", viewModel.Status);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SavePreservesWhitespaceInNonBlankTemplate()
    {
        var store = new FakeObsidianExportTargetStore();
        var viewModel = new ObsidianSettingsViewModel(
            store,
            new FakeDesktopFileDialogService())
        {
            VaultRootPath = @"C:\笔记库",
            TemplateMarkdown = "  {{content}}\n"
        };

        await viewModel.SaveCommand.ExecuteAsync();

        ObsidianExportTarget saved = Assert.IsType<ObsidianExportTarget>(
            store.Saved);
        Assert.Equal("  {{content}}\n", saved.TemplateMarkdown);
    }

    [Theory]
    [InlineData("Vault 根目录必须是绝对路径。")]
    [InlineData("标签包含不支持的空格。")]
    [InlineData("模板超过允许长度。")]
    public async Task SaveDisplaysTargetValidationErrors(string error)
    {
        var store = new FakeObsidianExportTargetStore
        {
            SaveException = new ArgumentException(error)
        };
        var viewModel = new ObsidianSettingsViewModel(
            store,
            new FakeDesktopFileDialogService())
        {
            VaultRootPath = @"C:\笔记库"
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(error, viewModel.Status);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData(0, "Vault 路径包含不安全的重解析点。")]
    [InlineData(1, "当前用户无权访问该 Vault。")]
    [InlineData(2, "保存 Obsidian 设置时无法访问本地存储。")]
    public async Task SaveMapsFilesystemFailuresToClosedMessages(
        int failureKind,
        string expectedStatus)
    {
        Exception failure = failureKind switch
        {
            0 => new InvalidOperationException("junction details"),
            1 => new UnauthorizedAccessException("private path"),
            _ => new IOException("filesystem details")
        };
        var viewModel = new ObsidianSettingsViewModel(
            new FakeObsidianExportTargetStore
            {
                SaveException = failure
            },
            new FakeDesktopFileDialogService())
        {
            VaultRootPath = @"C:\笔记库"
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(expectedStatus, viewModel.Status);
        Assert.DoesNotContain(
            failure.Message,
            viewModel.Status,
            StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    private sealed class FakeObsidianExportTargetStore
        : IObsidianExportTargetStore
    {
        public ObsidianExportTarget? Current { get; init; }
        public ObsidianExportTarget? Saved { get; private set; }
        public Exception? GetException { get; init; }
        public Exception? SaveException { get; init; }
        public int SaveCalls { get; private set; }

        public Task<ObsidianExportTarget?> GetAsync(
            CancellationToken cancellationToken)
        {
            if (GetException is not null)
            {
                throw GetException;
            }
            return Task.FromResult(Current);
        }

        public Task SaveAsync(
            ObsidianExportTarget target,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            if (SaveException is not null)
            {
                throw SaveException;
            }

            Saved = target;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDesktopFileDialogService
        : IDesktopFileDialogService
    {
        public string? Folder { get; init; }
        public int PickFolderCalls { get; private set; }

        public string? PickFolder()
        {
            PickFolderCalls++;
            return Folder;
        }

        public IReadOnlyList<string> PickMediaFiles() => [];
        public string? PickWhisperModel() => null;
        public string? PickDatabaseBackup() => null;
        public string? PickFileForHash() => null;
        public (string Source, string Destination)? PickWordConversion() =>
            null;
        public void OpenFolder(string path)
        {
        }

        public void OpenUri(string uri)
        {
        }
    }
}
