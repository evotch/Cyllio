using Azure.Messaging.ServiceBus;
using Cyllio.Common.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var connectionString = builder.Configuration["ServiceBus:ConnectionString"];
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(_ => new ServiceBusClient(connectionString));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(ServiceBusEntities.MessageEventsTopic));
}
var app = builder.Build();
app.MapGet("/", () => Results.Ok(new { service = "Cyllio.Messaging", status = "running" }));
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
// A development-only infrastructure probe, not a message persistence API.
if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(connectionString))
{
    app.MapPost("/dev/servicebus/probe", async (ServiceBusSender sender, CancellationToken cancellationToken) =>
    {
        var eventId = Guid.NewGuid().ToString();
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(new InfrastructureProbe(eventId)))
        {
            MessageId = eventId,
            Subject = InfrastructureProbe.EventName,
            ContentType = "application/json"
        };
        await sender.SendMessageAsync(message, cancellationToken);
        return Results.Accepted(value: new { eventId });
    });
}
app.Run();
