using LenxTool.App.Mvvm;
using LenxTool.App.ViewModels;

namespace LenxTool.App.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public void NavigateChangesCurrentPageAndClosesCommandPalette()
    {
        TestPageViewModel home = new("首页");
        TestPageViewModel news = new("资讯中心");
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, home),
            new("news", "资讯中心", "早报与热点", IconData, news)
        ]);
        shell.IsCommandPaletteOpen = true;

        shell.NavigateCommand.Execute("news");

        Assert.Same(news, shell.CurrentPage);
        Assert.Equal("news", shell.SelectedPageId);
        Assert.False(shell.IsCommandPaletteOpen);
    }

    [Fact]
    public void CommandQueryFiltersByLabelAndDescription()
    {
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, new TestPageViewModel("首页")),
            new("media", "媒体工作台", "字幕与音频", IconData, new TestPageViewModel("媒体")),
            new("settings", "设置", "主题与密钥", IconData, new TestPageViewModel("设置"))
        ]);

        shell.CommandQuery = "字幕";

        PageNavigationItem item = Assert.Single(shell.FilteredCommands);
        Assert.Equal("media", item.Id);
    }

    private const string IconData = "M0,0 L1,0 1,1 0,1 Z";

    private sealed class TestPageViewModel(string title) : PageViewModel(title, string.Empty);
}
