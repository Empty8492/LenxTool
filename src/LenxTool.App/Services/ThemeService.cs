using System.Windows;

namespace LenxTool.App.Services;

public interface IThemeService
{
    void ApplyTheme(bool useDarkTheme);

    void ApplyReduceMotion(bool reduceMotion);
}

public sealed class ThemeService : IThemeService
{
    private static readonly Uri LightTheme = new("/LenxTool;component/Themes/Colors.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkTheme = new("/LenxTool;component/Themes/Colors.Dark.xaml", UriKind.Relative);

    public void ApplyTheme(bool useDarkTheme)
    {
        ResourceDictionary resources = Application.Current.Resources;
        ResourceDictionary? current = resources.MergedDictionaries.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString.Contains("Colors.", StringComparison.Ordinal) == true);
        var replacement = new ResourceDictionary { Source = useDarkTheme ? DarkTheme : LightTheme };

        if (current is null)
        {
            resources.MergedDictionaries.Insert(0, replacement);
            return;
        }

        int index = resources.MergedDictionaries.IndexOf(current);
        resources.MergedDictionaries[index] = replacement;
    }

    public void ApplyReduceMotion(bool reduceMotion)
    {
        Application.Current.Resources["LenxTool.ReduceMotion"] = reduceMotion;
        Application.Current.Resources["LenxTool.MotionDuration"] =
            new Duration(reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(160));
    }
}
