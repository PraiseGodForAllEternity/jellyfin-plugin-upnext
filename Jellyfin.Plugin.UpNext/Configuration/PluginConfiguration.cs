using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UpNext.Configuration;

/// <summary>
/// What happens when a movie reaches its credits.
/// </summary>
public enum UpNextMode
{
    /// <summary>Show the Up Next card and play the next movie when the countdown ends.</summary>
    Autoplay = 0,

    /// <summary>Show the Up Next card, but never start anything automatically.</summary>
    SuggestionsOnly = 1,

    /// <summary>Do nothing.</summary>
    Off = 2
}

/// <summary>
/// Server-wide settings. Users can override mode and countdown for themselves.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the default mode for users who haven't picked one.</summary>
    public UpNextMode DefaultMode { get; set; } = UpNextMode.Autoplay;

    /// <summary>Gets or sets the default countdown length in seconds.</summary>
    public int DefaultCountdownSeconds { get; set; } = 20;

    /// <summary>
    /// Gets or sets how many seconds before the end the card appears when
    /// a movie has no detected credits segment.
    /// </summary>
    public int FallbackSecondsBeforeEnd { get; set; } = 180;

    /// <summary>Gets or sets how many other movies to offer on the card.</summary>
    public int SuggestionCount { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether the in-browser overlay is injected into the web client.</summary>
    public bool EnableWebOverlay { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server starts the next movie on
    /// apps that can't show the overlay (Roku, Android TV, etc.).
    /// </summary>
    public bool EnableRemoteAutoplay { get; set; } = true;

    /// <summary>Gets or sets the delay, in seconds, before the server starts the next movie on those apps.</summary>
    public int RemoteAutoplayDelaySeconds { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether movies the user has already watched are skipped when picking the next film in a collection.</summary>
    public bool SkipWatchedInCollections { get; set; }

    /// <summary>Gets or sets the per-user overrides.</summary>
    public List<UserPreference> UserPreferences { get; set; } = new();
}

/// <summary>
/// A user's own choices. Null means "use the server default".
/// </summary>
public class UserPreference
{
    /// <summary>Gets or sets the user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the mode override.</summary>
    public UpNextMode? Mode { get; set; }

    /// <summary>Gets or sets the countdown override in seconds.</summary>
    public int? CountdownSeconds { get; set; }
}
