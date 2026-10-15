using System.Diagnostics;

public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        // Method and path only: no query string, headers or body, so tokens and personal data stay out of logs.
        var method = LogText.Safe(ctx.Request.Method);
        var path = LogText.Safe(ctx.Request.Path.Value);
        var sw = Stopwatch.StartNew();

        logger.LogInformation("Request  {Method} {Path}", method, path);
        try
        {
            await next(ctx);
            logger.LogInformation("Response {Method} {Path} -> {StatusCode} in {ElapsedMs} ms",
                method, path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
        }
        catch
        {
            // The error-handling middleware (outer) turns this into a 500; log it as such, then rethrow.
            logger.LogWarning("Response {Method} {Path} -> 500 (unhandled exception) in {ElapsedMs} ms",
                method, path, sw.ElapsedMilliseconds);
            throw;
        }
    }
}

// Strips control characters (CR/LF etc.) so a crafted URL can't forge log lines.
public static class LogText
{
    public static string Safe(string? value) =>
        value is null ? "" : new string(value.Where(c => !char.IsControl(c)).ToArray());
}
