using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.Core.Models;

public enum AeroFoilErrorKind
{
    NotConfigured,
    Network,
    Timeout,
    InvalidCredentials,
    AccessDenied,
    AccountFrozen,
    RateLimited,
    ClientBlocked,
    EncryptedCatalog,
    InsecureConnection,
    UntrustedUrl,
    NotFound,
    ServerError,
    InvalidResponse,
}

/// <summary>A classified failure talking to AeroFoil. The message is safe to display to the user.</summary>
public sealed class AeroFoilException(AeroFoilErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AeroFoilErrorKind Kind { get; } = kind;

    /// <summary>Transient failures may be retried with backoff; credential and permission failures must not.</summary>
    public bool IsTransient => Kind is AeroFoilErrorKind.Network or AeroFoilErrorKind.Timeout or AeroFoilErrorKind.ServerError;

    /// <summary>True when the server could not be reached at all (used to switch to offline mode).</summary>
    public bool IsConnectivityFailure => Kind is AeroFoilErrorKind.Network or AeroFoilErrorKind.Timeout;
}

/// <summary>The transfer ended early or the size did not match; safe to retry/resume.</summary>
public sealed class IncompleteDownloadException(string message) : IOException(message);

public sealed class CacheLimitExceededException(CachePlan plan)
    : Exception($"Not enough room under your storage limit: {SizeFormatter.Format(plan.Shortfall)} more space is needed. " +
                "Remove downloaded games or raise Maximum storage for downloads in Settings.")
{
    public CachePlan Plan { get; } = plan;
}

public sealed class InsufficientDiskSpaceException(long required, long available)
    : Exception($"Not enough free disk space: {SizeFormatter.Format(required)} needed, {SizeFormatter.Format(available)} available.")
{
    public long Required { get; } = required;
    public long Available { get; } = available;
}

public sealed class UnsafePathException(string message) : Exception(message);
