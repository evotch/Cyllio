using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using AmazonServiceException = Amazon.Runtime.AmazonServiceException;

namespace Cyllio.Common.Accounts;

public static class AccountApi
{
    public static void AddAccountFoundation(this WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<AccountDatabase>();
        builder.Services.AddSingleton<AccountTokens>();
        // Fail closed when secrets are missing; there is no built-in development signing key.
        var tokens = new AccountTokens(builder.Configuration);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = AccountTokens.Issuer,
                ValidateAudience = true,
                ValidAudience = AccountTokens.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = tokens.SigningKey,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var database = context.HttpContext.RequestServices.GetRequiredService<AccountDatabase>();
                    var userId = context.Principal?.FindFirstValue("sub");
                    var deviceId = context.Principal?.FindFirstValue("deviceId");
                    var sessionId = context.Principal?.FindFirstValue("sessionId");
                    var account = await database.GetAccountAsync(userId, context.HttpContext.RequestAborted);
                    if (account is null || account.SecurityVersion.ToString() != context.Principal?.FindFirstValue("securityVersion") ||
                        !account.Devices.Any(x => x.Id == deviceId && x.RevokedAt == null) ||
                        !account.Sessions.Any(x => x.Id == sessionId && x.DeviceId == deviceId && x.RevokedAt == null && x.ExpiresAt > DateTime.UtcNow))
                        context.Fail("Session is no longer active.");
                }
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddHealthChecks().AddCheck<AccountDatabaseHealthCheck>("dynamodb", tags: ["ready"]);
        builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
    }

    public static string UserId(this ClaimsPrincipal user) => user.FindFirstValue("sub")!;
    public static string DeviceId(this ClaimsPrincipal user) => user.FindFirstValue("deviceId")!;
    public static string SessionId(this ClaimsPrincipal user) => user.FindFirstValue("sessionId")!;
    public static IResult Invalid(string detail) => Results.Problem(statusCode: 400, title: "Invalid request", detail: detail);
    public static IResult VersionRequired() => Results.Problem(statusCode: 428, title: "If-Match required", detail: "Read the resource ETag and supply it as If-Match.");
}

internal sealed class DatabaseExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AmazonServiceException and not TimeoutException) return false;
        context.Response.StatusCode = 503;
        await Results.Problem(statusCode: 503, title: "Account database unavailable").ExecuteAsync(context);
        return true;
    }
}
