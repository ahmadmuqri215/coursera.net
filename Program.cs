using System.ComponentModel.DataAnnotations;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "API token"
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});
builder.Services.AddSingleton<UserStore>();

var app = builder.Build();

// Tokens come from configuration (user-secrets / env var Auth__Tokens__0), never hard-coded.
// Fail fast: refuse to start with no tokens rather than run unprotected.
var tokens = (builder.Configuration.GetSection("Auth:Tokens").Get<string[]>() ?? Array.Empty<string>())
    .Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
if (tokens.Length == 0)
    throw new InvalidOperationException("No API tokens configured. Set Auth:Tokens.");

// ---- Middleware pipeline: order matters ----
app.UseMiddleware<ErrorHandlingMiddleware>();                 // 1. outermost: catches exceptions from everything below
app.UseHttpsRedirection();
var exempt = app.Environment.IsDevelopment() ? new[] { "/swagger" } : Array.Empty<string>();
app.UseMiddleware<TokenAuthenticationMiddleware>(new TokenAuthOptions(tokens, exempt));  // 2. reject bad tokens early
app.UseMiddleware<RequestLoggingMiddleware>();                // 3. logs requests that reach the API

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // Dev-only: confirms the error-handling middleware returns a clean 500
    app.MapGet("/api/_debug/throw", () => { throw new InvalidOperationException("Test exception"); });
}

var users = app.MapGroup("/api/users").WithTags("Users");

// GET /api/users?page=1&pageSize=20  (total count returned in X-Total-Count header)
users.MapGet("/", (HttpContext ctx, UserStore store, int page = 1, int pageSize = 20) =>
{
    if (page < 1) return Results.BadRequest(new { error = "page must be >= 1." });
    if (pageSize is < 1 or > 100) return Results.BadRequest(new { error = "pageSize must be between 1 and 100." });

    var (items, total) = store.GetPage(page, pageSize);
    ctx.Response.Headers["X-Total-Count"] = total.ToString();
    return Results.Ok(items);
});

// GET /api/users/{id}
users.MapGet("/{id:int:min(1)}", (int id, UserStore store) =>
    store.Get(id) is { } user ? Results.Ok(user) : NotFound(id));

// POST /api/users
users.MapPost("/", (UserInput input, UserStore store) =>
{
    input.Normalize();
    if (!TryValidate(input, out var errors)) return Results.ValidationProblem(errors);

    var (result, user) = store.Add(input);
    return result == StoreResult.DuplicateEmail
        ? Results.Conflict(new { error = "Email already in use." })
        : Results.Created($"/api/users/{user!.Id}", user);
});

// PUT /api/users/{id}
users.MapPut("/{id:int:min(1)}", (int id, UserInput input, UserStore store) =>
{
    input.Normalize();
    if (!TryValidate(input, out var errors)) return Results.ValidationProblem(errors);

    var (result, user) = store.Update(id, input);
    return result switch
    {
        StoreResult.NotFound => NotFound(id),
        StoreResult.DuplicateEmail => Results.Conflict(new { error = "Email already in use." }),
        _ => Results.Ok(user)
    };
});

// DELETE /api/users/{id}
users.MapDelete("/{id:int:min(1)}", (int id, UserStore store) =>
    store.Remove(id) ? Results.NoContent() : NotFound(id));

app.Run();

static IResult NotFound(int id) => Results.NotFound(new { error = $"User {id} not found." });

static bool TryValidate<T>(T model, out Dictionary<string, string[]> errors) where T : notnull
{
    var results = new List<ValidationResult>();
    var ok = Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
    errors = results
        .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : new[] { "" }).Select(m => (m, r.ErrorMessage ?? "Invalid")))
        .GroupBy(x => x.Item1)
        .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).ToArray());
    return ok;
}

public class UserInput
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = "";

    [Required, StringLength(50)]
    public string Department { get; set; } = "";

    [StringLength(50)]
    public string? Role { get; set; }

    // Trim whitespace and normalise email so "  A@B.com " and "a@b.com" are the same user
    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        Email = (Email ?? "").Trim().ToLowerInvariant();
        Department = (Department ?? "").Trim();
        Role = string.IsNullOrWhiteSpace(Role) ? null : Role.Trim();
    }
}

public record User(int Id, string Name, string Email, string Department, string? Role);

public enum StoreResult { Ok, NotFound, DuplicateEmail }

// Thread-safe store. One lock makes "check email then insert" atomic (no duplicate race),
// and the email index makes uniqueness checks O(1) instead of scanning every user.
public class UserStore
{
    private readonly object _lock = new();
    private readonly SortedDictionary<int, User> _users = new();               // ordered by Id
    private readonly Dictionary<string, int> _emailIndex = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public (IReadOnlyList<User> Items, int Total) GetPage(int page, int pageSize)
    {
        lock (_lock)
        {
            var items = _users.Values.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return (items, _users.Count);
        }
    }

    public User? Get(int id)
    {
        lock (_lock) return _users.TryGetValue(id, out var u) ? u : null;
    }

    public (StoreResult, User?) Add(UserInput i)
    {
        lock (_lock)
        {
            if (_emailIndex.ContainsKey(i.Email)) return (StoreResult.DuplicateEmail, null);
            var user = new User(++_nextId, i.Name, i.Email, i.Department, i.Role);
            _users[user.Id] = user;
            _emailIndex[user.Email] = user.Id;
            return (StoreResult.Ok, user);
        }
    }

    public (StoreResult, User?) Update(int id, UserInput i)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(id, out var existing)) return (StoreResult.NotFound, null);
            if (_emailIndex.TryGetValue(i.Email, out var ownerId) && ownerId != id)
                return (StoreResult.DuplicateEmail, null);

            _emailIndex.Remove(existing.Email);
            var updated = new User(id, i.Name, i.Email, i.Department, i.Role);
            _users[id] = updated;
            _emailIndex[updated.Email] = id;
            return (StoreResult.Ok, updated);
        }
    }

    public bool Remove(int id)
    {
        lock (_lock)
        {
            if (!_users.Remove(id, out var u)) return false;
            _emailIndex.Remove(u.Email);
            return true;
        }
    }
}
