using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UPnP.Rx.Parsing;

/// <summary>
/// Namespace- and case-tolerant XML lookup helpers implementing the leniency
/// policy: real-world UPnP devices ship wrong namespaces, wrong element casing,
/// stray whitespace inside token values, and unescaped ampersands. Lookups match
/// on local name only, ignoring case; values are normalized; nothing here throws
/// on bad input.
/// </summary>
internal static partial class XmlLeniency
{
    /// <summary>The first child element matching <paramref name="localName"/> (any namespace, any casing).</summary>
    internal static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    /// <summary>All child elements matching <paramref name="localName"/> (any namespace, any casing).</summary>
    internal static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The trimmed text of the child element, or <see langword="null"/> when the
    /// element is absent or empty (devices ship empty elements like <c>&lt;UPC/&gt;</c>).
    /// </summary>
    internal static string? Text(XElement parent, string localName)
    {
        var value = Child(parent, localName)?.Value.Trim();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// The text of the child element with <em>all</em> whitespace stripped, for
    /// values that cannot legitimately contain any (UDNs, type URNs, URLs) — seen
    /// in the wild with embedded line breaks. <see langword="null"/> when absent or empty.
    /// </summary>
    internal static string? Token(XElement parent, string localName)
    {
        var raw = Child(parent, localName)?.Value;

        if (raw is null)
        {
            return null;
        }

        var token = WhitespaceRun().Replace(raw, string.Empty);

        return token.Length == 0 ? null : token;
    }

    /// <summary>The child element's value as an integer, or <see langword="null"/> when absent or unparsable.</summary>
    internal static int? Int(XElement parent, string localName) =>
        int.TryParse(Token(parent, localName), out var value) ? value : null;

    /// <summary>
    /// Resolves <paramref name="raw"/> (absolute or relative) against
    /// <paramref name="baseUrl"/>, returning <see langword="null"/> when the value
    /// is absent or does not form an absolute URI.
    /// </summary>
    internal static Uri? AbsoluteUri(Uri baseUrl, string? raw) =>
        raw is not null
        && Uri.TryCreate(baseUrl, raw, out var resolved)
        && resolved.IsAbsoluteUri
            ? resolved
            : null;

    /// <summary>
    /// Escapes bare <c>&amp;</c> characters that are not part of an entity
    /// reference — the most common real-world XML malformation in device
    /// documents (<c>AT&amp;T</c>, <c>D&amp;M Holdings</c>, …).
    /// </summary>
    internal static string EscapeBareAmpersands(string xml) =>
        BareAmpersand().Replace(xml, "&amp;");

    [GeneratedRegex(@"&(?!(?:[a-zA-Z][a-zA-Z0-9]*|#[0-9]+|#x[0-9a-fA-F]+);)")]
    private static partial Regex BareAmpersand();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
