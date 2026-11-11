namespace EdenGameLibraryManager.Core.Models;

/// <summary>A newer release of this app published on GitHub.</summary>
public sealed record AppUpdate
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public required Uri ReleasePage { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The Windows zip for this machine, when the release has one.</summary>
    public Uri? PackageUrl { get; init; }
    public string? PackageName { get; init; }
    public long PackageSize { get; init; }

    /// <summary>The package's signature (<c>&lt;package&gt;.sig</c>); without it the update can't be installed automatically.</summary>
    public Uri? SignatureUrl { get; init; }

    public bool CanInstallAutomatically => PackageUrl is not null && SignatureUrl is not null;
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
