using System.Linq.Dynamic.Core;
using GameTracker.Domain.Entities;
using GameTrackerRazorLibrary.Catalogue;
using GameTrackerWpfClientApp.Data;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerWpfClientApp.Services.Catalogue;

/// <summary>
/// Serves the catalogue grids from the local SQLite mirror.
/// </summary>
/// <remarks>
/// Reads never touch the network. The mirror is populated by
/// <see cref="Sync.CatalogueSyncService"/>, so browsing works at a track with no
/// connectivity — which is the entire reason the client keeps a local copy. There is no
/// <c>IsDeleted</c> filter here because tombstones are applied as real deletions during
/// sync, so a row present locally is by definition a live row.
/// </remarks>
public sealed class LocalCatalogueReader : ICatalogueReader
{
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public LocalCatalogueReader(IDbContextFactory<ClientDbContext> contextFactory) =>
        _contextFactory = contextFactory;

    public async Task<CataloguePage<Car>> GetCarsAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default)
    {
        // A short-lived context per query: the grid is long-lived, and a shared context
        // would accumulate tracked entities for every page the user browses.
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<Car> query = context.Cars.AsNoTracking();
        query = ApplyFilter(query, filter);

        var total = await query.CountAsync(cancellationToken);

        query = string.IsNullOrWhiteSpace(orderBy)
            ? query.OrderBy(c => c.Name)
            : query.OrderBy(orderBy);

        var items = await query.Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new CataloguePage<Car>(items, total);
    }

    public async Task<CataloguePage<Track>> GetTracksAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<Track> query = context.Tracks.AsNoTracking();
        query = ApplyFilter(query, filter);

        var total = await query.CountAsync(cancellationToken);

        query = string.IsNullOrWhiteSpace(orderBy)
            ? query.OrderBy(t => t.Name).ThenBy(t => t.LayoutName)
            : query.OrderBy(orderBy);

        var items = await query.Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new CataloguePage<Track>(items, total);
    }

    /// <summary>
    /// Applies the grid's Dynamic LINQ filter expression, still as a database query so
    /// filtering does not pull the whole catalogue into memory first.
    /// </summary>
    private static IQueryable<T> ApplyFilter<T>(IQueryable<T> query, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? query : query.Where(filter);
}
