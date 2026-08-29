using GameTracker.Domain.Entities;
using GameTrackerRazorLibrary.Catalogue;

namespace GameTrackerBlazorServerApp.Data;

/// <summary>
/// Exposes the server's <see cref="CatalogueService"/> through the shared
/// <see cref="ICatalogueReader"/> contract used by the reusable grids.
/// </summary>
/// <remarks>
/// A thin adapter rather than changing <see cref="CatalogueService"/> to implement the
/// interface directly: its existing methods already return <c>PagedResult&lt;T&gt;</c>
/// with identical signatures, and C# cannot overload on return type alone. Keeping the
/// adapter separate also leaves the service's admin mutation surface out of the shared
/// read-only contract.
/// </remarks>
public sealed class ServerCatalogueReader(CatalogueService catalogue) : ICatalogueReader
{
    public async Task<CataloguePage<Car>> GetCarsAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default)
    {
        var page = await catalogue.GetCarsAsync(skip, take, filter, orderBy, cancellationToken);
        return new CataloguePage<Car>(page.Items, page.TotalCount);
    }

    public async Task<CataloguePage<Track>> GetTracksAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default)
    {
        var page = await catalogue.GetTracksAsync(skip, take, filter, orderBy, cancellationToken);
        return new CataloguePage<Track>(page.Items, page.TotalCount);
    }
}
