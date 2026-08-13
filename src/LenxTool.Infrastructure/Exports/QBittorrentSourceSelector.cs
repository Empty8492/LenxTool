using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

internal static class QBittorrentSourceSelector
{
    public static async Task<QBittorrentSource> SelectAsync(
        FeedEntry entry,
        ITorrentFileFetcher fetcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(fetcher);
        string[] magnetCandidates = new[] { entry.NormalizedUrl }
            .Concat(entry.Enclosures.Select(value => value.Url))
            .Where(value => value is not null
                && value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
        if (magnetCandidates.Length == 1)
        {
            return MagnetUriValidator.Validate(magnetCandidates[0]);
        }
        if (magnetCandidates.Length > 1)
        {
            throw Unsupported();
        }
        FeedEnclosure[] torrentCandidates = entry.Enclosures
            .Where(IsTorrentCandidate)
            .ToArray();
        if (torrentCandidates.Length != 1)
        {
            throw Unsupported();
        }
        return await fetcher.FetchAsync(torrentCandidates[0], cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsTorrentCandidate(FeedEnclosure value) =>
        string.Equals(
            value.MediaType,
            "application/x-bittorrent",
            StringComparison.OrdinalIgnoreCase)
        || Uri.TryCreate(value.Url, UriKind.Absolute, out Uri? uri)
            && uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    private static EntryExportException Unsupported() =>
        new(new(EntryExportErrorCode.UnsupportedContent, false));
}

internal static class MagnetUriValidator
{
    public static QBittorrentMagnetSource Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 8192
            || value.Any(char.IsControl)
            || !value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Magnet URI 无效。", nameof(value));
        }
        string[] hashes = value[8..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2
                && string.Equals(
                    Decode(part[0]),
                    "xt",
                    StringComparison.OrdinalIgnoreCase))
            .Select(part => Decode(part[1]))
            .Where(part => part.StartsWith(
                "urn:btih:",
                StringComparison.OrdinalIgnoreCase))
            .Select(part => NormalizeInfoHash(part[9..]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (hashes.Length != 1)
        {
            throw new ArgumentException("Magnet 必须包含唯一 BTIH。", nameof(value));
        }
        return new(
            $"magnet:?{value[8..]}",
            hashes[0]);
    }

    private static string NormalizeInfoHash(string value)
    {
        if (value.Length == 40
            && value.All(Uri.IsHexDigit))
        {
            return value.ToLowerInvariant();
        }
        if (value.Length == 32
            && value.All(character =>
                character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '2' and <= '7'))
        {
            return Convert.ToHexString(DecodeBase32(value)).ToLowerInvariant();
        }
        throw new ArgumentException("BTIH 长度或字符无效。", nameof(value));
    }

    private static byte[] DecodeBase32(string value)
    {
        byte[] output = new byte[20];
        int buffer = 0;
        int bits = 0;
        int index = 0;
        foreach (char raw in value.ToUpperInvariant())
        {
            int digit = raw is >= 'A' and <= 'Z'
                ? raw - 'A'
                : raw - '2' + 26;
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }
        return output;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException exception)
        {
            throw new ArgumentException("Magnet 编码无效。", nameof(value), exception);
        }
    }
}
