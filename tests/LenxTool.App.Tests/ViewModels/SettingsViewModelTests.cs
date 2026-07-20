using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Updates;

namespace LenxTool.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task InitializeRestoresAppearanceAndSavePersistsChanges()
    {
        var theme = new FakeThemeService();
        var settings = new FakeSettingsRepository
        {
            Values =
            {
                ["appearance.dark_mode"] = "True",
                ["appearance.reduce_motion"] = "True"
            }
        };
        var viewModel = new SettingsViewModel(
            theme, new FakeUpdateService(), new FakeSecretStore(), settings);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.IsDarkMode);
        Assert.True(viewModel.ReduceMotion);
        Assert.True(theme.DarkMode);
        Assert.True(theme.ReduceMotion);

        viewModel.IsDarkMode = false;
        viewModel.ReduceMotion = false;
        await viewModel.SaveAppearanceCommand.ExecuteAsync();

        Assert.Equal("False", settings.Values["appearance.dark_mode"]);
        Assert.Equal("False", settings.Values["appearance.reduce_motion"]);
        Assert.Contains("已保存", viewModel.AppearanceStatus, StringComparison.Ordinal);
    }

    private sealed class FakeThemeService : IThemeService
    {
        public bool DarkMode { get; private set; }
        public bool ReduceMotion { get; private set; }
        public void ApplyTheme(bool useDarkTheme) => DarkMode = useDarkTheme;
        public void ApplyReduceMotion(bool reduceMotion) => ReduceMotion = reduceMotion;
    }

    private sealed class FakeSettingsRepository : IAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SetAsync(string name, string value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult<UpdateCandidate?>(null);
        public Task<string> DownloadAsync(UpdateCandidate candidate, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void LaunchInstallerAndExit(string installerPath) => throw new NotSupportedException();
    }
}
