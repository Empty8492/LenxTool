using System.Text;

namespace LenxTool.Core.Tools;

public static class EncodingToolkit
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string ToBase64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToBase64String(Utf8.GetBytes(value));
    }

    public static string FromBase64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Utf8.GetString(Convert.FromBase64String(value.Trim()));
    }

    public static string EncodeUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(value);
    }

    public static string DecodeUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.UnescapeDataString(value);
    }
}
