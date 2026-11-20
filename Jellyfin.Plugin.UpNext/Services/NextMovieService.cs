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

        // Other parts of the same series make poor "more like this" picks
        // (nobody wants Part Three suggested right after Part One).
        var exclude = new HashSet<Guid> { current.Id };
        var boxSets = LoadCollections(user);

        try
        {
            foreach (var boxSet in FindCollections(boxSets, current))
            {
                var films = OrderedFilms(user, boxSet);
                var index = films.FindIndex(m => m.Id.Equals(current.Id));
                if (index < 0)
                {
                    continue;
                }

                foreach (var film in films)
                {
                    exclude.Add(film.Id);
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

        if (next is not null)
        {
            exclude.Add(next.Id);
        }

        var similar = FindSimilar(user, current, exclude, suggestionCount + 1, boxSets);
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

    private List<BoxSet> LoadCollections(User user)
    {
        try
        {
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            };

            return _libraryManager.GetItemList(query).OfType<BoxSet>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Up Next: couldn't list collections");
            return new List<BoxSet>();
        }
    }

    private static IEnumerable<BoxSet> FindCollections(IReadOnlyList<BoxSet> boxSets, BaseItem movie)
    {
        return boxSets
            .Where(b => (b.LinkedChildren ?? Array.Empty<LinkedChild>()).Any(c => Links(c, movie)))
            // Prefer smaller collections: a trilogy beats a 40-film "Marvel" list.
            .OrderBy(b => b.LinkedChildren?.Length ?? 0);
    }

    /// <summary>
    /// For a movie that's part of a series, the part the viewer should actually start
    /// with: the earliest one they haven't watched. Standalone movies come back unchanged.
    /// </summary>
    private BaseItem SeriesEntryPoint(User user, BaseItem movie, IReadOnlyList<BoxSet> boxSets, Dictionary<Guid, List<BaseItem>> filmsCache)
    {
        try
        {
            var boxSet = FindCollections(boxSets, movie).FirstOrDefault();
            if (boxSet is null)
            {
                return movie;
            }

            if (!filmsCache.TryGetValue(boxSet.Id, out var films))
            {
                films = OrderedFilms(user, boxSet);
                filmsCache[boxSet.Id] = films;
            }

            return films.FirstOrDefault(f => !IsPlayed(user, f)) ?? movie;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Up Next: couldn't check the series for {Name}", movie.Name);
            return movie;
        }
    }

    // Collections store their members by item id, library id or file path,
    // depending on how they were created; check all three.
    private static bool Links(LinkedChild? child, BaseItem item)
    {
        if (child is null)
        {
            return false;
        }

        if (child.ItemId.HasValue && child.ItemId.Value.Equals(item.Id))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(child.LibraryItemId)
            && Guid.TryParse(child.LibraryItemId, out var libraryId)
            && libraryId.Equals(item.Id))
        {
            return true;
        }

        return !string.IsNullOrEmpty(child.Path)
            && !string.IsNullOrEmpty(item.Path)
            && string.Equals(child.Path, item.Path, StringComparison.OrdinalIgnoreCase);
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

    private List<BaseItem> FindSimilar(User user, BaseItem current, HashSet<Guid> exclude, int count, IReadOnlyList<BoxSet> boxSets)
    {
        var picked = new List<BaseItem>();
        if (count <= 0)
        {
            return picked;
        }

        var seen = new HashSet<Guid>(exclude);
        var filmsCache = new Dictionary<Guid, List<BaseItem>>();

        // Suggest a series from its first unwatched part, never from the middle.
        void Offer(BaseItem candidate)
        {
            var entry = SeriesEntryPoint(user, candidate, boxSets, filmsCache);
            var isNew = seen.Add(entry.Id);
            seen.Add(candidate.Id);
            if (isNew)
            {
                picked.Add(entry);
            }
        }

        if (current.Genres.Length > 0)
        {
            var ranked = QueryUnwatched(user, exclude, 80, current.Genres)
                .Where(c => !exclude.Contains(c.Id))
                .OrderByDescending(c => Similarity(current, c));
            foreach (var candidate in ranked)
            {
                if (picked.Count >= count)
                {
                    break;
                }

                Offer(candidate);
            }
        }

        if (picked.Count < count)
        {
            // Not enough genre matches: top up with any unwatched movie.
            foreach (var candidate in QueryUnwatched(user, seen, (count - picked.Count) * 3, null))
            {
                if (picked.Count >= count)
                {
                    break;
                }

                Offer(candidate);
            }
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
