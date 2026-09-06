using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Serves the built docs site from a local Kestrel server the way GitHub Pages does: plain files
/// under the site's base path, with no headers beyond content types.
/// </summary>
internal sealed class StaticSite : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StaticSite(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>
    /// The URL of the site root, including the base path.
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Starts a server for the build output on a free port.
    /// </summary>
    /// <param name="dist">The Astro build output directory.</param>
    /// <returns>The running site.</returns>
    public static async Task<StaticSite> StartAsync(string dist)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".wasm"] = "application/wasm";
        contentTypes.Mappings[".dat"] = "application/octet-stream";
        contentTypes.Mappings[".cast"] = "text/plain";

        var app = builder.Build();
        var provider = new PhysicalFileProvider(dist);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = true,
        });

        await app.StartAsync().ConfigureAwait(false);
        return new StaticSite(app, app.Urls.First());
    }

    /// <summary>
    /// Stops the server.
    /// </summary>
    /// <returns>A task that completes when the server has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
