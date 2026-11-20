using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.UpNext.Services;

/// <summary>
/// Remembers which devices are showing the web overlay for which movie, so the
/// server doesn't also start the next movie on them by itself.
/// </summary>
public class ClaimTracker
{
    private const int MaxDeviceIdLength = 256;
    private const int PruneThreshold = 2000;
    private const int HardLimit = 10000;
    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromHours(12);

    // Keyed by user too: device ids come from a client-supplied header, so one
    // user must not be able to affect another user's autoplay.
    private readonly ConcurrentDictionary<(Guid UserId, string DeviceId, Guid ItemId), DateTime> _claims = new();

    /// <summary>
    /// Marks a movie on a device as handled by the overlay.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="deviceId">Device id.</param>
    /// <param name="itemId">Movie id.</param>
    /// <returns>False if the device id was unusable.</returns>
    public bool Claim(Guid userId, string? deviceId, Guid itemId)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId.Length > MaxDeviceIdLength)
        {
            return false;
        }

        if (_claims.Count >= PruneThreshold)
        {
            Prune();
        }

        _claims[(userId, deviceId, itemId)] = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Checks whether the overlay is handling this movie on this device.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="deviceId">Device id.</param>
    /// <param name="itemId">Movie id.</param>
    /// <returns>True if claimed recently.</returns>
    public bool IsClaimed(Guid userId, string? deviceId, Guid itemId)
    {
        if (string.IsNullOrEmpty(deviceId) || !_claims.TryGetValue((userId, deviceId, itemId), out var claimedAt))
        {
            return false;
        }

        return DateTime.UtcNow - claimedAt < ClaimLifetime;
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - ClaimLifetime;
        foreach (var entry in _claims.Where(e => e.Value < cutoff).ToList())
        {
            _claims.TryRemove(entry.Key, out _);
        }

        // Someone is flooding us with made-up device ids; start over rather than grow forever.
        if (_claims.Count >= HardLimit)
        {
            _claims.Clear();
        }
    }
}
