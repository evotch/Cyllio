using Cyllio.Common.Accounts;
using Cyllio.Device;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.AddAccountFoundation();
var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", () => Results.Ok(new { service = "Cyllio.Device", status = "running" }));
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapDeviceEndpoints();
app.Run();
