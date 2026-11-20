using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UpNext.Injection;

/// <summary>
/// If the File Transformation plugin is installed, registers with it too. This is a
/// backup for setups where the built-in middleware can't see index.html.
/// </summary>
public sealed class FileTransformationRegistration : IHostedService
{
    private readonly ILogger<FileTransformationRegistration> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationRegistration"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public FileTransformationRegistration(ILogger<FileTransformationRegistration> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RegisterWithRetries(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RegisterWithRetries(CancellationToken cancellationToken)
    {
        // File Transformation may finish starting after us, so try a few times.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (TryRegister())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Up Next: File Transformation not ready yet (attempt {Attempt})", attempt + 1);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool TryRegister()
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
        if (assembly is null)
        {
            // Not installed: the middleware handles injection on its own.
            return true;
        }

        var register = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
            ?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
        var payloadType = register?.GetParameters().FirstOrDefault()?.ParameterType;
        var parse = payloadType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        if (register is null || parse is null)
        {
            _logger.LogWarning("Up Next: this File Transformation version isn't supported; using built-in injection only");
            return true;
        }

        var json = JsonSerializer.Serialize(new
        {
            id = Plugin.PluginId.ToString(),
            fileNamePattern = "index.html",
            callbackAssembly = typeof(ScriptTag).Assembly.FullName,
            callbackClass = typeof(ScriptTag).FullName,
            callbackMethod = nameof(ScriptTag.TransformIndex)
        });

        // Build the JObject with File Transformation's own Newtonsoft copy so the types match.
        var payload = parse.Invoke(null, new object[] { json });
        register.Invoke(null, new[] { payload });
        _logger.LogInformation("Up Next: registered with File Transformation");
        return true;
    }
}
