using LenxTool.Core.Accounts;

namespace LenxTool.Core.Contracts;

public interface IAccountSessionService
{
    bool IsConfigured { get; }
    AccountSessionSnapshot Current { get; }
    event EventHandler<AccountSessionChangedEventArgs>? SessionChanged;

    Task InitializeAsync(CancellationToken cancellationToken);
    Task LoginAsync(string username, string password, CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
}
