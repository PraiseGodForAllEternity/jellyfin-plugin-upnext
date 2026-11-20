using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.UpNext.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UpNext.Injection;

/// <summary>
/// Patches index.html as it's served, so nothing on disk is modified (the web
/// folder is read-only on most Linux installs).
/// </summary>
public sealed class IndexHtmlMiddleware
{
    private const int MaxIndexBytes = 4 * 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<IndexHtmlMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexHtmlMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="logger">Logger.</param>
    public IndexHtmlMiddleware(RequestDelegate next, ILogger<IndexHtmlMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!PreferenceStore.Config.EnableWebOverlay || !IsIndexRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for a plain, uncached copy so there's something to patch.
        var headers = context.Request.Headers;
        headers.Remove("Accept-Encoding");
        headers.Remove("If-None-Match");
        headers.Remove("If-Modified-Since");
        headers.Remove("Range");
        headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = buffer.ToArray();
        if (IsPatchable(context.Response) && bytes.Length <= MaxIndexBytes)
        {
            try
            {
                var html = Encoding.UTF8.GetString(bytes);
                var patched = ScriptTag.Inject(html);
                if (!ReferenceEquals(html, patched))
                {
                    bytes = Encoding.UTF8.GetBytes(patched);
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    context.Response.Headers.CacheControl = "no-cache";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Up Next: couldn't add the overlay script to index.html");
            }
        }

        if (!context.Response.HasStarted)
        {
            context.Response.ContentLength = bytes.Length;
        }

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPatchable(HttpResponse response)
    {
        return response.StatusCode == StatusCodes.Status200OK
            && response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
            && !response.Headers.ContainsKey("Content-Encoding");
    }
}

/// <summary>
/// Puts <see cref="IndexHtmlMiddleware"/> at the front of the pipeline.
/// </summary>
public sealed class IndexHtmlStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<IndexHtmlMiddleware>();
            next(app);
        };
    }
}
