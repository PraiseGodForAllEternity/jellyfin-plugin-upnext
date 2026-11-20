using System;
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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
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
    private readonly IMediaSegmentManager _segmentManager;
    private readonly NextMovieService _nextMovies;
    private readonly PreferenceStore _preferences;
    private readonly ClaimTracker _claims;
    private readonly ILogger<UpNextController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpNextController"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="authContext">Authorization context.</param>
    /// <param name="segmentManager">Media segment manager.</param>
    /// <param name="nextMovies">Next movie picker.</param>
    /// <param name="preferences">Preference store.</param>
    /// <param name="claims">Overlay claim tracker.</param>
    /// <param name="logger">Logger.</param>
    public UpNextController(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IAuthorizationContext authContext,
        IMediaSegmentManager segmentManager,
        NextMovieService nextMovies,
        PreferenceStore preferences,
        ClaimTracker claims,
        ILogger<UpNextController> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _authContext = authContext;
        _segmentManager = segmentManager;
        _nextMovies = nextMovies;
        _preferences = preferences;
        _claims = claims;
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
            IsMovie = playing.Type == BaseItemKind.Movie,
            PositionTicks = session.PlayState?.PositionTicks,
            ReportAgeMs = Math.Max(0, (DateTime.UtcNow - session.LastPlaybackCheckIn).TotalMilliseconds),
            IsPaused = session.PlayState?.IsPaused ?? false,
            RunTimeTicks = playing.RunTimeTicks,
            QueueHasNext = QueueHasNext(session, playing.Id)
        };
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
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !item.IsVisible(user))
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
            FallbackSecondsBeforeEnd = Math.Max(10, config.FallbackSecondsBeforeEnd)
        };

        if (item is not Movie || prefs.Mode == UpNextMode.Off)
        {
            return response;
        }

        response.CreditsStartTicks = await GetCreditsStart(item).ConfigureAwait(false);

        var pick = _nextMovies.Pick(user, item, Math.Clamp(config.SuggestionCount, 0, 6));
        if (pick is not null)
        {
            response.Next = MovieCard.From(pick.Next);
            response.CollectionName = pick.CollectionName;
            response.Suggestions = pick.Suggestions.Select(MovieCard.From).ToList();
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
        if (string.IsNullOrEmpty(auth.DeviceId))
        {
            return BadRequest();
        }

        _claims.Claim(auth.DeviceId, itemId);
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

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        await SendPlay(_sessionManager, _userDataManager, session, user, item, cancellationToken).ConfigureAwait(false);
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

        await _sessionManager.SendPlaystateCommand(
            session.Id,
            session.Id,
            new PlaystateRequest { Command = PlaystateCommand.Stop, ControllingUserId = user.Id.ToString("N") },
            cancellationToken).ConfigureAwait(false);
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
            return Unauthorized();
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
            return Unauthorized();
        }

        UpNextMode? mode = null;
        if (!string.IsNullOrEmpty(body.Mode))
        {
            if (!Enum.TryParse<UpNextMode>(body.Mode, true, out var parsed))
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

    private async Task<long?> GetCreditsStart(BaseItem item)
    {
        try
        {
            var segments = await _segmentManager.GetSegmentsAsync(
                item,
                new[] { MediaSegmentType.Outro },
                _libraryManager.GetLibraryOptions(item)).ConfigureAwait(false);

            // Ignore anything in the first half; that's a mislabeled segment, not end credits.
            var half = (item.RunTimeTicks ?? 0) / 2;
            return segments
                .Where(s => s.Type == MediaSegmentType.Outro && s.StartTicks > half)
                .OrderBy(s => s.StartTicks)
                .Select(s => (long?)s.StartTicks)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Up Next: no credits segment for {Name}", item.Name);
            return null;
        }
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
