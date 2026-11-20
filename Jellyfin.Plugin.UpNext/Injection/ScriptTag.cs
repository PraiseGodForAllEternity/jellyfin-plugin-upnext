using System;

namespace Jellyfin.Plugin.UpNext.Injection;

/// <summary>
/// Adds the overlay's script tag to the web client's index.html.
/// </summary>
public static class ScriptTag
{
    private const string Marker = "UpNext/ClientScript";

    /// <summary>
    /// Inserts the script tag before &lt;/body&gt;, once.
    /// </summary>
    /// <param name="html">Original index.html.</param>
    /// <returns>Patched html.</returns>
    public static string Inject(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return html ?? string.Empty;
        }

        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return html;
        }

        // index.html lives at {base}/web/, so ../ reaches the API root under any base URL.
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1";
        var tag = $"<script defer src=\"../{Marker}?v={version}\"></script>";
        return html.Insert(end, tag);
    }

    /// <summary>
    /// Shape of the object the File Transformation plugin passes to <see cref="TransformIndex"/>.
    /// </summary>
    public class TransformPayload
    {
        /// <summary>Gets or sets the file contents.</summary>
        public string? Contents { get; set; }
    }

    /// <summary>
    /// Callback for the File Transformation plugin.
    /// </summary>
    /// <param name="payload">File contents.</param>
    /// <returns>Patched contents.</returns>
    public static string TransformIndex(TransformPayload payload)
    {
        return Inject(payload?.Contents);
    }
}
