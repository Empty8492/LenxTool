namespace LenxTool.Infrastructure.Exports;

public sealed record ReadeckExportTarget(
    string TargetId,
    Uri Endpoint,
    bool Archive,
    int CredentialVersion = 0)
{
    public const string DefaultTargetId = "default";
    public const string SettingsKey = "integration.readeck.target.v1";

    public string CreateQueueTargetId()
    {
        ReadeckExportTarget normalized = Normalize(this);
        return IntegrationExportTargetIdentity.Create(
            normalized.TargetId,
            normalized.Endpoint.AbsoluteUri,
            normalized.Archive ? "archive" : "inbox");
    }

    internal bool MatchesQueueTargetId(string? value) =>
        string.Equals(CreateQueueTargetId(), value, StringComparison.Ordinal);

    internal static bool IsSupportedQueueTargetId(string? value) =>
        IntegrationExportTargetIdentity.IsSupported(value, DefaultTargetId);

    public static ReadeckExportTarget Normalize(ReadeckExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(
                target.TargetId,
                DefaultTargetId,
                StringComparison.Ordinal)
            || target.CredentialVersion is not (0 or 1))
        {
            throw new ArgumentException("Readeck 目标标识无效。", nameof(target));
        }
        return target with
        {
            Endpoint = IntegrationTargetEndpointValidator.NormalizeHttps(
                target.Endpoint)
        };
    }
}
