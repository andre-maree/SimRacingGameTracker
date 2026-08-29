using GameTracker.Domain.Entities;

namespace GameTrackerRazorLibrary.Catalogue;

/// <summary>A single page of catalogue rows plus the unpaged total.</summary>
public sealed record CataloguePage<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>
/// Read-only catalogue access for shared UI components.
/// </summary>
/// <remarks>
/// The catalogue grids must run in two very different hosts: the Blazor Server app reads
/// SQL Server directly, while the WPF client reads its offline SQLite mirror. This
/// interface is what lets one set of components serve both — the component never learns
/// which database, or even which machine, is answering. Only reads are exposed: editing
/// remains server-only, so the client cannot be tricked into thinking a local change is
/// authoritative.
/// </remarks>
public interface ICatalogueReader
{
    /// <summary>
    /// Returns one page of cars. Paging, sorting and filtering are pushed to the database
    /// so the ~4,000-row R3E catalogue is never materialised in the UI.
    /// </summary>
    /// <param name="orderBy">A Dynamic LINQ order clause, as produced by Radzen's grid.</param>
    Task<CataloguePage<Car>> GetCarsAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one page of track layouts.</summary>
    Task<CataloguePage<Track>> GetTracksAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default);
}
