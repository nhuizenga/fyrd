namespace Fyrd.Server;

internal static class SameOriginMiddleware
{
    /// <summary>
    /// Rejects state-changing /api requests whose Origin (or, lacking one, Referer) is not this server's own
    /// host:port, so other web pages cannot drive the local API. Requests with neither header (curl, the launcher) pass.
    /// </summary>
    public static IApplicationBuilder UseSameOriginApiWrites(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            if (req.Path.StartsWithSegments("/api")
                && !HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method))
            {
                var source = req.Headers.Origin.ToString();
                if (source.Length == 0) source = req.Headers.Referer.ToString();
                if (source.Length > 0 && !IsSameHost(source, req.Host))
                {
                    await Results.Problem("Cross-origin request rejected.", statusCode: StatusCodes.Status403Forbidden)
                        .ExecuteAsync(ctx);
                    return;
                }
            }
            await next();
        });

    private static bool IsSameHost(string source, HostString host)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        var own = host.Port is { } p ? new Uri($"{uri.Scheme}://{host.Host}:{p}") : new Uri($"{uri.Scheme}://{host.Host}");
        return string.Equals(uri.Host, own.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == own.Port;
    }
}
