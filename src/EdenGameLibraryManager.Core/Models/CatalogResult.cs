namespace EdenGameLibraryManager.Core.Models;

public sealed record CatalogResult
{
    public required IReadOnlyList<RemoteGameItem> Items { get; init; }
    public required DateTimeOffset RetrievedAt { get; init; }

    /// <summary>The API path that produced this catalogue (for diagnostics).</summary>
    public string? Endpoint { get; init; }

    /// <summary>False when the server reports truncation or we cannot confirm the full library was returned.</summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>True when the catalogue was loaded from the local offline cache.</summary>
    public bool FromCache { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record TitleDetails
{
    public required string TitleId { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> ScreenshotUrls { get; init; } = [];
}

/// <summary>One title from titledb: the eShop name, text and artwork for an application id.</summary>
public sealed record TitleInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? IconUrl { get; init; }
    public IReadOnlyList<string> ScreenshotUrls { get; init; } = [];
    public DateOnly? ReleaseDate { get; init; }
    public string? Category { get; init; }
}

/// <summary>State of the local titledb copy, for Settings.</summary>
public sealed record TitleDbStatus(bool IsAvailable, string? Region, int TitleCount, DateTimeOffset? UpdatedAt, string? LastError);
