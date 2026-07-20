namespace LenxTool.Core.Contracts;

public interface IAppSettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}
