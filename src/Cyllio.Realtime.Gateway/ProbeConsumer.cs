using Azure.Messaging.ServiceBus;
using Cyllio.Common.Messaging;

// Consumes only infrastructure probes. Business event handlers must be added explicitly.
internal sealed class ProbeConsumer(ServiceBusClient client, ILogger<ProbeConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = client.CreateProcessor(ServiceBusEntities.MessageEventsTopic, ServiceBusEntities.RealtimeSubscription,
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        processor.ProcessMessageAsync += async args =>
        {
            if (args.Message.Subject != InfrastructureProbe.EventName)
            {
                await args.DeadLetterMessageAsync(args.Message, "UnsupportedEvent", cancellationToken: args.CancellationToken);
                return;
            }
            logger.LogInformation("Service Bus probe received: {EventId} by realtime", args.Message.MessageId);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        };
        processor.ProcessErrorAsync += args =>
        {
            logger.LogWarning("Service Bus processing error: {Reason}", args.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        try
        {
            await processor.StartProcessingAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }
}
