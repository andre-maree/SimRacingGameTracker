using System.Linq.Dynamic.Core;
using System.Security.Claims;
using GameTracker.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerBlazorServerApp.Data;

/// <summary>A single page of catalogue rows plus the unpaged total.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>
/// Query and mutation surface behind the Radzen catalogue grids.
/// </summary>
/// <remarks>
/// Paging, sorting and filtering are all pushed into SQL: the grid never receives more
/// than one page, so the ~4,000-row R3E catalogue does not get materialised per render.
/// Reads are <c>AsNoTracking</c> because the Blazor Server circuit holds one scoped
/// DbContext for the life of the connection, and tracking every browsed row would leak
/// memory across the session. Mutations re-authorize server-side rather than trusting
/// that the UI hid the button.
/// </remarks>
public sealed class CatalogueService(ApplicationDbContext context, IAuthorizationService authorization)
{
    public const string AdminPolicy = "AdminOnly";

    public async Task<PagedResult<Car>> GetCarsAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default)
    {
        // Tombstones are a sync concern; the catalogue UI must not show deleted rows.
        IQueryable<Car> query = context.Cars.AsNoTracking().Where(c => !c.IsDeleted);
        query = ApplyFilter(query, filter);

        var total = await query.CountAsync(cancellationToken);

        query = string.IsNullOrWhiteSpace(orderBy)
            ? query.OrderBy(c => c.Name)
            : query.OrderBy(orderBy);

        var items = await query.Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new PagedResult<Car>(items, total);
    }

    public async Task<PagedResult<Track>> GetTracksAsync(
        int skip,
        int take,
        string? filter,
        string? orderBy,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Track> query = context.Tracks.AsNoTracking().Where(t => !t.IsDeleted);
        query = ApplyFilter(query, filter);

        var total = await query.CountAsync(cancellationToken);

        query = string.IsNullOrWhiteSpace(orderBy)
            ? query.OrderBy(t => t.Name).ThenBy(t => t.LayoutName)
            : query.OrderBy(orderBy);

        var items = await query.Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new PagedResult<Track>(items, total);
    }

    public Task<List<Game>> GetGamesAsync(CancellationToken cancellationToken = default)
        => context.Games.AsNoTracking().Where(g => !g.IsDeleted).OrderBy(g => g.Name).ToListAsync(cancellationToken);

    public async Task SaveCarAsync(ClaimsPrincipal user, Car car, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(user);

        if (car.Id == 0)
        {
            context.Cars.Add(car);
        }
        else
        {
            context.Cars.Update(car);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveTrackAsync(ClaimsPrincipal user, Track track, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(user);

        if (track.Id == 0)
        {
            context.Tracks.Add(track);
        }
        else
        {
            context.Tracks.Update(track);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes a car. The ServerVersion interceptor rewrites this into a tombstone so
    /// synced clients are told about the deletion instead of silently keeping the row.
    /// </summary>
    public async Task DeleteCarAsync(ClaimsPrincipal user, int id, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(user);

        var car = await context.Cars.FindAsync([id], cancellationToken);
        if (car is null)
        {
            return;
        }

        context.Cars.Remove(car);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Removes a track, converted to a tombstone by the interceptor.</summary>
    public async Task DeleteTrackAsync(ClaimsPrincipal user, int id, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(user);

        var track = await context.Tracks.FindAsync([id], cancellationToken);
        if (track is null)
        {
            return;
        }

        context.Tracks.Remove(track);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<T> ApplyFilter<T>(IQueryable<T> query, string? filter)
        => string.IsNullOrWhiteSpace(filter) ? query : query.Where(filter);

    private async Task EnsureAdminAsync(ClaimsPrincipal user)
    {
        var result = await authorization.AuthorizeAsync(user, AdminPolicy);
        if (!result.Succeeded)
        {
            throw new UnauthorizedAccessException("Catalogue mutations require the Admin role.");
        }
    }
}
