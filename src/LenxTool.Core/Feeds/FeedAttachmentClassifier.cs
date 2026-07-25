using System.Globalization;
using System.Net;
using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class FeedAttachmentClassifier
{
    private static readonly Dictionary<string, MimeRule> MimeRules =
        new Dictionary<string, MimeRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = new(
                FeedAttachmentKind.Image,
                [".jpg", ".jpeg"]),
            ["image/jpg"] = new(
                FeedAttachmentKind.Image,
                [".jpg", ".jpeg"]),
            ["image/png"] = new(
                FeedAttachmentKind.Image,
                [".png"]),
            ["image/webp"] = new(
                FeedAttachmentKind.Image,
                [".webp"]),
            ["image/gif"] = new(
                FeedAttachmentKind.Image,
                [".gif"]),
            ["image/avif"] = new(
                FeedAttachmentKind.Image,
                [".avif"]),
            ["audio/mpeg"] = new(
                FeedAttachmentKind.Audio,
                [".mp3"]),
            ["audio/mp4"] = new(
                FeedAttachmentKind.Audio,
                [".m4a"]),
            ["audio/x-m4a"] = new(
                FeedAttachmentKind.Audio,
                [".m4a"]),
            ["audio/aac"] = new(
                FeedAttachmentKind.Audio,
                [".aac"]),
            ["audio/ogg"] = new(
                FeedAttachmentKind.Audio,
                [".ogg", ".oga"]),
            ["audio/opus"] = new(
                FeedAttachmentKind.Audio,
                [".opus"]),
            ["audio/wav"] = new(
                FeedAttachmentKind.Audio,
                [".wav"]),
            ["audio/x-wav"] = new(
                FeedAttachmentKind.Audio,
                [".wav"]),
            ["audio/flac"] = new(
                FeedAttachmentKind.Audio,
                [".flac"]),
            ["video/mp4"] = new(
                FeedAttachmentKind.Video,
                [".mp4", ".m4v"]),
            ["video/webm"] = new(
                FeedAttachmentKind.Video,
                [".webm"]),
            ["video/quicktime"] = new(
                FeedAttachmentKind.Video,
                [".mov"]),
            ["video/ogg"] = new(
                FeedAttachmentKind.Video,
                [".ogv"])
        };

    private static readonly Dictionary<string, FeedAttachmentKind>
        ExtensionKinds = MimeRules.Values
            .SelectMany(rule => rule.Extensions.Select(
                extension => (extension, rule.Kind)))
            .GroupBy(item => item.extension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Kind,
                StringComparer.OrdinalIgnoreCase);

    public static FeedAttachmentClassification Classify(
        FeedEnclosure enclosure,
        string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(enclosure);
        Uri? safeUri = TryCreateAllowedUri(enclosure.Url, baseUrl);
        string? extension = ReadExtension(safeUri);
        string? mediaType = NormalizeMediaType(enclosure.MediaType);
        MimeRules.TryGetValue(mediaType ?? string.Empty, out MimeRule? mimeRule);
        ExtensionKinds.TryGetValue(
            extension ?? string.Empty,
            out FeedAttachmentKind extensionKind);

        FeedAttachmentKind kind;
        FeedAttachmentTypeStatus typeStatus;
        if (mimeRule is not null
            && extension is not null
            && mimeRule.Extensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            kind = mimeRule.Kind;
            typeStatus = FeedAttachmentTypeStatus.Verified;
        }
        else if (mimeRule is not null && extension is not null)
        {
            kind = FeedAttachmentKind.Unknown;
            typeStatus = FeedAttachmentTypeStatus.Conflicting;
        }
        else if (mimeRule is not null)
        {
            kind = mimeRule.Kind;
            typeStatus = FeedAttachmentTypeStatus.Unverified;
        }
        else if (extensionKind != FeedAttachmentKind.Unknown)
        {
            kind = extensionKind;
            typeStatus = FeedAttachmentTypeStatus.Unverified;
        }
        else
        {
            kind = FeedAttachmentKind.Unknown;
            typeStatus = FeedAttachmentTypeStatus.Unsupported;
        }

        return new(
            safeUri?.AbsoluteUri,
            kind,
            typeStatus,
            safeUri is null
                ? FeedAttachmentUrlStatus.Blocked
                : FeedAttachmentUrlStatus.Allowed,
            mediaType,
            extension,
            enclosure.Length is >= 0 ? enclosure.Length : null,
            enclosure.Title);
    }

    private static Uri? TryCreateAllowedUri(
        string value,
        string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value.Any(char.IsControl))
        {
            return null;
        }

        Uri? resolved;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out resolved))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
                || !Uri.TryCreate(baseUri, value.Trim(), out resolved))
            {
                return null;
            }
        }

        if (resolved.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(resolved.IdnHost)
            || !string.IsNullOrEmpty(resolved.UserInfo)
            || !IsDefaultWebPort(resolved))
        {
            return null;
        }

        string host;
        try
        {
            host = IPAddress.TryParse(
                resolved.IdnHost,
                out IPAddress? address)
                ? address.ToString().ToLowerInvariant()
                : new IdnMapping()
                    .GetAscii(resolved.IdnHost)
                    .TrimEnd('.')
                    .ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (NetworkTargetClassifier.IsReservedHostName(host)
            || (IPAddress.TryParse(host, out IPAddress? literal)
                && NetworkTargetClassifier.Classify(literal)
                    != NetworkAddressDisposition.Public))
        {
            return null;
        }

        var builder = new UriBuilder(resolved)
        {
            Host = host,
            Fragment = string.Empty
        };
        if ((builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443)
            || (builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80))
        {
            builder.Port = -1;
        }

        string normalized = builder.Uri.AbsoluteUri;
        return normalized.Length <= 2048 ? builder.Uri : null;
    }

    private static bool IsDefaultWebPort(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443)
        || (uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80);

    private static string? ReadExtension(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        string extension = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(extension)
            || extension.Length > 12
            ? null
            : extension.ToLowerInvariant();
    }

    private static string? NormalizeMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        int parameter = normalized.IndexOf(';');
        if (parameter >= 0)
        {
            normalized = normalized[..parameter].Trim();
        }

        return normalized.Length is > 0 and <= 128
            ? normalized.ToLowerInvariant()
            : null;
    }

    private sealed record MimeRule(
        FeedAttachmentKind Kind,
        IReadOnlyList<string> Extensions);
}
