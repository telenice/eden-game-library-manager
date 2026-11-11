using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.TorBox;

namespace EdenGameLibraryManager.Infrastructure.Downloads;

/// <summary>
/// AeroFoil files are fetched from the configured server with its credentials. TorBox files are
/// fetched from the direct link TorBox hands out, through a connection with no credentials of its own
/// that is limited to that link's origin, so a redirect to another host is refused before the link
/// (which embeds the API key) could be sent there.
/// </summary>
public sealed class DownloadLinkResolver(TorBoxClient torBox) : IDownloadLinkResolver
{
    public void Validate(ServerConnection connection, RemoteGameItem item)
    {
        if (connection.Kind == ServerKind.TorBox)
            TorBoxClient.ParseItemUrl(item.Url);
        else
            UrlPolicy.ResolveTrusted(connection.BaseUri, item.Url);
    }

    public async Task<DownloadLink> ResolveAsync(ServerConnection connection, RemoteGameItem item, CancellationToken cancellationToken)
    {
        if (connection.Kind != ServerKind.TorBox)
            return new DownloadLink(connection, UrlPolicy.ResolveTrusted(connection.BaseUri, item.Url));

        var link = await torBox.RequestDownloadLinkAsync(connection, item, cancellationToken);
        var origin = new Uri(link.GetLeftPart(UriPartial.Authority) + "/");
        return new DownloadLink(new ServerConnection(origin, string.Empty, string.Empty, AllowInsecureHttp: false), link);
    }
}
