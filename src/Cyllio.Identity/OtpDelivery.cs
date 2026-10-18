using Azure;
using Azure.Communication.Sms;

namespace Cyllio.Identity;

internal sealed class OtpDelivery
{
    private readonly SmsClient? client;
    private readonly string? sender;
    private readonly DevelopmentOtpDelivery development;
    private readonly ILogger<OtpDelivery> logger;
    public bool IsDevelopment { get; }
    public bool IsAvailable => IsDevelopment || client is not null;

    public OtpDelivery(IConfiguration configuration, IWebHostEnvironment environment,
        DevelopmentOtpDelivery development, ILogger<OtpDelivery> logger)
        : this(configuration, environment, development, logger, null) { }

    internal OtpDelivery(IConfiguration configuration, IWebHostEnvironment environment,
        DevelopmentOtpDelivery development, ILogger<OtpDelivery> logger, SmsClient? smsClient)
    {
        this.development = development;
        this.logger = logger;
        var connectionString = configuration["Auth:Sms:ConnectionString"];
        sender = configuration["Auth:Sms:Sender"];
        // Partial ACS configuration fails closed; it must never reveal real SMS codes through /dev/otp.
        var azureConfigured = !string.IsNullOrWhiteSpace(connectionString) || !string.IsNullOrWhiteSpace(sender);
        IsDevelopment = !azureConfigured && environment.IsDevelopment() && configuration.GetValue<bool>("Auth:EnableDevelopmentOtp");
        if (!string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(sender))
        {
            var options = new SmsClientOptions();
            // A timeout may occur after ACS accepts a billable SMS. Do not automatically resend.
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            options.Diagnostics.IsLoggingContentEnabled = false;
            client = smsClient ?? new SmsClient(connectionString, options);
        }
    }

    public async Task<bool> SendAsync(string phoneNumber, string verificationId, string code, DateTime expiresAt, CancellationToken ct)
    {
        if (IsDevelopment)
        {
            development.Store(verificationId, code, expiresAt);
            return true;
        }
        if (client is null) return false;
        try
        {
            var result = await client.SendAsync(sender!, phoneNumber,
                $"Your Cyllio verification code is {code}. It expires in 5 minutes. Do not share this code.",
                cancellationToken: ct);
            // ACS can return HTTP 202 while rejecting an individual message.
            if (!result.Value.Successful)
                logger.LogWarning("ACS rejected verification SMS (status {Status}).", result.Value.HttpStatusCode);
            return result.Value.Successful;
        }
        catch (RequestFailedException error)
        {
            // Provider exception text may include phone numbers; log only the status.
            logger.LogWarning("ACS verification SMS request failed (status {Status}).", error.Status);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("ACS verification SMS request timed out.");
            return false;
        }
    }
}
