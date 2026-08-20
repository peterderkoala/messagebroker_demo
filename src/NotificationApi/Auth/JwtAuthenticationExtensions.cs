using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace NotificationPlatform.NotificationApi.Auth;

/// <summary>
/// The self-issued dev JWT flow (research: <c>docs/research/self-issued-jwt-auth.md</c>
/// on branch <c>research/self-issued-jwt-auth</c>). Deliberately minimal - see
/// <see cref="TokenEndpoint"/> for the caveat against reusing this pattern
/// outside a local learning environment.
/// </summary>
public static class JwtAuthenticationExtensions
{
    public const string Issuer = "messagebroker-demo";
    public const string Audience = "messagebroker-demo-clients";

    public static IHostApplicationBuilder AddPlatformJwtAuthentication(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var signingKey = builder.Configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Missing required configuration value 'Jwt:SigningKey' (env var JWT__SIGNINGKEY).");

        var symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = symmetricKey,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(symmetricKey);

        return builder;
    }
}
