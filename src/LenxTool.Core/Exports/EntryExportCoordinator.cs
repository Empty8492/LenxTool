using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Core.Exports;

public sealed class EntryExportCoordinator
    : IEntryExportCoordinator
{
    private const int MaximumIdentifierLength = 128;
    private const int MaximumDisplayNameLength = 120;
    private const int MaximumRemoteIdLength = 512;
    private readonly Dictionary<string, ExporterRegistration>
        _exporters;

    public static TimeSpan MaximumRetryAfter { get; } =
        TimeSpan.FromDays(7);

    public EntryExportCoordinator(
        IEnumerable<IEntryExporter> exporters)
    {
        ArgumentNullException.ThrowIfNull(exporters);
        var registrations =
            new Dictionary<string, ExporterRegistration>(
                StringComparer.Ordinal);
        foreach (IEntryExporter? exporter in exporters)
        {
            if (exporter is null)
            {
                throw new ArgumentException(
                    "Exporter collections cannot contain null.",
                    nameof(exporters));
            }
            EntryExportCapability capability =
                NormalizeCapability(exporter.Capability);
            if (!registrations.TryAdd(
                    capability.ExporterId,
                    new(exporter, capability)))
            {
                throw new ArgumentException(
                    "Exporter identifiers must be unique.",
                    nameof(exporters));
            }
        }
        _exporters = registrations;
        Capabilities = Array.AsReadOnly(
            registrations.Values
                .Select(value => value.Capability)
                .OrderBy(
                    value => value.ExporterId,
                    StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<EntryExportCapability> Capabilities
    {
        get;
    }

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        if (!_exporters.TryGetValue(
                request.ExporterId,
                out ExporterRegistration? registration))
        {
            return Failure(
                request,
                EntryExportErrorCode.ExporterNotFound);
        }
        EntryExportCapability capability =
            registration.Capability;
        if (!capability.SupportedViewKinds.Contains(
                request.ViewKind))
        {
            return Failure(
                request,
                EntryExportErrorCode.UnsupportedContent);
        }
        if (capability.MaximumContentBytes is { } maximum
            && request.ContentBytes > maximum)
        {
            return Failure(
                request,
                EntryExportErrorCode.ContentTooLarge);
        }

        EntryExportResult result;
        try
        {
            result = await registration.Exporter.ExportAsync(
                request,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntryExportException exception)
        {
            return EntryExportResult.Failure(
                request.IdempotencyKey,
                NormalizeError(exception.Error));
        }
        catch
        {
            return Failure(
                request,
                EntryExportErrorCode.Unknown);
        }
        return ValidateResult(request, result);
    }

    private static EntryExportCapability NormalizeCapability(
        EntryExportCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ValidateExporterId(capability.ExporterId);
        string displayName = ValidateBoundedText(
            capability.DisplayName,
            MaximumDisplayNameLength,
            nameof(capability.DisplayName));
        ArgumentNullException.ThrowIfNull(
            capability.SupportedViewKinds);
        EntryViewKind[] supported =
            capability.SupportedViewKinds
                .OrderBy(value => value)
                .ToArray();
        if (supported.Length == 0
            || supported.Any(value => !Enum.IsDefined(value))
            || supported.Distinct().Count() != supported.Length)
        {
            throw new ArgumentException(
                "Supported view kinds must be non-empty, valid, and unique.",
                nameof(capability));
        }
        if (capability.MaximumContentBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capability));
        }
        return capability with
        {
            DisplayName = displayName,
            SupportedViewKinds =
                Array.AsReadOnly(supported)
        };
    }

    private static void ValidateRequest(
        EntryExportRequest request)
    {
        ValidateExporterId(request.ExporterId);
        ValidateCanonicalText(
            request.TargetId,
            MaximumIdentifierLength,
            nameof(request.TargetId));
        ArgumentNullException.ThrowIfNull(request.Entry);
        ValidateCanonicalText(
            request.Entry.Id,
            MaximumIdentifierLength,
            nameof(request.Entry.Id));
        if (!IsLowerHex(request.Entry.ContentHash, 64))
        {
            throw new ArgumentException(
                "Entry content hashes must be 64 lowercase hexadecimal characters.",
                nameof(request));
        }
        if (!Enum.IsDefined(request.ViewKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(
            request.ContentBytes);
        if (!IsLowerHex(request.IdempotencyKey, 64))
        {
            throw new ArgumentException(
                "Idempotency keys must be 64 lowercase hexadecimal characters.",
                nameof(request));
        }
        EntryExportRequest expected =
            EntryExportRequest.Create(
                request.ExporterId,
                request.TargetId,
                request.Entry,
                request.ViewKind,
                request.ContentBytes);
        if (!string.Equals(
                expected.IdempotencyKey,
                request.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Idempotency keys must match the canonical request scope.",
                nameof(request));
        }
    }

    private static EntryExportResult ValidateResult(
        EntryExportRequest request,
        EntryExportResult? result)
    {
        if (result is null
            || !string.Equals(
                result.IdempotencyKey,
                request.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exporter returned a result for another request.");
        }
        if (result.Succeeded)
        {
            if (result.Error is not null
                || (string.IsNullOrWhiteSpace(result.RemoteId)
                    && result.RemoteUrl is null))
            {
                throw new InvalidOperationException(
                    "Successful exports require a remote reference and no error.");
            }
            if (result.RemoteId is { } remoteId)
            {
                ValidateCanonicalText(
                    remoteId,
                    MaximumRemoteIdLength,
                    nameof(result.RemoteId));
            }
            if (result.RemoteUrl is { } remoteUrl
                && (!remoteUrl.IsAbsoluteUri
                    || remoteUrl.Scheme is not ("http" or "https")
                    || !string.IsNullOrEmpty(remoteUrl.UserInfo)))
            {
                throw new InvalidOperationException(
                    "Exporter URLs must be safe absolute HTTP(S) URLs.");
            }
            return result;
        }
        if (result.Error is null
            || result.RemoteId is not null
            || result.RemoteUrl is not null)
        {
            throw new InvalidOperationException(
                "Failed exports require one structured error and no remote reference.");
        }
        return result with
        {
            Error = NormalizeError(result.Error)
        };
    }

    private static EntryExportError NormalizeError(
        EntryExportError error)
    {
        if (!Enum.IsDefined(error.Code))
        {
            return new(
                EntryExportErrorCode.Unknown,
                IsRetryable: false);
        }
        TimeSpan? retryAfter = error.IsRetryable
            ? BoundRetryAfter(error.RetryAfter)
            : null;
        return error with
        {
            RetryAfter = retryAfter
        };
    }

    private static TimeSpan? BoundRetryAfter(
        TimeSpan? retryAfter)
    {
        if (retryAfter is null)
        {
            return null;
        }
        if (retryAfter < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return retryAfter > MaximumRetryAfter
            ? MaximumRetryAfter
            : retryAfter;
    }

    private static EntryExportResult Failure(
        EntryExportRequest request,
        EntryExportErrorCode code) =>
        EntryExportResult.Failure(
            request.IdempotencyKey,
            new(
                code,
                IsRetryable: false));

    private static void ValidateExporterId(string value)
    {
        ValidateBoundedText(
            value,
            64,
            nameof(value));
        if (!char.IsAsciiLetterOrDigit(value[0])
            || value[0] is >= 'A' and <= 'Z'
            || value.Any(character =>
                !(character is >= 'a' and <= 'z'
                  || character is >= '0' and <= '9'
                  || character is '-' or '.')))
        {
            throw new ArgumentException(
                "Exporter identifiers must use lowercase ASCII letters, digits, dots, or hyphens.",
                nameof(value));
        }
    }

    private static string ValidateBoundedText(
        string value,
        int maximumLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
        return normalized;
    }

    private static void ValidateCanonicalText(
        string value,
        int maximumLength,
        string parameterName)
    {
        string normalized = ValidateBoundedText(
            value,
            maximumLength,
            parameterName);
        if (!string.Equals(
                normalized,
                value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Identifiers cannot contain surrounding whitespace.",
                parameterName);
        }
    }

    private static bool IsLowerHex(
        string? value,
        int length) =>
        value?.Length == length
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private sealed record ExporterRegistration(
        IEntryExporter Exporter,
        EntryExportCapability Capability);
}
