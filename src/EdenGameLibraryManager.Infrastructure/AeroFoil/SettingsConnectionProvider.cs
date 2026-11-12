using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.TorBox;

namespace EdenGameLibraryManager.Infrastructure.AeroFoil;

/// <summary>Builds the <see cref="ServerConnection"/> for the active profile (AeroFoil or TorBox) from settings and the credential store.</summary>
public sealed class SettingsConnectionProvider(ISettingsService settings, ICredentialStore credentials) : IConnectionProvider
{
    public ServerConnection? GetConnection() => For(settings.Current.ActiveProfile, credentials);

    public static ServerConnection? For(ServerProfile? profile, ICredentialStore credentials)
    {
        if (profile is null) return null;
        if (profile.Kind == ServerKind.TorBox)
            return TorBoxClient.ConnectionFor(credentials.GetPassword(profile.Id) ?? string.Empty, profile.Id);
        if (string.IsNullOrWhiteSpace(profile.ServerUrl)) return null;
        try
        {
            var uri = UrlPolicy.NormalizeServerUrl(profile.ServerUrl, profile.AllowInsecureHttp);
            return new ServerConnection(uri, profile.Username, credentials.GetPassword(profile.Id) ?? string.Empty, profile.AllowInsecureHttp);
        }
        catch (AeroFoilException)
        {
            return null;
        }
    }
}
