using System.Text;

namespace EdenGameLibraryManager.Core.Models;

/// <summary>Resolved server address plus credentials. <see cref="ToString"/> never reveals the password.</summary>
public sealed record ServerConnection(Uri BaseUri, string Username, string Password, bool AllowInsecureHttp)
{
    /// <summary>Stable identifier for the server, used to scope manifest rows and cached catalogues.</summary>
    public string ServerKey => BaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();

    public bool HasCredentials => !string.IsNullOrEmpty(Username);

    public string BasicAuthParameter => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

    public override string ToString() => $"{BaseUri} (user: {(HasCredentials ? Username : "<anonymous>")})";
}
