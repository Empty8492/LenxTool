using System.Text;
using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class FeedDiscoveryQueryClassifier
{
    public const int MaximumInputCodePoints = 2048;

    public static FeedDiscoveryQuery Classify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Invalid(FeedDiscoveryQueryError.Empty);
        }
        if (input.Any(char.IsControl))
        {
            return Invalid(FeedDiscoveryQueryError.ControlCharacter);
        }
        if (input.EnumerateRunes().Count() > MaximumInputCodePoints)
        {
            return Invalid(FeedDiscoveryQueryError.TooLong);
        }

        string value = input.Trim();
        if (!TryReadScheme(value, out string? scheme))
        {
            return new(
                NormalizeKeyword(value),
                FeedDiscoveryQueryKind.Keyword,
                FeedDiscoveryQueryError.None);
        }

        if (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyUrl(value);
        }
        if (string.Equals(scheme, "rsshub", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyRssHubRoute(value);
        }

        return Invalid(FeedDiscoveryQueryError.UnsupportedScheme);
    }

    private static FeedDiscoveryQuery ClassifyUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return Invalid(FeedDiscoveryQueryError.InvalidUrl);
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid(FeedDiscoveryQueryError.CredentialsNotAllowed);
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return Invalid(FeedDiscoveryQueryError.FragmentNotAllowed);
        }
        if (!FeedDiscoveryUrlNormalizer.TryNormalizeHttpUrl(uri, out string normalized))
        {
            return Invalid(FeedDiscoveryQueryError.InvalidUrl);
        }

        return new(
            normalized,
            FeedDiscoveryQueryKind.Url,
            FeedDiscoveryQueryError.None);
    }

    private static FeedDiscoveryQuery ClassifyRssHubRoute(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "rsshub", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return Invalid(FeedDiscoveryQueryError.InvalidRssHubRoute);
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid(FeedDiscoveryQueryError.CredentialsNotAllowed);
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return Invalid(FeedDiscoveryQueryError.FragmentNotAllowed);
        }

        var builder = new UriBuilder(uri)
        {
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty
        };
        string normalized = builder.Uri.AbsoluteUri;
        if (normalized.Length > MaximumInputCodePoints)
        {
            return Invalid(FeedDiscoveryQueryError.TooLong);
        }

        return new(
            normalized,
            FeedDiscoveryQueryKind.RssHubRoute,
            FeedDiscoveryQueryError.None);
    }

    private static string NormalizeKeyword(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(normalized.Length);
        bool pendingSpace = false;
        foreach (char character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static bool TryReadScheme(string value, out string? scheme)
    {
        scheme = null;
        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }
        for (int index = 1; index < separator; index++)
        {
            char character = value[index];
            if (!char.IsAsciiLetterOrDigit(character)
                && character != '+'
                && character != '-'
                && character != '.')
            {
                return false;
            }
        }

        scheme = value[..separator];
        return true;
    }

    private static FeedDiscoveryQuery Invalid(FeedDiscoveryQueryError error) =>
        new(null, FeedDiscoveryQueryKind.Invalid, error);
}

internal static class FeedDiscoveryUrlNormalizer
{
    public static bool TryNormalizeHttpUrl(Uri uri, out string normalized)
    {
        normalized = string.Empty;
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty
        };
        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }
        string value = builder.Uri.AbsoluteUri;
        if (value.Length > FeedDiscoveryQueryClassifier.MaximumInputCodePoints)
        {
            return false;
        }

        normalized = value;
        return true;
    }
}
