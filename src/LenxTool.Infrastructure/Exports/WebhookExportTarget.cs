namespace LenxTool.Infrastructure.Exports;

public sealed record WebhookExportTarget(
    string TargetId,
    Uri Endpoint,
    bool UseHmac,
    int CredentialVersion = 0)
{
    public const string DefaultTargetId = "default";
    public const string SettingsKey = "integration.webhook.target.v1";

    public string CreateQueueTargetId()
    {
        WebhookExportTarget normalized = Normalize(this);
        return IntegrationExportTargetIdentity.Create(
            normalized.TargetId,
            normalized.Endpoint.AbsoluteUri,
            normalized.UseHmac ? "hmac-sha256" : "unsigned");
    }

    internal bool MatchesQueueTargetId(string? value) =>
        string.Equals(CreateQueueTargetId(), value, StringComparison.Ordinal);

    internal static bool IsSupportedQueueTargetId(string? value) =>
        IntegrationExportTargetIdentity.IsSupported(value, DefaultTargetId);

    public static WebhookExportTarget Normalize(WebhookExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(target.TargetId, DefaultTargetId, StringComparison.Ordinal)
            || target.CredentialVersion is not (0 or 1))
        {
            throw new ArgumentException("Webhook 目标标识无效。", nameof(target));
        }
        return target with
        {
            Endpoint = IntegrationTargetEndpointValidator.NormalizeHttpsEndpoint(
                target.Endpoint)
        };
    }
}
