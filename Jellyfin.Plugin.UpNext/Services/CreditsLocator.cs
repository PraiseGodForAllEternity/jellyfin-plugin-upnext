using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UpNext.Services;

/// <summary>
/// Where the credits start, and how we know.
/// </summary>
/// <param name="StartTicks">Credits start, or null if unknown.</param>
/// <param name="Source">"Media segments" (Intro Skipper and similar), "Chapter: name", or null.</param>
public record CreditsInfo(long? StartTicks, string? Source);

/// <summary>
/// Finds where an item's end credits begin: first from media segments (Intro Skipper),
/// then from chapter names like "End Credits".
/// </summary>
public partial class CreditsLocator
{
    private readonly IMediaSegmentManager _segmentManager;
    private readonly IChapterManager _chapterManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CreditsLocator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CreditsLocator"/> class.
    /// </summary>
    /// <param name="segmentManager">Media segment manager.</param>
    /// <param name="chapterManager">Chapter manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public CreditsLocator(
        IMediaSegmentManager segmentManager,
        IChapterManager chapterManager,
        ILibraryManager libraryManager,
        ILogger<CreditsLocator> logger)
    {
        _segmentManager = segmentManager;
        _chapterManager = chapterManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Finds the start of the end credits.
    /// </summary>
    /// <param name="item">Movie or episode.</param>
    /// <returns>Where the credits start, if known.</returns>
    public async Task<CreditsInfo> Find(BaseItem item)
    {
        // Ignore anything in the first half: that's a mislabeled segment or opening credits.
        var half = (item.RunTimeTicks ?? 0) / 2;

        try
        {
            var segments = await _segmentManager.GetSegmentsAsync(
                item,
                new[] { MediaSegmentType.Outro },
                _libraryManager.GetLibraryOptions(item)).ConfigureAwait(false);

            var start = segments
                .Where(s => s.Type == MediaSegmentType.Outro && s.StartTicks > half)
                .OrderBy(s => s.StartTicks)
                .Select(s => (long?)s.StartTicks)
                .FirstOrDefault();
            if (start is not null)
            {
                return new CreditsInfo(start, "Media segments (Intro Skipper)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Up Next: couldn't read media segments for {Name}", item.Name);
        }

        try
        {
            var chapter = _chapterManager.GetChapters(item.Id)
                .Where(c => c.StartPositionTicks > half && !string.IsNullOrWhiteSpace(c.Name) && CreditsChapter().IsMatch(c.Name))
                .OrderBy(c => c.StartPositionTicks)
                .FirstOrDefault();
            if (chapter is not null)
            {
                return new CreditsInfo(chapter.StartPositionTicks, "Chapter: " + chapter.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Up Next: couldn't read chapters for {Name}", item.Name);
        }

        return new CreditsInfo(null, null);
    }

    // "Credits", "End Credits", "Closing Credits", "End Titles", "Outro", "Ending",
    // but not "Opening Credits".
    [GeneratedRegex(@"^(?!.*\bopening\b).*\b(credits?|end\s*titles?|outro|ending)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditsChapter();
}
