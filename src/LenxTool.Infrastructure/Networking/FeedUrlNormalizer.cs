using System.Globalization;

namespace LenxTool.Infrastructure.Networking;

internal static class FeedUrlNormalizer
{
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "mc_cid", "mc_eid", "_ga"
    };

    private static readonly HashSet<string> IdentityParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "auth", "authorization", "signature", "sig", "key",
        "api_key", "apikey", "credential", "policy", "expires", "session", "sessionid",
        "code", "identity"
    };

    public static string? Normalize(string? value, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value.Any(char.IsControl)
            || !Uri.TryCreate(baseUri, value.Trim(), out Uri? resolved)
            || resolved.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(resolved.IdnHost)
            || !string.IsNullOrEmpty(resolved.UserInfo))
        {
            return null;
        }

        var builder = new UriBuilder(resolved)
        {
            Scheme = resolved.Scheme.ToLowerInvariant(),
            Host = new IdnMapping().GetAscii(resolved.IdnHost).ToLowerInvariant(),
            Fragment = string.Empty
        };
        if ((builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443)
            || (builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80))
        {
            builder.Port = -1;
        }

        string query = resolved.Query.Length > 1 ? resolved.Query[1..] : string.Empty;
        if (query.Length > 0 && !ContainsIdentityParameter(query))
        {
            query = string.Join(
                '&',
                query.Split('&', StringSplitOptions.None)
                    .Where(segment => !IsTrackingParameter(ReadParameterName(segment))));
        }
        builder.Query = query;
        string normalized = builder.Uri.AbsoluteUri;
        return normalized.Length <= 2048 ? normalized : null;
    }

    private static bool ContainsIdentityParameter(string query) =>
        query.Split('&', StringSplitOptions.None)
            .Select(ReadParameterName)
            .Any(name => IdentityParameters.Contains(name)
                || name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("x-goog-", StringComparison.OrdinalIgnoreCase));

    private static bool IsTrackingParameter(string name) =>
        TrackingParameters.Contains(name)
        || name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase);

    private static string ReadParameterName(string segment)
    {
        int separator = segment.IndexOf('=');
        string encoded = separator < 0 ? segment : segment[..separator];
        try
        {
            return Uri.UnescapeDataString(encoded.Replace("+", " ", StringComparison.Ordinal));
        }
        catch (UriFormatException)
        {
            return encoded;
        }
    }
}
