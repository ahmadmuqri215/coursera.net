using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

public record TokenAuthOptions(IReadOnlyCollection<string> Tokens, IReadOnlyCollection<string> ExemptPrefixes);

public class TokenAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TokenAuthenticationMiddleware> _logger;
    private readonly byte[][] _tokenHashes;
    private readonly string[] _exempt;

    public TokenAuthenticationMiddleware(RequestDelegate next, ILogger<TokenAuthenticationMiddleware> logger, TokenAuthOptions options)
    {
        _next = next;
        _logger = logger;
        _tokenHashes = options.Tokens.Select(Hash).ToArray();
        _exempt = options.ExemptPrefixes.ToArray();
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (_exempt.Any(p => ctx.Request.Path.StartsWithSegments(p)))
        {
            await _next(ctx);
            return;
        }

        if (TryGetBearerToken(ctx, out var token) && IsValid(token))
        {
            await _next(ctx);
            return;
        }

        // Rejections are logged here because this middleware short-circuits before the logging middleware.
        _logger.LogWarning("Rejected {Method} {Path} -> 401 (missing or invalid token)",
            LogText.Safe(ctx.Request.Method), LogText.Safe(ctx.Request.Path.Value));

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
        await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
    }

    private static bool TryGetBearerToken(HttpContext ctx, out string token)
    {
        token = "";
        if (AuthenticationHeaderValue.TryParse(ctx.Request.Headers.Authorization.ToString(), out var header)
            && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(header.Parameter))
        {
            token = header.Parameter!;
            return true;
        }
        return false;
    }

    // Compare SHA-256 hashes in constant time; check every token (no early exit) to avoid timing leaks.
    private bool IsValid(string token)
    {
        var candidate = Hash(token);
        var match = false;
        foreach (var valid in _tokenHashes)
            match |= CryptographicOperations.FixedTimeEquals(candidate, valid);
        return match;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
