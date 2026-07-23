namespace LenxTool.Infrastructure.SystemServices;

public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string DataDirectory => Path.Combine(RootDirectory, "Data");
    public string AssetCacheDirectory => Path.Combine(DataDirectory, "Assets");
    public string DatabasePath => Path.Combine(DataDirectory, "lenx.db");
    public string BackupDirectory => Path.Combine(DataDirectory, "Backups");
    public string SecretsDirectory => Path.Combine(RootDirectory, "Secrets");
    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");
    public string ModelsDirectory => Path.Combine(RootDirectory, "Models");
    public string TempDirectory => Path.Combine(RootDirectory, "Temp");
    public string UpdatesDirectory => Path.Combine(RootDirectory, "Updates");
    public string OutputDirectory => Path.Combine(RootDirectory, "Output");

    public static AppPaths CreateDefault()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new(Path.Combine(localAppData, "LenxTool"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(AssetCacheDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
        Directory.CreateDirectory(OutputDirectory);
    }
}
