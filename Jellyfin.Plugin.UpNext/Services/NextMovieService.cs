using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UpNext.Services;

/// <summary>
/// The movie to play next plus a few alternatives.
/// </summary>
/// <param name="Next">The movie that will autoplay.</param>
/// <param name="CollectionName">The collection the pick came from, or null if it's a similar-movie pick.</param>
/// <param name="Suggestions">Other movies to offer.</param>
public record UpNextPick(BaseItem Next, string? CollectionName, IReadOnlyList<BaseItem> Suggestions);

/// <summary>
/// Decides what plays after a movie.
/// </summary>
public class NextMovieService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<NextMovieService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextMovieService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="logger">Logger.</param>
    public NextMovieService(ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger<NextMovieService> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Picks what plays after <paramref name="current"/>.
    /// </summary>
    /// <param name="user">The viewer.</param>
    /// <param name="current">The movie that's ending.</param>
    /// <param name="suggestionCount">How many alternatives to include.</param>
    /// <returns>The pick, or null if there's nothing to play.</returns>
    public UpNextPick? Pick(User user, BaseItem current, int suggestionCount)
    {
        if (current is not Movie)
        {
            return null;
        }

        var config = PreferenceStore.Config;
        BaseItem? next = null;
        string? collectionName = null;

        try
        {
            foreach (var boxSet in FindCollections(user, current))
            {
                var films = OrderedFilms(user, boxSet);
                var index = films.FindIndex(m => m.Id.Equals(current.Id));
                if (index < 0)
                {
                    continue;
                }

                next = films.Skip(index + 1).FirstOrDefault(m => !config.SkipWatchedInCollections || !IsPlayed(user, m));
                if (next is not null)
                {
                    collectionName = boxSet.Name;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // A broken collection shouldn't stop the similar-movie fallback.
            _logger.LogWarning(ex, "Up Next: couldn't read collections for {Name}", current.Name);
        }

        var exclude = new HashSet<Guid> { current.Id };
        if (next is not null)
        {
            exclude.Add(next.Id);
        }

        var similar = FindSimilar(user, current, exclude, suggestionCount + 1);
        if (next is null)
        {
            next = similar.FirstOrDefault();
            similar = similar.Skip(1).ToList();
        }

        if (next is null)
        {
            return null;
        }

        return new UpNextPick(next, collectionName, similar.Take(Math.Max(0, suggestionCount)).ToList());
    }

    private IEnumerable<BoxSet> FindCollections(User user, BaseItem current)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        };

        return _libraryManager.GetItemList(query)
            .OfType<BoxSet>()
            .Where(b => b.LinkedChildren.Any(c => c.ItemId.HasValue && c.ItemId.Value.Equals(current.Id)))
            // Prefer smaller collections: a trilogy beats a 40-film "Marvel" list.
            .OrderBy(b => b.LinkedChildren.Length);
    }

    private static List<BaseItem> OrderedFilms(User user, BoxSet boxSet)
    {
        return boxSet.GetLinkedChildren()
            .OfType<Movie>()
            .Where(m => m.IsVisible(user))
            .GroupBy(m => m.Id)
            .Select(g => (BaseItem)g.First())
            .OrderBy(ReleaseDate)
            .ThenBy(m => m.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateTime ReleaseDate(BaseItem item)
    {
        if (item.PremiereDate.HasValue)
        {
            return item.PremiereDate.Value;
        }

        return item.ProductionYear.HasValue ? new DateTime(item.ProductionYear.Value, 1, 1) : DateTime.MaxValue;
    }

    private List<BaseItem> FindSimilar(User user, BaseItem current, HashSet<Guid> exclude, int count)
    {
        var candidates = new List<BaseItem>();
        if (current.Genres.Length > 0)
        {
            candidates.AddRange(QueryUnwatched(user, exclude, 80, current.Genres));
        }

        var picked = candidates
            .Where(c => !exclude.Contains(c.Id))
            .OrderByDescending(c => Similarity(current, c))
            .Take(count)
            .ToList();

        if (picked.Count < count)
        {
            // Not enough genre matches: top up with any unwatched movie.
            var skip = new HashSet<Guid>(exclude);
            skip.UnionWith(picked.Select(p => p.Id));
            picked.AddRange(QueryUnwatched(user, skip, count - picked.Count, null));
        }

        return picked;
    }

    private IEnumerable<BaseItem> QueryUnwatched(User user, HashSet<Guid> exclude, int limit, IReadOnlyList<string>? genres)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsPlayed = false,
            ExcludeItemIds = exclude.ToArray(),
            Limit = limit,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(false)
        };

        if (genres is not null)
        {
            query.Genres = genres;
        }

        return _libraryManager.GetItemList(query);
    }

    private static double Similarity(BaseItem a, BaseItem b)
    {
        double score = 0;
        score += 2.0 * a.Genres.Intersect(b.Genres, StringComparer.OrdinalIgnoreCase).Count();
        score += 1.5 * a.Studios.Intersect(b.Studios, StringComparer.OrdinalIgnoreCase).Count();
        score += 0.5 * a.Tags.Intersect(b.Tags, StringComparer.OrdinalIgnoreCase).Count();

        if (a.ProductionYear.HasValue && b.ProductionYear.HasValue)
        {
            // Movies from the same era feel closer; fades out over ~20 years.
            score += Math.Max(0, 2.0 - (Math.Abs(a.ProductionYear.Value - b.ProductionYear.Value) / 10.0));
        }

        if (string.Equals(a.OfficialRating, b.OfficialRating, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.5;
        }

        score += (b.CommunityRating ?? 0) / 10.0;
        return score;
    }

    private bool IsPlayed(User user, BaseItem item)
    {
        return _userDataManager.GetUserData(user, item)?.Played ?? false;
    }
}
