namespace LenxTool.Core.Accounts;

public enum AccountRole
{
    User,
    Admin
}

public enum AccountSessionStatus
{
    SignedOut,
    SignedIn,
    Expired
}

public sealed record AccountUser(string Id, string Username, AccountRole Role);

public sealed record AccountQuotaCounter(int Limit, int Used, int Reserved, int Remaining);

public sealed record AccountQuota(
    DateOnly Date,
    AccountQuotaCounter Ai,
    AccountQuotaCounter SpeechSeconds);

public sealed record AccountSessionSnapshot(
    AccountSessionStatus Status,
    AccountUser? User = null,
    AccountQuota? Quota = null)
{
    public static AccountSessionSnapshot SignedOut { get; } = new(AccountSessionStatus.SignedOut);
    public static AccountSessionSnapshot Expired { get; } = new(AccountSessionStatus.Expired);

    public bool IsAuthenticated => Status == AccountSessionStatus.SignedIn && User is not null;
    public bool IsAdmin => IsAuthenticated && User!.Role == AccountRole.Admin;
}

public sealed class AccountSessionChangedEventArgs(AccountSessionSnapshot session) : EventArgs
{
    public AccountSessionSnapshot Session { get; } = session;
}
