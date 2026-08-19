# Self-issued JWT auth for a .NET 10 Minimal API (local demo scope)

> Scope note: this document is written for a **local docker-compose learning/demo environment only**
> (the messagebroker_demo notification platform). It deliberately covers self-issuing a JWT from a
> hardcoded username/password, which is explicitly against Microsoft's own production guidance
> (see the caveat in "Sources" and the callout below). Do not port this pattern to a real system
> without replacing it with a proper identity provider (Entra ID, Duende IdentityServer, Keycloak,
> etc.) and asymmetric signing.

## Summary / recommended pattern

- Add `Microsoft.AspNetCore.Authentication.JwtBearer` **10.0.11** and
  `Microsoft.IdentityModel.JsonWebTokens` **8.22.0** to the API project (both build for `net10.0`).
- In `Program.cs`, call `builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(...)`,
  configuring `TokenValidationParameters` with a `SymmetricSecurityKey` read from configuration
  (`Jwt:SigningKey`, bound from the `JWT__SIGNINGKEY` env var via ASP.NET Core's built-in
  double-underscore convention), and `app.UseAuthentication(); app.UseAuthorization();`.
- Add one unauthenticated `MapPost("/token", ...)` Minimal API endpoint that checks the posted
  username/password against a single hardcoded demo credential, and on success builds a JWT with
  `Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor)`
  (the handler Microsoft now recommends over the older `JwtSecurityTokenHandler`).
- Protect every other endpoint with `.RequireAuthorization()` (or `[Authorize]` on controllers) —
  the JWT bearer middleware validates signature, issuer, audience and expiry on every request.
- Keep the claim set minimal: `sub`, `name`, one or more `role` claims, `iat`, `exp`, `jti`, `iss`,
  `aud`. Skip anything OAuth/OIDC-specific that this demo has no use for (`scope`, `client_id`,
  `nonce`, `azp`, etc.).
- Use a short expiry — **30 minutes** is the recommendation here — and skip refresh tokens
  entirely; for a demo, re-hitting `/token` is simpler and safer than a refresh-token flow.
- Store the signing key as an env var (`JWT__SIGNINGKEY`) passed in through docker-compose
  `environment:`, at least 32 random bytes (256 bits) base64- or hex-encoded, because
  Microsoft.IdentityModel enforces a 256-bit minimum for HS256 as of IdentityModel 6.30.1+ (current
  version 8.22.0 still enforces it).

## Packages to install

| Package | Version (verified) | Purpose |
|---|---|---|
| `Microsoft.AspNetCore.Authentication.JwtBearer` | **10.0.11** (targets `net10.0`, also has `net10.0-*` platform TFMs) | `AddJwtBearer()` middleware for validating bearer JWTs on protected endpoints. [1] |
| `Microsoft.IdentityModel.JsonWebTokens` | **8.22.0** (multi-targets `net6.0`/`net8.0`/`net9.0`/`net10.0`/`netstandard2.0`/`net462`+) | `JsonWebTokenHandler` — used to *create* (sign) the JWT in `/token`. This is a transitive dependency of the JwtBearer package for validation, but add it explicitly since you're also using it to issue tokens. [2] |

`System.IdentityModel.Tokens.Jwt` (the older `JwtSecurityTokenHandler`, also currently at 8.22.0)
is **not** needed here — see the handler choice discussion below. [3]

```bash
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer --version 10.0.11
dotnet add package Microsoft.IdentityModel.JsonWebTokens --version 8.22.0
```

### Why `JsonWebTokenHandler` over `JwtSecurityTokenHandler`

Microsoft.IdentityModel's `JsonWebTokenHandler` (in `Microsoft.IdentityModel.JsonWebTokens`) is the
modern, actively-developed token handler: it's ~30% faster than the legacy `JwtSecurityToken`
path, supports async validation (`ValidateTokenAsync`) returning a result object instead of
throwing, and is fully AOT-compatible; ASP.NET Core's own `JwtBearerHandler` uses it internally.
`JwtSecurityTokenHandler` (`System.IdentityModel.Tokens.Jwt`) is the older API and is being
de-emphasized in favor of the newer handler. [3][4] For a demo written against .NET 10 today, use
`JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor)` for issuance — it's a supported,
documented instance method that returns the encoded JWT `string` directly. [4]

## .NET 10 status (verified, not assumed)

.NET 10 reached General Availability on **November 11, 2025** at .NET Conf 2025, and is a
Long-Term Support (LTS) release supported through November 10, 2028. As of this research
(August 2026), the current servicing release is **.NET 10.0.11**, shipped with SDK bands
**10.0.303 / 10.0.111 / 10.0.400** on August 11, 2026. [5][6] All package versions and API
guidance below are therefore based on **stable, GA documentation for `aspnetcore-10.0`**, not
preview/RC docs.

## Program.cs — full setup

```csharp
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- Read the signing key from configuration ---------------------------------
// Bound from either appsettings ("Jwt:SigningKey") or, in docker-compose, the
// environment variable JWT__SIGNINGKEY (ASP.NET Core's EnvironmentVariablesConfigurationProvider
// maps "__" to the ":" section separator automatically). [7]
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException(
        "Missing required configuration value 'Jwt:SigningKey' (env var JWT__SIGNINGKEY).");

const string JwtIssuer = "messagebroker-demo";
const string JwtAudience = "messagebroker-demo-clients";
var signingKeyBytes = Encoding.UTF8.GetBytes(jwtSigningKey);
var signingKey = new SymmetricSecurityKey(signingKeyBytes); // must be >= 32 bytes (256 bits) for HS256, see below

// --- Authentication / authorization ------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtIssuer,
            ValidateAudience = true,
            ValidAudience = JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30), // default is 5 minutes [8]; tighter is fine for a
                                                    // same-host demo where client/server clocks agree
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// WebApplication auto-registers UseAuthentication/UseAuthorization when Add* is called above,
// but registering explicitly keeps middleware order predictable, e.g. relative to CORS. [9]
app.UseAuthentication();
app.UseAuthorization();

// --- /token endpoint (unauthenticated, issues the JWT) ------------------------
app.MapPost("/token", (LoginRequest request) =>
{
    // Hardcoded demo credential check ONLY — never do this against a real user store.
    if (request.Username != "demo" || request.Password != "demo-password")
    {
        return Results.Unauthorized();
    }

    var now = DateTime.UtcNow;
    var tokenHandler = new JsonWebTokenHandler();

    var descriptor = new SecurityTokenDescriptor
    {
        Issuer = JwtIssuer,
        Audience = JwtAudience,
        IssuedAt = now,
        NotBefore = now,
        Expires = now.AddMinutes(30), // short-lived demo token, see "Expiry" section
        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = "demo-user-1",
            [JwtRegisteredClaimNames.Name] = request.Username,
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            [ClaimTypes.Role] = "Admin", // single hardcoded role for this demo user
        },
    };

    var jwt = tokenHandler.CreateToken(descriptor); // returns the encoded JWT string [4]

    return Results.Ok(new { access_token = jwt, token_type = "Bearer", expires_in = 1800 });
});

// --- A protected endpoint ------------------------------------------------------
app.MapGet("/notifications", () => Results.Ok(new[] { "example notification" }))
    .RequireAuthorization();

// Equivalent MVC-controller-style alternative: [Authorize] on the controller/action.

app.Run();

record LoginRequest(string Username, string Password);
```

Notes on the code:

- `AddAuthentication(JwtBearerDefaults.AuthenticationScheme)` sets `"Bearer"` as both the default
  authenticate and challenge scheme, which is what lets bare `.RequireAuthorization()` (no policy
  name) and `[Authorize]` work without further wiring. [9]
- The Minimal API guidance samples this exact shape — `builder.Services.AddAuthentication().AddJwtBearer()`
  plus `app.MapGet(...).RequireAuthorization()` — as the standard way to protect a Minimal API
  endpoint; `[Authorize]` also works directly on a route handler delegate
  (`app.MapGet("/hello", [Authorize] () => "Hi")`). [9][10]
- `SetDefaultTimesOnTokenCreation` on `TokenHandler` (inherited by `JsonWebTokenHandler`) defaults
  to `true`, so `iat`/`nbf`/`exp` would be populated automatically even without setting them
  explicitly — but setting `Expires` explicitly is still recommended for clarity. [4]

## Claims — what to include and why

| Claim | Include for this demo? | Why |
|---|---|---|
| `sub` | Yes | Standard JWT subject identifier; identifies "who" the token is about. Required per RFC 7519 conventions and expected by most JWT tooling. Microsoft's own JWT bearer guidance lists `sub` among the claims required for OAuth 2.0 access tokens. [1] |
| `name` / `preferred_username` | Yes (use `name`) | Human-readable identifier for the demo user; convenient for logging/debugging without a lookup. Not required by the spec, but cheap and useful for a demo. |
| `role` (one or more) | Yes | Drives `[Authorize(Roles = "Admin")]` / `RequireRole` style authorization if the demo wants role-gated endpoints. ASP.NET Core's `ClaimTypes.Role` claim type is what `RequireRole`/`[Authorize(Roles=...)]` check by default. |
| `iat` | Yes | Issued-at time; standard, and set automatically by `JsonWebTokenHandler` when `SetDefaultTimesOnTokenCreation` is true (default). [4] |
| `exp` | Yes (mandatory) | Token expiry — this is what `ValidateLifetime = true` actually checks. Non-negotiable for a bearer token. [1][8] |
| `jti` | Yes | Unique token ID. Not strictly validated by `AddJwtBearer` out of the box, but cheap to include and useful if you ever add revocation/logging later. Listed among required OAuth 2.0 access-token claims in Microsoft's guidance. [1] |
| `iss` | Yes | Checked by `ValidateIssuer` — needed so the API only accepts tokens it minted itself. [1][8] |
| `aud` | Yes | Checked by `ValidateAudience` — scopes the token to "this API" and is standard bearer-token hygiene even with one API. [1][8] |
| `scope` / `client_id` / `azp` / `nonce` | No | These are OAuth2/OIDC-flow-specific claims (client credentials flow, authorization code flow with PKCE, etc.) that have no meaning when the token issuer *is* the API itself and there's exactly one hardcoded "client." Omit — including them without real backing semantics is cargo-culting, not security. |

**Recommendation:** include `sub`, `name`, `role`, `iat`, `exp`, `jti`, `iss`, `aud` — this is the
full "necessary" set for a self-contained demo API that both issues and validates its own tokens.
Nothing beyond this is warranted at this scope.

## Expiry recommendation

**30 minutes**, with no refresh token.

Reasoning:
- This is a local docker-compose demo hit interactively or by short-lived test scripts — sessions
  longer than a typical demo/dev session aren't needed, and a refresh-token flow adds a second
  endpoint, a second token type, and (usually) server-side state to track revocation — complexity
  this demo doesn't need.
- 15 minutes (a common "short-lived access token" figure in OAuth guidance) is also reasonable;
  30 minutes gives more slack for a demo/dev loop (e.g., stepping through a debugger) without being
  so long that a leaked token in a demo log stays valid all day.
- If a token expires mid-demo, hitting `/token` again is a one-line `curl`/HTTP call — trivially
  cheap compared to implementing refresh-token issuance, rotation, and revocation correctly.
- `ClockSkew` defaults to 5 minutes in `TokenValidationParameters`, which is added on top of `exp`
  during validation. [8] The sample above tightens it to 30 seconds since issuer and validator are
  the same process/host in a compose network — leaving the default 5 minutes is also fine and
  simpler if that tightening isn't wanted.

## Signing key storage (docker-compose / local env var)

### Key length and algorithm

Microsoft.IdentityModel enforces a **minimum 256-bit (32-byte) key** for HMAC-SHA256 (`HS256`) —
this has been enforced since Microsoft.IdentityModel **6.30.1** (fixed as a security issue; a
smaller key throws `SecurityTokenInvalidSigningKeyException` / `IDX10720`, "the algorithm 'HS256'
requires the SecurityKey.KeySize to be greater than '256' bits") and the currently-shipping
**8.22.0** still enforces it. [11][12] An older, laxer 128-bit check (`IDX10603`) also exists in the
library history but the effective floor today is 256 bits — generate at least 32 random bytes.

Recommended: generate 32+ random bytes and store as base64 (or hex) text:

```bash
# 32 random bytes, base64-encoded — 44 chars, comfortably above the 256-bit HS256 floor
openssl rand -base64 32
```

or, without external tools, via the .NET CLI:

```bash
dotnet run --project . -c 'Console.WriteLine(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))'
```

Note: in the `Program.cs` sample above, the base64 string is turned into signing-key bytes via
`Encoding.UTF8.GetBytes(...)` (i.e. the *characters* of the base64 string are the key material),
which is simplest and still comfortably >= 32 bytes since a 32-byte base64 string is 44 characters
long. If you prefer the raw entropy as the key, swap in `Convert.FromBase64String(jwtSigningKey)`
instead — either is fine for HS256 as long as the resulting byte array is >= 32 bytes.

### Env var naming and IConfiguration binding

ASP.NET Core's default configuration pipeline includes the environment-variables provider, which
maps `__` (double underscore) in an env var name to `:` (the standard hierarchical configuration
separator) — this exists specifically because `:` isn't a valid/portable identifier in shell
environments (e.g., bash). So the env var:

```
JWT__SIGNINGKEY=<your base64 key>
```

is read in code as `builder.Configuration["Jwt:SigningKey"]`, no extra provider code needed. [7]

### docker-compose wiring

```yaml
services:
  notification-api:
    build: ./src/NotificationApi
    environment:
      JWT__SIGNINGKEY: ${JWT_SIGNING_KEY}   # sourced from a .env file or the host shell, not committed
      JWT__ISSUER: messagebroker-demo
      JWT__AUDIENCE: messagebroker-demo-clients
    ports:
      - "5080:8080"
```

with a local, git-ignored `.env` file at the compose project root:

```
JWT_SIGNING_KEY=<output of `openssl rand -base64 32`>
```

This is the standard docker-compose interpolation pattern: `${JWT_SIGNING_KEY}` is substituted from
the shell environment or an adjacent `.env` file at `docker-compose up` time, and the *container's*
env var ends up named `JWT__SIGNINGKEY` (matching the `__` convention above) so ASP.NET Core's
configuration system picks it up as `Jwt:SigningKey` with zero custom code. An env var is
sufficient and appropriate here — a real secrets manager (Azure Key Vault, AWS Secrets Manager,
etc.) is unnecessary overhead for a local learning/demo compose stack. [7]

An `appsettings.json` fallback/default (non-secret placeholder, overridden by the env var in every
real run) can also be added for local `dotnet run` convenience:

```json
{
  "Jwt": {
    "Issuer": "messagebroker-demo",
    "Audience": "messagebroker-demo-clients"
  }
}
```

— deliberately omitting `SigningKey` from `appsettings.json` so the app fails fast (per the
`?? throw` in the `Program.cs` snippet) if the env var isn't set, rather than silently falling back
to a key checked into source control.

## Important caveat: this pattern vs. Microsoft's own guidance

Microsoft's official "Configure JWT bearer authentication in ASP.NET Core" doc (current as of
2026-08-07, `aspnetcore-10.0`) explicitly states, under "Recommended approaches to create a JWT":

> "You should **NOT** create an access token from a username/password request. Username/password
> requests aren't authenticated and are vulnerable to impersonation and phishing attacks. Access
> tokens should only be created using an OpenID Connect flow or an OAuth standard flow."

and

> "Asymmetric keys should **always** be used when creating access tokens."

Both are direct conflicts with what this document recommends (self-issued token from a hardcoded
username/password, symmetric HS256 key). [1] That guidance is written for production systems. The
task this document supports is explicitly scoped to a **local docker-compose learning/demo
environment**, where the tradeoffs are different: there's no real user data, no network-exposed
deployment, and the goal is to learn/demonstrate the `[Authorize]` + `AddJwtBearer` mechanics
cheaply. The pattern above is a reasonable, minimal choice **for that scope only** — it should not
be treated as a template for a production API. If this project grows toward something
internet-facing or handling real credentials, replace `/token` with a real OIDC/OAuth flow against
a proper identity provider, per Microsoft's guidance.

## Sources

1. [Configure JWT bearer authentication in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) — verified: the `Microsoft.AspNetCore.Authentication.JwtBearer` package identity, `AddJwtBearer` usage patterns, `[Authorize]` on Minimal API route delegates (`app.MapGet("/hello", [Authorize] () => "Hi")`), the required-claims list for OAuth 2.0 access tokens (`iss`, `exp`, `aud`, `sub`, `client_id`, `iat`, `jti`), and the explicit "never issue tokens from username/password" / "always use asymmetric keys" production guidance quoted above. Page dated 2026-08-07 (`aspnetcore-10.0` default moniker).
2. [Microsoft.IdentityModel.JsonWebTokens on NuGet.org](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens) — verified: current stable version **8.22.0** (released 2026-07-29), multi-targets `net6.0`/`net8.0`/`net9.0`/`net10.0`/`netstandard2.0`/`net462`+.
3. [System.IdentityModel.Tokens.Jwt on NuGet.org](https://www.nuget.org/packages/System.IdentityModel.Tokens.Jwt) — verified: current stable version also **8.22.0**, and that it depends on `Microsoft.IdentityModel.JsonWebTokens (>= 8.22.0)` across all target frameworks — confirming `JsonWebTokenHandler` is the underlying/newer implementation.
4. [JsonWebTokenHandler Class (Microsoft.IdentityModel.JsonWebTokens)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.jsonwebtokens.jsonwebtokenhandler?view=msal-web-dotnet-latest) — verified: namespace/assembly, the `CreateToken(SecurityTokenDescriptor)` overload used in the code sample, `SetDefaultTimesOnTokenCreation` (inherited from `TokenHandler`) defaulting to populating `iat`/`nbf`/`exp` automatically, and that `ValidateToken(string, TokenValidationParameters)` is marked obsolete in favor of the async `ValidateTokenAsync` overloads (supporting the "prefer JsonWebTokenHandler" recommendation).
5. [Announcing .NET 10 - .NET Blog](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/) (via search) — verified: .NET 10 reached GA on November 11, 2025, as an LTS release supported through November 10, 2028.
6. [.NET 10.0 August 2026 Update discussion — dotnet/source-build](https://github.com/dotnet/source-build/discussions/5634) (via search) — verified: current patch as of this research is .NET 10.0.11 with SDK bands 10.0.303/10.0.111/10.0.400, released 2026-08-11 — consistent with the NuGet package version found for `Microsoft.AspNetCore.Authentication.JwtBearer`.
7. [Configuration in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0) (via search) — verified: the environment-variables configuration provider maps `__` to `:` for hierarchical keys (e.g. `ConnectionStrings__Default` → `ConnectionStrings:Default`), specifically because `:` isn't usable in some shells (bash included) — the basis for the `JWT__SIGNINGKEY` → `Jwt:SigningKey` mapping used above.
8. [TokenValidationParameters.ClockSkew Property](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.tokens.tokenvalidationparameters.clockskew?view=msal-web-dotnet-latest) and `DefaultClockSkew` field (via search of the same API reference site) — verified: `ClockSkew` default is `TimeSpan.FromMinutes(5)`, applied on top of `exp`/`nbf` during lifetime validation.
9. [Authentication and authorization in Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/security?view=aspnetcore-10.0) — verified: the exact `builder.Services.AddAuthentication().AddJwtBearer();` Minimal API setup pattern (explicitly noted as requiring the `Microsoft.AspNetCore.Authentication.JwtBearer` NuGet package), that `WebApplication` auto-registers `UseAuthentication`/`UseAuthorization` middleware once `AddAuthentication`/`AddAuthorization` are called (but that explicit `UseAuthentication()`/`UseAuthorization()` calls are needed when middleware ordering matters, e.g. relative to CORS), `RequireAuthorization()` / `AddAuthorizationBuilder().AddPolicy(...)` usage, and configuration-driven scheme options under `Authentication:Schemes:{SchemeName}`. Page dated 2026-04-28, default moniker `aspnetcore-10.0`.
10. [Generate tokens with dotnet user-jwts](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/jwt-authn?view=aspnetcore-10.0) (via search) — verified: `dotnet user-jwts` subcommands (`create`, `key`, `list`, `print`, `remove`, `clear`) and that it stores generated signing keys via the user-secrets mechanism with a `SigningKeys` configuration shape including `Id`/`Issuer`/`Value`/`Length` — used only as corroborating evidence for the "generate a random key of sufficient length" guidance, not as the mechanism recommended for this docker-compose demo (user-secrets isn't appropriate inside a container).
11. [IDX10720 wiki — azure-activedirectory-identitymodel-extensions-for-dotnet](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/wiki/IDX10720) — verified: HS256 requires a 256-bit key (HS384 → 384 bits, HS512 → 512 bits) per RFC 7518, enforcement introduced in Microsoft.IdentityModel **6.30.1** via the "Enforce key sizes when creating HMAC" fix, and the existence (but non-use here) of the `Switch.Microsoft.IdentityModel.UnsafeRelaxHmacKeySizeValidation` AppContext escape hatch.
12. NuGet.org version pages for `Microsoft.IdentityModel.JsonWebTokens` / `System.IdentityModel.Tokens.Jwt` (sources 2 and 3 above) — verified current version 8.22.0 postdates the 6.30.1 enforcement fix, i.e. the 256-bit HS256 floor is still in force in the version this document recommends installing.
