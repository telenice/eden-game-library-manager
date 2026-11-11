using System.Security.Cryptography;
using System.Text;

namespace EdenGameLibraryManager.Core.Models;

/// <summary>
/// Resolved server address plus credentials. For TorBox, <see cref="Password"/> holds the API key and
/// <see cref="BaseUri"/> is the TorBox API. <see cref="ToString"/> never reveals the password or key.
/// </summary>
public sealed record ServerConnection(Uri BaseUri, string Username, string Password, bool AllowInsecureHttp, ServerKind Kind = ServerKind.AeroFoil)
{
    /// <summary>
    /// Stable identifier for the server, used to scope manifest rows and cached catalogues. Every
    /// TorBox profile shares one API address, so TorBox accounts are told apart by a hash of the key.
    /// </summary>
    public string ServerKey => Kind == ServerKind.TorBox
        ? "torbox:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password)))[..16].ToLowerInvariant()
        : BaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();

    public bool HasCredentials => !string.IsNullOrEmpty(Username);

    public string BasicAuthParameter => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

    public override string ToString() => Kind == ServerKind.TorBox
        ? "TorBox (API key)"
        : $"{BaseUri} (user: {(HasCredentials ? Username : "<anonymous>")})";
}
