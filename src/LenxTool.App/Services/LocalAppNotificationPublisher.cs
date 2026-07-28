using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class LocalAppNotificationPublisher(
    IAppNotificationRepository repository,
    IAppNotificationInbox inbox,
    TimeProvider timeProvider) : IAppNotificationPublisher
{
    private static readonly string LocalRuleId =
        Guid.Empty.ToString("D");

    public async Task<AppNotificationRegistration> PublishAsync(
        AppNotificationDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Enum.IsDefined(draft.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(draft));
        }

        string dedupeKey = RequireText(
            draft.DedupeKey,
            1_024,
            nameof(draft.DedupeKey));
        string? ruleId = draft.RuleId;
        if (ruleId is not null &&
            !Guid.TryParseExact(ruleId, "D", out _))
        {
            throw new ArgumentException(
                "通知规则 ID 必须是规范 GUID。",
                nameof(draft));
        }

        int ruleVersion = draft.RuleVersion ?? 1;
        if (ruleVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(draft));
        }

        var notification = new AppNotification(
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"{draft.Kind}\n{dedupeKey}")))
                .ToLowerInvariant(),
            RequireText(draft.EntryId, 512, nameof(draft.EntryId)),
            RequireText(draft.FeedId, 512, nameof(draft.FeedId)),
            ruleId ?? LocalRuleId,
            ruleVersion,
            NormalizeLabel(draft.Title, 1_024, "本地通知"),
            NormalizeLabel(draft.SourceLabel, 160, "Lenx Tools"),
            timeProvider.GetUtcNow(),
            ReadAt: null,
            draft.Kind);
        AppNotificationRegistration registration =
            await repository.RegisterAsync(
                notification,
                cancellationToken).ConfigureAwait(false);
        if (registration.Created)
        {
            inbox.Publish(registration.Notification);
        }
        return registration;
    }

    private static string RequireText(
        string value,
        int maximumLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            value,
            parameterName);
        if (value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return value;
    }

    private static string NormalizeLabel(
        string? value,
        int maximumLength,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var result = new StringBuilder(
            Math.Min(value.Length, maximumLength));
        bool needsSpace = false;
        foreach (char character in value.Normalize(
                     NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character) ||
                char.IsControl(character))
            {
                needsSpace = result.Length > 0;
                continue;
            }
            if (needsSpace && result.Length < maximumLength)
            {
                result.Append(' ');
            }
            needsSpace = false;
            if (result.Length >= maximumLength)
            {
                break;
            }
            result.Append(character);
        }
        if (result.Length > 0 && char.IsHighSurrogate(result[^1]))
        {
            result.Length--;
        }
        return result.Length == 0
            ? fallback
            : result.ToString();
    }
}
