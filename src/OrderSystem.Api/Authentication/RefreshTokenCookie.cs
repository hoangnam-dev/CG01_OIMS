namespace OrderSystem.Api.Authentication;

public static class RefreshTokenCookie
{
    public const string Name = "__Secure-oims-refresh";

    private const string Path = "/api/auth";

    public static void Append(HttpResponse response, string token, DateTimeOffset expiresAt)
    {
        response.Cookies.Append(Name, token, CreateOptions(expiresAt));
    }

    public static void Delete(HttpResponse response)
    {
        response.Cookies.Delete(Name, CreateOptions(DateTimeOffset.UnixEpoch));
    }

    private static CookieOptions CreateOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = Path,
        Expires = expiresAt,
        IsEssential = true
    };
}
