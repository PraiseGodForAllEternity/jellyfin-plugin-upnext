using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.UpNext.Api;
using Jellyfin.Plugin.UpNext.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UpNext.Services;

/// <summary>
/// For apps that can't run the web overlay (Roku, Android TV, Swiftfin...), starts
/// the next movie from the server once a movie plays to the end.
/// </summary>
public sealed class RemoteAutoplayService : IHostedService, IDisposable
{
    // A stop this close to the end counts as "the movie finished", not "the viewer quit".
    private static readonly long EndToleranceTicks = TimeSpan.FromSeconds(30).Ticks;

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly NextMovieService _nextMovies;
    private readonly PreferenceStore _preferences;
    private readonly ClaimTracker _claims;
    private readonly ILogger<RemoteAutoplayService> _logger;
    private readonly CancellationTokenSource _stopping = new();

    // Per user and device: the movie the server last started on its own, and how many
    // it has started in a row. If that movie is the one that just ended, nobody picked
    // anything in between, so the chain continues.
    private readonly ConcurrentDictionary<(Guid UserId, string Device), (Guid ItemId, int Count)> _chains = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteAutoplayService"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="nextMovies">Next movie picker.</param>
    /// <param name="preferences">Preference store.</param>
    /// <param name="claims">Overlay claim tracker.</param>
    /// <param name="logger">Logger.</param>
    public RemoteAutoplayService(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        NextMovieService nextMovies,
        PreferenceStore preferences,
        ClaimTracker claims,
        ILogger<RemoteAutoplayService> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _nextMovies = nextMovies;
        _preferences = preferences;
        _claims = claims;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var config = PreferenceStore.Config;
            if (!config.EnableRemoteAutoplay || e.Item is not (Movie or Episode) || e.Session is null)
            {
                return;
            }

            var movie = e.Item;

            // The web overlay already took care of this one.
            if (_claims.IsClaimed(e.Session.UserId, e.Session.DeviceId, movie.Id))
            {
                return;
            }

            if (!ReachedEnd(e, movie.RunTimeTicks))
            {
                return;
            }

            var user = e.Users?.FirstOrDefault();
            if (user is null || _preferences.Get(user.Id).Mode != UpNextMode.Autoplay)
            {
                return;
            }

            var key = (user.Id, e.Session.DeviceId ?? e.Session.Id);
            var chain = _chains.TryGetValue(key, out var link) && link.ItemId.Equals(movie.Id) ? link.Count : 0;
            var limit = Math.Clamp(config.StillWatchingAfter, 0, 20);
            if (limit > 0 && chain >= limit)
            {
                // These apps can't ask "Are you still watching?", so just don't start another.
                _chains.TryRemove(key, out _);
                _logger.LogInformation(
                    "Up Next: {Count} titles autoplayed in a row on {Client}; waiting for someone to pick the next one",
                    chain,
                    e.ClientName);
                return;
            }

            var pick = movie is Episode episode
                ? _nextMovies.PickAfterEpisode(user, episode, 0)
                : _nextMovies.Pick(user, movie, 0);

            // A show that's still airing never rolls into something else on its own.
            if (pick is null || pick.Context == PickContext.CaughtUp)
            {
                return;
            }

            var sessionId = e.Session.Id;
            var delay = TimeSpan.FromSeconds(Math.Clamp(config.RemoteAutoplayDelaySeconds, 0, 120));
            _logger.LogInformation(
                "Up Next: {Movie} finished on {Client}; starting {Next} in {Delay}s",
                movie.Name,
                e.ClientName,
                pick.Next.Display.Name,
                delay.TotalSeconds);

            _ = Task.Run(() => PlayLater(sessionId, user, pick, delay, key, chain + 1), _stopping.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Up Next: error handling playback stop");
        }
    }

    private static bool ReachedEnd(PlaybackStopEventArgs e, long? runTimeTicks)
    {
        if (e.PlaybackPositionTicks.HasValue && runTimeTicks.GetValueOrDefault() > 0)
        {
            return runTimeTicks!.Value - e.PlaybackPositionTicks.Value <= EndToleranceTicks;
        }

        // Some apps don't report a position on stop; fall back to the server's own verdict.
        return e.PlayedToCompletion;
    }

    private async Task PlayLater(
        string sessionId,
        Jellyfin.Database.Implementations.Entities.User user,
        UpNextPick pick,
        TimeSpan delay,
        (Guid UserId, string Device) chainKey,
        int chainCount)
    {
        try
        {
            await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);

            var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session is null || !session.IsActive)
            {
                return;
            }

            // The viewer already picked something else.
            if (session.NowPlayingItem is not null)
            {
                return;
            }

            if (!session.SupportsRemoteControl)
            {
                _logger.LogInformation("Up Next: {Client} doesn't accept remote play commands; skipping", session.Client);
                return;
            }

            await UpNextController.SendPlay(_sessionManager, _userDataManager, session, user, pick.Next.Play, _stopping.Token).ConfigureAwait(false);

            if (_chains.Count > 1000)
            {
                _chains.Clear();
            }

            _chains[chainKey] = (pick.Next.Play.Id, chainCount);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Up Next: couldn't start {Next}", pick.Next.Display.Name);
        }
    }
}
