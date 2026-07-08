namespace Solo.Client.Api;

/// <summary>
/// Reads a single query-string value. Small enough not to justify pulling in
/// <c>Microsoft.AspNetCore.WebUtilities</c> just for <c>returnUrl</c>.
/// </summary>
public static class QueryString
{
    public static string? Value(string uri, string key)
    {
        var query = new Uri(uri).Query;
        if (query.Length <= 1)
        {
            return null;
        }

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            if (Uri.UnescapeDataString(name) != key)
            {
                continue;
            }

            return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }
}
