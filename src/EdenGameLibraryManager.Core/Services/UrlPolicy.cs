using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Core.Services;

/// <summary>
/// Validates the configured server address and makes sure every request (catalogue, artwork,
/// downloads, redirects) stays on that server's origin.
/// </summary>
public static class UrlPolicy
{
    public static Uri NormalizeServerUrl(string input, bool allowInsecureHttp)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "Enter the AeroFoil server URL.");

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, $"'{input}' is not a valid server URL.");

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "The server URL must start with https:// (or http:// for a trusted local server).");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "Do not put credentials in the server URL; use the username and password fields.");

        if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
            throw new AeroFoilException(AeroFoilErrorKind.InsecureConnection,
                "Plain HTTP is disabled. Use https://, or enable \"Allow insecure HTTP\" only for a trusted LAN or VPN server.");

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    /// <summary>Hosts that serve titledb artwork. Images from these are fetched without any credentials.</summary>
    public static readonly IReadOnlyList<string> PublicArtworkHosts = ["img-eshop.cdn.nintendo.net"];

    public static bool IsPublicArtworkUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort &&
        PublicArtworkHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    public static bool IsSameOrigin(Uri a, Uri b) =>
        a.IsAbsoluteUri && b.IsAbsoluteUri &&
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port;

    /// <summary>Builds an API URL below the configured base path, e.g. "api/shop/sections".</summary>
    public static Uri Combine(Uri baseUri, string relativePath) => new(baseUri, relativePath.TrimStart('/'));

    /// <summary>
    /// Resolves a catalogue-provided URL (usually relative) against the server and rejects anything
    /// that would leave the trusted origin. The fragment (AeroFoil's "#filename" hint) is removed.
    /// </summary>
    public static Uri ResolveTrusted(Uri baseUri, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl, "The catalogue entry has no download URL.");

        if (!Uri.TryCreate(baseUri, url.Trim(), out var resolved))
            throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl, "The catalogue entry has an invalid URL.");

        if (resolved.Scheme != Uri.UriSchemeHttps && resolved.Scheme != Uri.UriSchemeHttp)
            throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl, "The catalogue entry uses an unsupported URL scheme.");

        if (!string.IsNullOrEmpty(resolved.UserInfo) || !IsSameOrigin(resolved, baseUri))
            throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl,
                $"Refusing to contact {resolved.Scheme}://{resolved.Authority}: it is not the configured AeroFoil server.");

        return new UriBuilder(resolved) { Fragment = string.Empty }.Uri;
    }
}
