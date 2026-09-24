namespace WG.AP.Integrations.Pace;

internal static class PaceXPath
{
    /// <summary>
    /// Quotes a value as an XPath 1.0 string literal for Pace <c>xpathFilter</c>s. XPath has no escape
    /// sequences, so a value containing both quote kinds is built with <c>concat(...)</c>.
    /// </summary>
    public static string StringLiteral(string value)
    {
        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return $"'{value}'";
        }

        if (!value.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{value}\"";
        }

        return $"concat({string.Join(", \"'\", ", value.Split('\'').Select(part => $"'{part}'"))})";
    }
}
