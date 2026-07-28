using System.Security.Cryptography;
using System.Text;

namespace LenxTool.Core.Models;

public sealed record EntryExportCapability(
    string ExporterId,
    string DisplayName,
    IReadOnlyList<EntryViewKind> SupportedViewKinds,
    bool RequiresCredentials,
    long? MaximumContentBytes,
    bool IsIdempotent);

public sealed record EntryExportRequest(
    string IdempotencyKey,
    string ExporterId,
    string TargetId,
    FeedEntry Entry,
    EntryViewKind ViewKind,
    long ContentBytes)
{
    public static EntryExportRequest Create(
        string exporterId,
        string targetId,
        FeedEntry entry,
        EntryViewKind viewKind,
        long contentBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exporterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(entry);
        if (!Enum.IsDefined(viewKind))
        {
            throw new ArgumentOutOfRangeException(nameof(viewKind));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(contentBytes);
        string idempotencyKey = CreateIdempotencyKey(
            exporterId,
            targetId,
            entry.Id,
            entry.ContentHash,
            viewKind);
        return new(
            idempotencyKey,
            exporterId,
            targetId,
            entry,
            viewKind,
            contentBytes);
    }

    private static string CreateIdempotencyKey(
        string exporterId,
        string targetId,
        string entryId,
        string contentHash,
        EntryViewKind viewKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        var canonical = new StringBuilder();
        Append(canonical, exporterId);
        Append(canonical, targetId);
        Append(canonical, entryId);
        Append(canonical, contentHash);
        Append(
            canonical,
            ((int)viewKind).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(
        StringBuilder target,
        string value)
    {
        target.Append(value.Length);
        target.Append(':');
        target.Append(value);
    }
}

public enum EntryExportErrorCode
{
    InvalidRequest,
    ExporterNotFound,
    UnsupportedContent,
    CredentialsRequired,
    ContentTooLarge,
    RateLimited,
    DestinationUnavailable,
    AccessDenied,
    Conflict,
    ProviderRejected,
    Unknown
}

public sealed record EntryExportError(
    EntryExportErrorCode Code,
    bool IsRetryable,
    TimeSpan? RetryAfter = null);

public sealed record EntryExportResult(
    string IdempotencyKey,
    bool Succeeded,
    string? RemoteId,
    Uri? RemoteUrl,
    EntryExportError? Error)
{
    public static EntryExportResult Success(
        string idempotencyKey,
        string? remoteId,
        Uri? remoteUrl) =>
        new(
            idempotencyKey,
            Succeeded: true,
            remoteId,
            remoteUrl,
            Error: null);

    public static EntryExportResult Failure(
        string idempotencyKey,
        EntryExportError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(
            idempotencyKey,
            Succeeded: false,
            RemoteId: null,
            RemoteUrl: null,
            error);
    }
}
