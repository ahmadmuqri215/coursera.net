# Middleware Summary: User Management API

## Pipeline (in order)
| Order | Middleware | Job | Why here |
|-------|------------|-----|----------|
| 1 | `ErrorHandlingMiddleware` | Catch unhandled exceptions, return `{"error":"Internal server error."}` (500) | Outermost, so it wraps every other component |
| 2 | HTTPS redirection | Redirect HTTP to HTTPS | Before auth, so tokens travel over TLS |
| 3 | `TokenAuthenticationMiddleware` | Validate `Authorization: Bearer <token>`, else 401 JSON | Rejects bad requests before any API work |
| 4 | `RequestLoggingMiddleware` | Log method, path, status code, elapsed ms | Last, per the activity's required order |
| 5 | Endpoints | CRUD handlers | |

## Design decisions
| Topic | Decision |
|-------|----------|
| Token storage | Configuration (`Auth:Tokens`), not in code. Use user-secrets or env var `Auth__Tokens__0` outside dev. `appsettings.Development.json` holds a demo token only. |
| No tokens configured | App refuses to start (fail closed) |
| Token comparison | SHA-256 hashes compared with `FixedTimeEquals`, all tokens checked (no timing leak) |
| Logging content | Method, path, status, duration only. No query string, headers or body, so tokens and personal data stay out of logs. Control characters stripped to prevent log forging. |
| 401 audit gap | Logging runs after auth, so 401s never reach it. Auth middleware logs its own rejections as warnings. |
| Exception in logging | Logged as 500, rethrown to error middleware |
| Swagger | Exempt from auth in Development only |
| Token type | Static API keys, which satisfies "token-based". For per-user identity or expiry, use JWT bearer authentication instead. |

## Expected log output
| Request | Log lines |
|---------|-----------|
| Valid GET | `Request GET /api/users` then `Response GET /api/users -> 200 in N ms` |
| No / bad token | `Rejected GET /api/users -> 401 (missing or invalid token)` |
| Exception | `Request ...`, `Response ... -> 500 (unhandled exception)`, then an Error entry with the stack trace |

## Copilot log (fill in from your real sessions)
| Prompt used | What Copilot produced | What I changed or fixed | Result |
|-------------|----------------------|-------------------------|--------|
|             |                      |                         |        |

## Suggested prompts
| Goal | Prompt |
|------|--------|
| Logging | "Generate ASP.NET Core middleware that logs HTTP method, path and response status code." |
| Errors | "Create middleware that catches unhandled exceptions and returns `{ \"error\": \"Internal server error.\" }` as JSON." |
| Auth | "Create middleware that validates a Bearer token and returns 401 JSON for invalid tokens." |
| Pipeline | "Review my Program.cs middleware order: error handling first, authentication next, logging last." |
| Review | "Is this auth middleware safe against timing attacks and log injection?" |

## Test results (run Middleware.http, record the outcome)
| # | Test | Expected | Actual |
|---|------|----------|--------|
| 1 | No token | 401 JSON | |
| 2 | Wrong token | 401 JSON | |
| 3 | Basic scheme | 401 | |
| 4 | Empty bearer | 401 | |
| 5 | Valid token | 200 | |
| 6 | Missing user | 404 | |
| 7 | Invalid body | 400 | |
| 8 | Exception with token | 500 `{"error":"Internal server error."}` | |
| 9 | Exception without token | 401 | |
| 10 | Swagger without token | 200 (Development) | |
| 11 | Console logs match tests above | method, path, status present | |
