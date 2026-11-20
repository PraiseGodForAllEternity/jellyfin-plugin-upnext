using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.UpNext.Services;

/// <summary>
/// Remembers which devices are showing the web overlay for which movie, so the
/// server doesn't also start the next movie on them by itself.
/// </summary>
public class ClaimTracker
{
    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, (Guid ItemId, DateTime ClaimedAt)> _claims = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks a movie on a device as handled by the overlay.
    /// </summary>
    /// <param name="deviceId">Device id.</param>
    /// <param name="itemId">Movie id.</param>
    public void Claim(string deviceId, Guid itemId)
    {
        _claims[deviceId] = (itemId, DateTime.UtcNow);
    }

    /// <summary>
    /// Checks whether the overlay is handling this movie on this device.
    /// </summary>
    /// <param name="deviceId">Device id.</param>
    /// <param name="itemId">Movie id.</param>
    /// <returns>True if claimed recently.</returns>
    public bool IsClaimed(string? deviceId, Guid itemId)
    {
        if (string.IsNullOrEmpty(deviceId) || !_claims.TryGetValue(deviceId, out var claim))
        {
            return false;
        }

        return claim.ItemId.Equals(itemId) && DateTime.UtcNow - claim.ClaimedAt < ClaimLifetime;
    }
}
