using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.UpNext.Configuration;
using Jellyfin.Plugin.UpNext.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UpNext.Api;

/// <summary>
/// Endpoints used by the Up Next web overlay.
/// </summary>
[ApiController]
[Route("UpNext")]
public class UpNextController : ControllerBase
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IAuthorizationContext _authContext;
    private readonly CreditsLocator _credits;
    private readonly NextMovieService _nextMovies;
    private readonly PreferenceStore _preferences;
    private readonly ClaimTracker _claims;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<UpNextController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpNextController"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="authContext">Authorization context.</param>
    /// <param name="credits">Credits finder.</param>
    /// <param name="nextMovies">Next movie picker.</param>
    /// <param name="preferences">Preference store.</param>
    /// <param name="claims">Overlay claim tracker.</param>
    /// <param name="mediaSources">Media source manager.</param>
    /// <param name="localization">Localization manager.</param>
    /// <param name="logger">Logger.</param>
    public UpNextController(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IAuthorizationContext authContext,
        CreditsLocator credits,
        NextMovieService nextMovies,
        PreferenceStore preferences,
        ClaimTracker claims,
        IMediaSourceManager mediaSources,
        ILocalizationManager localization,
        ILogger<UpNextController> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _authContext = authContext;
        _credits = credits;
        _nextMovies = nextMovies;
        _preferences = preferences;
        _claims = claims;
        _mediaSources = mediaSources;
        _localization = localization;
        _logger = logger;
    }

    /// <summary>
    /// Serves the overlay script. Loaded by the web client before login, so it's anonymous.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetClientScript()
    {
        if (!PreferenceStore.Config.EnableWebOverlay)
        {
            return Content("/* Up Next overlay is turned off */", "application/javascript");
        }

        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.upnext.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Gets what the calling device is playing.
    /// </summary>
    /// <returns>Session state.</returns>
    [HttpGet("Session")]
    [Authorize]
    public async Task<ActionResult<SessionStateResponse>> GetSessionState()
    {
        var (session, _) = await GetCallerSession().ConfigureAwait(false);
        var playing = session?.NowPlayingItem;
        if (session is null || playing is null)
        {
            return new SessionStateResponse();
        }

        return new SessionStateResponse
        {
            ItemId = playing.Id,
            IsSupported = playing.Type == BaseItemKind.Movie || playing.Type == BaseItemKind.Episode,
            PositionTicks = session.PlayState?.PositionTicks,
            ReportAgeMs = ReportAge(session.LastPlaybackCheckIn),
            IsPaused = session.PlayState?.IsPaused ?? false,
            RunTimeTicks = playing.RunTimeTicks,
            QueueHasNext = QueueHasNext(session, playing.Id)
        };
    }

    // How stale the last position report is. "Never reported" must not read as
    // "decades old", or the overlay would think the movie is already over.
    private static double ReportAge(DateTime lastCheckIn)
    {
        if (lastCheckIn == default)
        {
            return 0;
        }

        return Math.Clamp((DateTime.UtcNow - lastCheckIn).TotalMilliseconds, 0, 30000);
    }

    private static bool QueueHasNext(SessionInfo session, Guid playingId)
    {
        var queue = session.NowPlayingQueue;
        if (queue is null || queue.Count < 2)
        {
            return false;
        }

        var index = -1;
        for (var i = 0; i < queue.Count; i++)
        {
            if (queue[i].Id.Equals(playingId))
            {
                index = i;
            }
        }

        return index >= 0 && index < queue.Count - 1;
    }

    /// <summary>
    /// Gets what should play after a movie, plus where its credits start.
    /// </summary>
    /// <param name="itemId">The movie that's playing.</param>
    /// <returns>The Up Next details.</returns>
    [HttpGet("Next/{itemId}")]
    [Authorize]
    public async Task<ActionResult<UpNextResponse>> GetNext([FromRoute] Guid itemId)
    {
        var user = await GetCallerUser().ConfigureAwait(false);
        if (user is null)
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(user))
        {
            return NotFound();
        }

        var config = PreferenceStore.Config;
        var prefs = _preferences.Get(user.Id);
        var response = new UpNextResponse
        {
            ItemId = item.Id,
            Mode = prefs.Mode.ToString(),
            CountdownSeconds = prefs.CountdownSeconds,
            RunTimeTicks = item.RunTimeTicks,
            FallbackSecondsBeforeEnd = Math.Max(10, config.FallbackSecondsBeforeEnd),
            StillWatchingAfter = Math.Clamp(config.StillWatchingAfter, 0, 20)
        };

        if (prefs.Mode == UpNextMode.Off)
        {
            return response;
        }

        var count = Math.Clamp(config.SuggestionCount, 0, 6);
        var pick = item switch
        {
            Movie => _nextMovies.Pick(user, item, count),
            Episode episode => _nextMovies.PickAfterEpisode(user, episode, count),
            _ => null
        };

        if (pick is null)
        {
            // Includes every episode except a show's last one: Jellyfin handles those itself.
            return response;
        }

        response.CreditsStartTicks = await GetCreditsStart(item).ConfigureAwait(false);
        response.CreditsScene = HasCreditsScene(item);
        response.Next = MovieCard.From(pick.Next);
        response.Next.SubtitlesReady = SubtitlesReady(user, pick.Next.Play);
        response.Context = pick.Context.ToString();
        response.SourceName = pick.SourceName;
        response.CollectionName = pick.Context == PickContext.Collection ? pick.SourceName : null;
        response.Suggestions = pick.Suggestions.Select(s => MovieCard.From(s)).ToList();

        // Caught up on a show that's still airing: offer, but never start something on our own.
        if (pick.Context == PickContext.CaughtUp && prefs.Mode == UpNextMode.Autoplay)
        {
            response.Mode = UpNextMode.SuggestionsOnly.ToString();
        }

        return response;
    }

    /// <summary>
    /// Tells the server the overlay is handling this movie on the calling device.
    /// </summary>
    /// <param name="itemId">The movie.</param>
    /// <returns>No content.</returns>
    [HttpPost("Claim/{itemId}")]
    [Authorize]
    public async Task<ActionResult> Claim([FromRoute] Guid itemId)
    {
        var auth = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (auth.UserId.Equals(Guid.Empty) || !_claims.Claim(auth.UserId, auth.DeviceId, itemId))
        {
            return BadRequest();
        }

        return NoContent();
    }

    /// <summary>
    /// Starts a movie on the calling device.
    /// </summary>
    /// <param name="itemId">The movie to play.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Play/{itemId}")]
    [Authorize]
    public async Task<ActionResult> Play([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var (session, user) = await GetCallerSession().ConfigureAwait(false);
        if (session is null || user is null)
        {
            return NotFound();
        }

        // Movies and episodes only: this endpoint exists for Up Next, not as a general remote control.
        var item = _libraryManager.GetItemById(itemId);
        if (item is not (Movie or Episode) || !item.IsVisibleStandalone(user))
        {
            return NotFound();
        }

        try
        {
            await SendPlay(_sessionManager, _userDataManager, session, user, item, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Thrown when the user isn't allowed to play this item.
            return Forbid();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Up Next: couldn't start {Name} on {Client}", item.Name, session.Client);
            return Conflict();
        }

        return NoContent();
    }

    /// <summary>
    /// Stops playback on the calling device (the "Back to Browse" button).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Stop")]
    [Authorize]
    public async Task<ActionResult> Stop(CancellationToken cancellationToken)
    {
        var (session, user) = await GetCallerSession().ConfigureAwait(false);
        if (session is null || user is null)
        {
            return NotFound();
        }

        try
        {
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop, ControllingUserId = user.Id.ToString("N") },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Up Next: couldn't stop playback on {Client}", session.Client);
            return Conflict();
        }

        return NoContent();
    }

    /// <summary>
    /// Gets the caller's Up Next settings.
    /// </summary>
    /// <returns>Settings.</returns>
    [HttpGet("Preferences")]
    [Authorize]
    public async Task<ActionResult<PreferencesDto>> GetPreferences()
    {
        var user = await GetCallerUser().ConfigureAwait(false);
        if (user is null)
        {
            return NotFound();
        }

        return ToDto(_preferences.Get(user.Id));
    }

    /// <summary>
    /// Saves the caller's Up Next settings.
    /// </summary>
    /// <param name="body">New settings. Null fields go back to the server default.</param>
    /// <returns>The saved settings.</returns>
    [HttpPost("Preferences")]
    [Authorize]
    public async Task<ActionResult<PreferencesDto>> SavePreferences([FromBody] PreferencesDto body)
    {
        var user = await GetCallerUser().ConfigureAwait(false);
        if (user is null)
        {
            return NotFound();
        }

        UpNextMode? mode = null;
        if (!string.IsNullOrEmpty(body.Mode))
        {
            // TryParse also accepts any number ("42"), so check it's a real mode.
            if (!Enum.TryParse<UpNextMode>(body.Mode, true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return BadRequest("Unknown mode.");
            }

            mode = parsed;
        }

        _preferences.Save(user.Id, mode, body.CountdownSeconds);
        return ToDto(_preferences.Get(user.Id));
    }

    /// <summary>
    /// Sends a "play now" command for <paramref name="item"/>, resuming if it was partly watched.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="session">Target session.</param>
    /// <param name="user">The viewer.</param>
    /// <param name="item">The movie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    internal static Task SendPlay(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        SessionInfo session,
        User user,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        long? start = null;
        var data = userDataManager.GetUserData(user, item);
        if (data is not null && !data.Played && data.PlaybackPositionTicks > 0)
        {
            start = data.PlaybackPositionTicks;
        }

        return sessionManager.SendPlayCommand(
            session.Id,
            session.Id,
            new PlayRequest
            {
                ItemIds = new[] { item.Id },
                PlayCommand = PlayCommand.PlayNow,
                StartPositionTicks = start,
                ControllingUserId = user.Id
            },
            cancellationToken);
    }

    private static PreferencesDto ToDto(EffectivePreferences prefs)
    {
        var config = PreferenceStore.Config;
        return new PreferencesDto
        {
            Mode = prefs.Mode.ToString(),
            CountdownSeconds = prefs.CountdownSeconds,
            UsesDefaultMode = prefs.UsesDefaultMode,
            UsesDefaultCountdown = prefs.UsesDefaultCountdown,
            DefaultMode = config.DefaultMode.ToString(),
            DefaultCountdownSeconds = PreferenceStore.ClampCountdown(config.DefaultCountdownSeconds)
        };
    }

    /// <summary>
    /// Admin tool: shows what Up Next sees for an item (series found, play order, what's
    /// next, and where the credits timing comes from), as the calling admin.
    /// </summary>
    /// <param name="itemId">Movie or episode.</param>
    /// <returns>Plain-language diagnosis.</returns>
    [HttpGet("Diagnose/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<DiagnoseResponse>> Diagnose([FromRoute] Guid itemId)
    {
        var user = await GetCallerUser().ConfigureAwait(false);
        if (user is null)
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not (Movie or Episode) || !item.IsVisibleStandalone(user))
        {
            return NotFound();
        }

        var config = PreferenceStore.Config;
        var credits = await _credits.Find(item).ConfigureAwait(false);
        var runtime = item.RunTimeTicks ?? 0;
        var fallback = Math.Max(runtime / 2, runtime - TimeSpan.FromSeconds(Math.Max(10, config.FallbackSecondsBeforeEnd)).Ticks);

        var response = new DiagnoseResponse
        {
            Name = item.Name,
            Year = item.ProductionYear,
            RunTimeTicks = item.RunTimeTicks,
            CreditsStartTicks = credits.StartTicks ?? fallback,
            CreditsSource = credits.Source ?? $"Not detected; using the fallback ({Math.Max(10, config.FallbackSecondsBeforeEnd)} seconds before the end)"
        };

        if (item is Movie)
        {
            var (series, tmdbId) = _nextMovies.Explain(user, item);
            response.TmdbSeriesId = tmdbId;
            foreach (var (source, name, films) in series)
            {
                response.Series.Add(new DiagnoseSeries
                {
                    Source = source,
                    Name = name,
                    Films = films.Select(f => new DiagnoseFilm
                    {
                        Name = f.Name,
                        Year = f.ProductionYear,
                        Released = f.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        IsThis = f.Id.Equals(item.Id)
                    }).ToList()
                });
            }
        }

        var pick = item switch
        {
            Movie => _nextMovies.Pick(user, item, 0),
            Episode episode => _nextMovies.PickAfterEpisode(user, episode, 0),
            _ => null
        };
        response.Next = pick?.Next.Display.Name;
        response.NextReason = pick?.Context.ToString();
        return response;
    }

    // The viewer's own subtitle language, if the title has full (not forced-only) subtitles in it.
    private string? SubtitlesReady(User user, BaseItem item)
    {
        var language = user.SubtitleLanguagePreference?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(language) || user.SubtitleMode is SubtitlePlaybackMode.None or SubtitlePlaybackMode.OnlyForced)
        {
            return null;
        }

        try
        {
            var wanted = _localization.FindLanguageInfo(language);
            if (wanted is null)
            {
                return null;
            }

            var has = _mediaSources.GetMediaStreams(item.Id).Any(s =>
                s.Type == MediaStreamType.Subtitle
                && !s.IsForced
                && !string.IsNullOrEmpty(s.Language)
                && _localization.FindLanguageInfo(s.Language) is { } found
                && found.ThreeLetterISOLanguageNames.Intersect(wanted.ThreeLetterISOLanguageNames, StringComparer.OrdinalIgnoreCase).Any());
            return has ? wanted.DisplayName : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Up Next: couldn't check subtitles for {Name}", item.Name);
            return null;
        }
    }

    // TMDb keywords, which Jellyfin saves as tags unless its TMDb "exclude tags" option is on.
    private static readonly string[] CreditsSceneTags = { "aftercreditsstinger", "duringcreditsstinger" };

    private static bool HasCreditsScene(BaseItem item)
        => (item.Tags ?? Array.Empty<string>()).Any(tag => CreditsSceneTags.Any(known => string.Equals(tag?.Replace(" ", string.Empty, StringComparison.Ordinal), known, StringComparison.OrdinalIgnoreCase)));

    private async Task<long?> GetCreditsStart(BaseItem item)
    {
        return (await _credits.Find(item).ConfigureAwait(false)).StartTicks;
    }

    private async Task<User?> GetCallerUser()
    {
        var auth = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return auth.UserId.Equals(Guid.Empty) ? null : _userManager.GetUserById(auth.UserId);
    }

    private async Task<(SessionInfo? Session, User? User)> GetCallerSession()
    {
        var auth = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (auth.UserId.Equals(Guid.Empty) || string.IsNullOrEmpty(auth.DeviceId))
        {
            return (null, null);
        }

        var user = _userManager.GetUserById(auth.UserId);
        var session = _sessionManager.Sessions
            .Where(s => string.Equals(s.DeviceId, auth.DeviceId, StringComparison.Ordinal) && s.UserId.Equals(auth.UserId))
            .OrderByDescending(s => s.LastActivityDate)
            .FirstOrDefault();
        return (session, user);
    }
}
