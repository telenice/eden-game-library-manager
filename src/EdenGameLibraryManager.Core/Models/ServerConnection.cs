using System.Security.Cryptography;
using System.Text;

namespace EdenGameLibraryManager.Core.Models;

/// <summary>
/// Resolved server address plus credentials. For TorBox, <see cref="Password"/> holds the API key and
/// <see cref="BaseUri"/> is the TorBox API. <see cref="ToString"/> never reveals the password or key.
/// </summary>
public sealed record ServerConnection(
    Uri BaseUri, string Username, string Password, bool AllowInsecureHttp, ServerKind Kind = ServerKind.AeroFoil, string? ProfileId = null)
{
    /// <summary>
    /// Stable identifier for the server, used to scope manifest rows and cached catalogues. Every
    /// TorBox profile shares one API address, so TorBox accounts are told apart by their profile, which
    /// keeps the key when the API key is replaced (or by a hash of the key when there is no profile yet).
    /// </summary>
    public string ServerKey => Kind != ServerKind.TorBox
        ? BaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant()
        : ProfileId is null ? ApiKeyServerKey : "torbox:profile:" + ProfileId;

    /// <summary>True for this server's key, including the API-key-based key TorBox downloads were queued under before 1.1.7.</summary>
    public bool Owns(string? serverKey) =>
        string.Equals(serverKey, ServerKey, StringComparison.OrdinalIgnoreCase) ||
        (Kind == ServerKind.TorBox && string.Equals(serverKey, ApiKeyServerKey, StringComparison.OrdinalIgnoreCase));

    private string ApiKeyServerKey =>
        "torbox:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password)))[..16].ToLowerInvariant();

    public bool HasCredentials => !string.IsNullOrEmpty(Username);

    public string BasicAuthParameter => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

    public override string ToString() => Kind == ServerKind.TorBox
        ? "TorBox (API key)"
        : $"{BaseUri} (user: {(HasCredentials ? Username : "<anonymous>")})";
}
