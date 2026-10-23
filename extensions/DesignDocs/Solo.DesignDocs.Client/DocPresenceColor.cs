using System.Globalization;

namespace Solo.DesignDocs.Client;

public static class DocPresenceColor
{
    public static string Value(int slot, bool translucent=false) =>
        $"oklch(var(--design-docs-presence-lightness,65%) .15 {(slot*137.508%360).ToString(CultureInfo.InvariantCulture)}{(translucent?" / .2":"")})";
}
