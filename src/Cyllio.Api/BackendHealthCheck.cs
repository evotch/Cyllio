using Microsoft.Extensions.Diagnostics.HealthChecks;

internal sealed class BackendHealthCheck(HttpClient client, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var service in new[] { ("Identity", "http://localhost:8081/"), ("Device", "http://localhost:8082/") })
            {
                var address = configuration["Services:" + service.Item1] ?? service.Item2;
                using var response = await client.GetAsync(new Uri(new Uri(address), "health/ready"), cancellationToken);
                if (!response.IsSuccessStatusCode) return HealthCheckResult.Unhealthy("Account backend is not ready.");
            }
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Account backend is unavailable.");
        }
    }
}
