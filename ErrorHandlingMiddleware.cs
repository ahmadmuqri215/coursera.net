public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; nothing to send back.
        }
        catch (Exception ex)
        {
            // Full detail goes to the log only, never to the client.
            logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                LogText.Safe(ctx.Request.Method), LogText.Safe(ctx.Request.Path.Value));

            if (ctx.Response.HasStarted)
            {
                ctx.Abort();   // too late to change the response
                return;
            }

            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error." });
        }
    }
}
