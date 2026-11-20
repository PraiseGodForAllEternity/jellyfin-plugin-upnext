using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
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

    /// <summary>Gets or sets the alternatives.</summary>
    public List<MovieCard> Suggestions { get; set; } = new();
}

/// <summary>
/// What the caller's device is playing according to the server.
/// </summary>
public class SessionStateResponse
{
    /// <summary>Gets or sets the playing item id.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a movie.</summary>
    public bool IsMovie { get; set; }

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
