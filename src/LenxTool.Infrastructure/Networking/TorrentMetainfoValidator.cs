using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace LenxTool.Infrastructure.Networking;

internal static class TorrentMetainfoValidator
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumDepth = 64;
    private const int MaximumNodes = 100_000;

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "BitTorrent v1 info hashes are defined as SHA-1 over the raw info dictionary.")]
    public static QBittorrentFileSource Validate(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is < 4 or > MaximumBytes)
        {
            throw new ArgumentException("Torrent 文件大小无效。", nameof(content));
        }
        var parser = new Parser(content);
        (int start, int length) = parser.ParseMetainfo();
        string hash = Convert.ToHexString(
                SHA1.HashData(content.AsSpan(start, length)))
            .ToLowerInvariant();
        return new((byte[])content.Clone(), hash);
    }

    private sealed class Parser(byte[] content)
    {
        private int _position;
        private int _nodes;

        public (int Start, int Length) ParseMetainfo()
        {
            Require((byte)'d');
            ReadOnlySpan<byte> previous = default;
            int infoStart = -1;
            int infoLength = 0;
            while (Peek() != (byte)'e')
            {
                ReadOnlySpan<byte> key = ParseByteString();
                EnsureSorted(previous, key);
                previous = key;
                bool isInfo = key.SequenceEqual("info"u8);
                int start = _position;
                if (isInfo && Peek() != (byte)'d') Fail();
                ParseValue(1);
                if (isInfo)
                {
                    if (infoStart >= 0) Fail();
                    infoStart = start;
                    infoLength = _position - start;
                }
            }
            Require((byte)'e');
            if (_position != content.Length || infoStart < 0) Fail();
            return (infoStart, infoLength);
        }

        private void ParseValue(int depth)
        {
            if (++_nodes > MaximumNodes || depth > MaximumDepth) Fail();
            byte token = Peek();
            if (token is >= (byte)'0' and <= (byte)'9')
            {
                _ = ParseByteString();
                return;
            }
            switch (token)
            {
                case (byte)'i':
                    ParseInteger();
                    return;
                case (byte)'l':
                    Require((byte)'l');
                    while (Peek() != (byte)'e') ParseValue(depth + 1);
                    Require((byte)'e');
                    return;
                case (byte)'d':
                    Require((byte)'d');
                    ReadOnlySpan<byte> previous = default;
                    while (Peek() != (byte)'e')
                    {
                        ReadOnlySpan<byte> key = ParseByteString();
                        EnsureSorted(previous, key);
                        previous = key;
                        ParseValue(depth + 1);
                    }
                    Require((byte)'e');
                    return;
                default:
                    Fail();
                    return;
            }
        }

        private ReadOnlySpan<byte> ParseByteString()
        {
            int lengthStart = _position;
            while (Peek() is >= (byte)'0' and <= (byte)'9') _position++;
            if (_position == lengthStart || Peek() != (byte)':') Fail();
            ReadOnlySpan<byte> digits = content.AsSpan(
                lengthStart,
                _position - lengthStart);
            if (digits.Length > 1 && digits[0] == (byte)'0') Fail();
            if (!int.TryParse(
                    System.Text.Encoding.ASCII.GetString(digits),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int length)
                || length < 0)
            {
                Fail();
            }
            _position++;
            if (length > content.Length - _position) Fail();
            ReadOnlySpan<byte> result = content.AsSpan(_position, length);
            _position += length;
            return result;
        }

        private void ParseInteger()
        {
            Require((byte)'i');
            int start = _position;
            if (Peek() == (byte)'-') _position++;
            int digitsStart = _position;
            while (Peek() is >= (byte)'0' and <= (byte)'9') _position++;
            if (_position == digitsStart || Peek() != (byte)'e') Fail();
            ReadOnlySpan<byte> raw = content.AsSpan(start, _position - start);
            ReadOnlySpan<byte> digits = content.AsSpan(
                digitsStart,
                _position - digitsStart);
            if (digits.Length > 1 && digits[0] == (byte)'0'
                || raw.SequenceEqual("-0"u8))
            {
                Fail();
            }
            Require((byte)'e');
        }

        private byte Peek()
        {
            if (_position >= content.Length) Fail();
            return content[_position];
        }

        private void Require(byte expected)
        {
            if (Peek() != expected) Fail();
            _position++;
        }

        private static void EnsureSorted(
            ReadOnlySpan<byte> previous,
            ReadOnlySpan<byte> current)
        {
            if (!previous.IsEmpty && previous.SequenceCompareTo(current) >= 0)
            {
                Fail();
            }
        }

        [DoesNotReturn]
        private static void Fail() =>
            throw new ArgumentException("Torrent metainfo 不是规范 bencode。", nameof(content));
    }
}
