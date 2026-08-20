using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace NotificationPlatform.NotificationApi.Auth;

/// <summary>
/// Issues the self-issued dev JWT. This endpoint self-issuing a token from a
/// hardcoded username/password is a deliberate, scope-limited choice - see
/// <c>docs/research/self-issued-jwt-auth.md</c> (branch
/// <c>research/self-issued-jwt-auth</c>) for why this is safe only for a
/// local docker-compose learning environment and not a pattern to reuse
/// anywhere real credentials or an internet-facing deployment are involved.
/// </summary>
public static class TokenEndpoint
{
    private const string DemoUsername = "demo";
    private const string DemoPassword = "demo-password";

    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/token", (LoginRequest request, SymmetricSecurityKey signingKey) =>
        {
            if (request.Username != DemoUsername || request.Password != DemoPassword)
            {
                return Results.Unauthorized();
            }

            var now = DateTime.UtcNow;
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = JwtAuthenticationExtensions.Issuer,
                Audience = JwtAuthenticationExtensions.Audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(30),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
                Claims = new Dictionary<string, object>
                {
                    [JwtRegisteredClaimNames.Sub] = "demo-user-1",
                    [JwtRegisteredClaimNames.Name] = request.Username,
                    [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                },
            };

            var accessToken = new JsonWebTokenHandler().CreateToken(descriptor);

            return Results.Ok(new TokenResponse(accessToken, "Bearer", 1800));
        });

        return endpoints;
    }
}

public sealed record LoginRequest(string Username, string Password);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
