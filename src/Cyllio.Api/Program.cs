using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<BackendHealthCheck>(client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHealthChecks().AddCheck<BackendHealthCheck>("account-backends");
var routes = new List<RouteConfig>();
foreach (var path in new[] { "auth/{**remaining}", "users/{**remaining}", "privacy/{**remaining}" })
{
    routes.Add(new RouteConfig { RouteId = path, ClusterId = "identity", Match = new RouteMatch { Path = "/api/v1/" + path } });
}
routes.Add(new RouteConfig { RouteId = "devices", ClusterId = "device", Match = new RouteMatch { Path = "/api/v1/devices/{**remaining}" } });
builder.Services.AddReverseProxy().LoadFromMemory(routes,
[
    new ClusterConfig { ClusterId = "identity", Destinations = new Dictionary<string, DestinationConfig>
        { ["default"] = new() { Address = builder.Configuration["Services:Identity"] ?? "http://localhost:8081/" } } },
    new ClusterConfig { ClusterId = "device", Destinations = new Dictionary<string, DestinationConfig>
        { ["default"] = new() { Address = builder.Configuration["Services:Device"] ?? "http://localhost:8082/" } } }
]);
var app = builder.Build();
app.MapGet("/", () => Results.Ok(new { service = "Cyllio.Api", status = "running" }));
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
// Backends validate the bearer token and session; development OTP routes are not forwarded.
app.MapReverseProxy();
app.Run();
