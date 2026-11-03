using System.Net;
using System.Text.RegularExpressions;

namespace NetTrace.Reporting;

/// <summary>Replaces private and link-local IPv4 addresses in report text.</summary>
public static partial class Redactor
{
    public const string Placeholder = "[redacted local address]";

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex Ipv4();

    public static bool IsLocalAddress(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();
        return b.Length == 4
            && (b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254));
    }

    public static string Redact(string text, bool enabled)
    {
        if (!enabled || string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (IPAddress.TryParse(text, out var whole))
        {
            return IsLocalAddress(whole) ? Placeholder : text;
        }

        return Ipv4().Replace(text, m => IPAddress.TryParse(m.Value, out var ip) && IsLocalAddress(ip) ? Placeholder : m.Value);
    }
}
