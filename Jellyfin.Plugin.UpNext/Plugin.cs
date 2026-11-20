using System;
using System.Collections.Generic;
using Jellyfin.Plugin.UpNext.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.UpNext;

/// <summary>
/// Up Next: plays the next movie in a collection, or a similar one, when a movie ends.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id. Must match the guid in manifest.json.
    /// </summary>
    public static readonly Guid PluginId = new("c592bf50-4d3e-427a-934c-5d9d38208e03");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the running plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Up Next";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Netflix-style Up Next for movies: plays the next film in a collection, or a similar unwatched movie, when the credits roll.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "UpNext",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
            }
        };
    }
}
