using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface INewsCenterService
{
    Task<NewsCenterSnapshot> RefreshAsync(CancellationToken cancellationToken);

    Task<NewsCenterSnapshot> LoadCachedAsync(CancellationToken cancellationToken);
}
