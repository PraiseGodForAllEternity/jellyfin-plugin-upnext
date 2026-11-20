using System;
using System.Linq;
using Jellyfin.Plugin.UpNext.Configuration;

namespace Jellyfin.Plugin.UpNext.Services;

/// <summary>
/// The settings that apply to one user after server defaults are filled in.
/// </summary>
/// <param name="Mode">Effective mode.</param>
/// <param name="CountdownSeconds">Effective countdown length.</param>
/// <param name="UsesDefaultMode">True when the user hasn't chosen a mode.</param>
/// <param name="UsesDefaultCountdown">True when the user hasn't chosen a countdown.</param>
public record EffectivePreferences(UpNextMode Mode, int CountdownSeconds, bool UsesDefaultMode, bool UsesDefaultCountdown);

/// <summary>
/// Reads and writes per-user preferences, stored in the plugin configuration.
/// </summary>
public class PreferenceStore
{
    private const int MinCountdown = 5;
    private const int MaxCountdown = 60;

    private readonly object _lock = new();

    /// <summary>
    /// Gets the server configuration, or defaults if the plugin isn't loaded yet.
    /// </summary>
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Clamps a countdown value to a sensible range.
    /// </summary>
    /// <param name="seconds">Requested seconds.</param>
    /// <returns>Clamped seconds.</returns>
    public static int ClampCountdown(int seconds) => Math.Clamp(seconds, MinCountdown, MaxCountdown);

    /// <summary>
    /// Gets the effective preferences for a user.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <returns>Effective preferences.</returns>
    public EffectivePreferences Get(Guid userId)
    {
        var config = Config;
        UserPreference? pref;
        lock (_lock)
        {
            pref = config.UserPreferences?.FirstOrDefault(p => p is not null && p.UserId.Equals(userId));
        }

        var mode = pref?.Mode ?? config.DefaultMode;
        if (!Enum.IsDefined(mode))
        {
            mode = UpNextMode.Autoplay;
        }
        var countdown = ClampCountdown(pref?.CountdownSeconds ?? config.DefaultCountdownSeconds);
        return new EffectivePreferences(mode, countdown, pref?.Mode is null, pref?.CountdownSeconds is null);
    }

    /// <summary>
    /// Saves a user's choices. Pass null to go back to the server default.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="mode">Mode, or null for default.</param>
    /// <param name="countdownSeconds">Countdown, or null for default.</param>
    public void Save(Guid userId, UpNextMode? mode, int? countdownSeconds)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        lock (_lock)
        {
            var list = plugin.Configuration.UserPreferences ??= new();
            list.RemoveAll(p => p is null || p.UserId.Equals(userId));
            if (mode is not null || countdownSeconds is not null)
            {
                list.Add(new UserPreference
                {
                    UserId = userId,
                    Mode = mode,
                    CountdownSeconds = countdownSeconds is null ? null : ClampCountdown(countdownSeconds.Value)
                });
            }

            plugin.SaveConfiguration();
        }
    }
}
