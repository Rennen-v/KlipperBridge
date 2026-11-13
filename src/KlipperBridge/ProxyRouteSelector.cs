using Microsoft.AspNetCore.Http;

public static class ProxyRouteSelector
{
    private static readonly string[] MoonrakerPrefixes =
    [
        "/server", "/printer", "/machine", "/access", "/api", "/websocket", "/klippysocket",
        "/oneshot_token", "/debug", "/connection", "/extensions", "/provider", "/moonraker"
    ];

    public static bool IsMoonrakerPath(PathString path)
    {
        var value = path.Value ?? "/";
        return MoonrakerPrefixes.Any(prefix => value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (value.Length == prefix.Length || value[prefix.Length] == '/')));
    }
}
