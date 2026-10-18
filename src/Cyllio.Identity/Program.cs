using Cyllio.Common.Accounts;
using Cyllio.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.AddAccountFoundation();
builder.Services.AddSingleton<DevelopmentOtpDelivery>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("verification", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
await app.Services.GetRequiredService<AccountDatabase>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.MapGet("/", () => Results.Ok(new { service = "Cyllio.Identity", status = "running" }));
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapAuthenticationEndpoints();
app.MapAccountEndpoints();
app.Run();
