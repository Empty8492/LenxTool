namespace LenxTool.Core.Contracts;

public interface IFavoriteRepository
{
    Task<int> GetCountAsync(CancellationToken cancellationToken);
}
