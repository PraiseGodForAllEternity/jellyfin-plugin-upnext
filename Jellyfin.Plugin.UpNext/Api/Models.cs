using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.UpNext.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.UpNext.Api;

/// <summary>
/// A movie as shown on the Up Next card.
/// </summary>
public class MovieCard
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the runtime in ticks.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the parental rating.</summary>
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets the community rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets a value indicating whether the movie has a backdrop image.</summary>
    public bool HasBackdrop { get; set; }

    /// <summary>Gets or sets a value indicating whether the movie has a poster.</summary>
    public bool HasPrimary { get; set; }

    /// <summary>Gets or sets a value indicating whether the movie has a logo.</summary>
    public bool HasLogo { get; set; }

    /// <summary>Gets or sets the item that actually plays (a show's first episode; the movie itself otherwise).</summary>
    public Guid PlayId { get; set; }

    /// <summary>Gets or sets "Movie" or "Series".</summary>
    public string Kind { get; set; } = "Movie";

    /// <summary>Gets or sets an extra line, e.g. "Season 1, Episode 1: Pilot".</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the viewer's subtitle language when the title has it, e.g. "English".</summary>
    public string? SubtitlesReady { get; set; }

    /// <summary>
    /// Builds a card from a suggestion.
    /// </summary>
    /// <param name="suggestion">What to show and what to play.</param>
    /// <returns>The card.</returns>
    public static MovieCard From(Suggestion suggestion)
    {
        var card = From(suggestion.Display);
        card.PlayId = suggestion.Play.Id;
        if (suggestion.Display is Series)
        {
            card.Kind = "Series";
            card.RunTimeTicks = null;
            if (suggestion.Play is Episode episode)
            {
                card.Subtitle = string.Format(
                    CultureInfo.InvariantCulture,
                    "Season {0}, Episode {1}: {2}",
                    episode.ParentIndexNumber ?? 1,
                    episode.IndexNumber ?? 1,
                    episode.Name);
            }
        }

        return card;
    }

    /// <summary>
    /// Builds a card from a library item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The card.</returns>
    public static MovieCard From(BaseItem item)
    {
        return new MovieCard
        {
            Id = item.Id,
            PlayId = item.Id,
            Name = item.Name,
            Year = item.ProductionYear,
            Overview = item.Overview,
            RunTimeTicks = item.RunTimeTicks,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            HasBackdrop = item.HasImage(ImageType.Backdrop, 0),
            HasPrimary = item.HasImage(ImageType.Primary, 0),
            HasLogo = item.HasImage(ImageType.Logo, 0)
        };
    }
}

/// <summary>
/// Everything the overlay needs for the current movie.
/// </summary>
public class UpNextResponse
{
    /// <summary>Gets or sets the current movie's id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the effective mode for this user.</summary>
    public string Mode { get; set; } = "Autoplay";

    /// <summary>Gets or sets the countdown length.</summary>
    public int CountdownSeconds { get; set; }

    /// <summary>Gets or sets the current movie's runtime.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets where the credits start, if detected.</summary>
    public long? CreditsStartTicks { get; set; }

    /// <summary>Gets or sets how far before the end to show the card when no credits were detected.</summary>
    public int FallbackSecondsBeforeEnd { get; set; }

    /// <summary>Gets or sets how many autoplays in a row are allowed before asking "Are you still watching?" (0 = never ask).</summary>
    public int StillWatchingAfter { get; set; }

    /// <summary>Gets or sets the movie that will autoplay.</summary>
    public MovieCard? Next { get; set; }

    /// <summary>Gets or sets the collection the next movie comes from.</summary>
    public string? CollectionName { get; set; }

    /// <summary>Gets or sets why this was picked: Collection, Similar, SeriesFinale or CaughtUp.</summary>
    public string? Context { get; set; }

    /// <summary>Gets or sets the collection or show the pick relates to.</summary>
    public string? SourceName { get; set; }

    /// <summary>Gets or sets the alternatives.</summary>
    public List<MovieCard> Suggestions { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the ending title has a scene during or after its credits.</summary>
    public bool CreditsScene { get; set; }
}

/// <summary>
/// What the caller's device is playing according to the server.
/// </summary>
public class SessionStateResponse
{
    /// <summary>Gets or sets the playing item id.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a movie or an episode (the kinds Up Next looks at).</summary>
    public bool IsSupported { get; set; }

    /// <summary>Gets or sets the last reported position.</summary>
    public long? PositionTicks { get; set; }

    /// <summary>Gets or sets how old that position report is, in milliseconds.</summary>
    public double ReportAgeMs { get; set; }

    /// <summary>Gets or sets a value indicating whether playback is paused.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Gets or sets the runtime.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the viewer's own play queue has another
    /// item after this one (e.g. "Play All" on a collection). Up Next stays out of the way then.
    /// </summary>
    public bool QueueHasNext { get; set; }
}

/// <summary>
/// A user's Up Next settings.
/// </summary>
public class PreferencesDto
{
    /// <summary>Gets or sets the mode: Autoplay, SuggestionsOnly or Off. Null resets to the server default.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the countdown. Null resets to the server default.</summary>
    public int? CountdownSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether the mode is the server default (read only).</summary>
    public bool UsesDefaultMode { get; set; }

    /// <summary>Gets or sets a value indicating whether the countdown is the server default (read only).</summary>
    public bool UsesDefaultCountdown { get; set; }

    /// <summary>Gets or sets the server default mode (read only).</summary>
    public string? DefaultMode { get; set; }

    /// <summary>Gets or sets the server default countdown (read only).</summary>
    public int DefaultCountdownSeconds { get; set; }
}

/// <summary>
/// What Up Next sees for one item (admin "Check a movie" tool).
/// </summary>
public class DiagnoseResponse
{
    /// <summary>Gets or sets the title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the runtime.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets when the Up Next card would appear.</summary>
    public long? CreditsStartTicks { get; set; }

    /// <summary>Gets or sets where that time comes from.</summary>
    public string? CreditsSource { get; set; }

    /// <summary>Gets or sets the TMDb film series id in the movie's metadata, if any.</summary>
    public string? TmdbSeriesId { get; set; }

    /// <summary>Gets or sets every series found, in the order Up Next consults them.</summary>
    public List<DiagnoseSeries> Series { get; set; } = new();

    /// <summary>Gets or sets what would play next.</summary>
    public string? Next { get; set; }

    /// <summary>Gets or sets why (Collection, Similar, SeriesFinale, CaughtUp).</summary>
    public string? NextReason { get; set; }
}

/// <summary>
/// One film series found for a movie.
/// </summary>
public class DiagnoseSeries
{
    /// <summary>Gets or sets where it came from.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets its name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the films in play order.</summary>
    public List<DiagnoseFilm> Films { get; set; } = new();
}

/// <summary>
/// One film in a series.
/// </summary>
public class DiagnoseFilm
{
    /// <summary>Gets or sets the title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the release date (yyyy-MM-dd).</summary>
    public string? Released { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the movie being checked.</summary>
    public bool IsThis { get; set; }
}
