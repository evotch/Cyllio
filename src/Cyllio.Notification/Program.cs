using Azure.Messaging.ServiceBus;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var connectionString = builder.Configuration["ServiceBus:ConnectionString"];
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(_ => new ServiceBusClient(connectionString));
    builder.Services.AddHostedService<ProbeConsumer>();
}
var app = builder.Build();
app.MapGet("/", () => Results.Ok(new { service = "Cyllio.Notification", status = "running" }));
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.Run();
