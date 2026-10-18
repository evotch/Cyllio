using System.Net;
using Amazon;
using Amazon.PinpointSMSVoiceV2;
using Amazon.PinpointSMSVoiceV2.Model;
using Amazon.Runtime;
using PhoneNumbers;

namespace Cyllio.Identity;

internal sealed class OtpDelivery : IDisposable
{
    private static readonly PhoneNumberUtil PhoneNumbers = PhoneNumberUtil.GetInstance();
    private readonly IAmazonPinpointSMSVoiceV2? client;
    private readonly Dictionary<string, string> senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? configurationSet;
    private readonly bool ownsClient;
    private readonly DevelopmentOtpDelivery development;
    private readonly ILogger<OtpDelivery> logger;
    public bool IsDevelopment { get; }
    public bool IsAvailable => IsDevelopment || client is not null;

    public OtpDelivery(IConfiguration configuration, IWebHostEnvironment environment,
        DevelopmentOtpDelivery development, ILogger<OtpDelivery> logger)
        : this(configuration, environment, development, logger, null) { }

    internal OtpDelivery(IConfiguration configuration, IWebHostEnvironment environment,
        DevelopmentOtpDelivery development, ILogger<OtpDelivery> logger, IAmazonPinpointSMSVoiceV2? smsClient)
    {
        this.development = development;
        this.logger = logger;
        var section = configuration.GetSection("Auth:Sms:OriginationIdentities");
        foreach (var country in new[] { "US", "BG" })
            if (!string.IsNullOrWhiteSpace(section[country])) senders[country] = section[country]!;

        configurationSet = configuration["Auth:Sms:ConfigurationSet"];

        var region = configuration["Auth:Sms:Region"] ?? configuration["AWS_REGION"];

        // Explicit/partial AWS configuration (and obsolete ACS settings) must never expose codes via /dev/otp.
        var configured = configuration.GetValue<bool>("Auth:Sms:Enabled") || section.GetChildren().Any(child => !string.IsNullOrWhiteSpace(child.Value)) ||
            !string.IsNullOrWhiteSpace(configurationSet) || !string.IsNullOrWhiteSpace(configuration["Auth:Sms:ConnectionString"]) ||
            !string.IsNullOrWhiteSpace(configuration["Auth:Sms:Sender"]);

        IsDevelopment = !configured && environment.IsDevelopment() && configuration.GetValue<bool>("Auth:EnableDevelopmentOtp");

        if (senders.Count > 0 && !string.IsNullOrWhiteSpace(region) && !string.IsNullOrWhiteSpace(configurationSet))
        {
            // Credentials come from the AWS default chain (ECS task role in AWS), never ACS keys.
            client = smsClient ?? new AmazonPinpointSMSVoiceV2Client(new AmazonPinpointSMSVoiceV2Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(region),
                MaxErrorRetry = 0,
                Timeout = TimeSpan.FromSeconds(15),
                LogResponse = false,
                LogMetrics = false
            });
            ownsClient = smsClient is null;
        }
    }

    private string? SenderFor(string phoneNumber)
    {
        try
        {
            var number = PhoneNumbers.Parse(phoneNumber, null);
            var country = PhoneNumbers.GetRegionCodeForNumber(number);
            return PhoneNumbers.IsValidNumber(number) && country is not null && senders.TryGetValue(country, out var sender) ? sender : null;
        }
        catch (NumberParseException) { return null; }
    }

    public bool Supports(string phoneNumber) => IsDevelopment || SenderFor(phoneNumber) is not null;

    public async Task<bool> SendAsync(
        string phoneNumber,
        string verificationId,
        string code,
        DateTime expiresAt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsDevelopment)
        {
            development.Store(verificationId, code, expiresAt);
            return true;
        }

        var sender = SenderFor(phoneNumber);
        var remaining = (int)Math.Ceiling((expiresAt - DateTime.UtcNow).TotalSeconds);

        if (client is null || sender is null || remaining <= 0) return false;

        try
        {
            var result = await client.SendTextMessageAsync(new SendTextMessageRequest
            {
                DestinationPhoneNumber = phoneNumber,
                OriginationIdentity = sender,
                ConfigurationSetName = configurationSet,
                MessageType = MessageType.TRANSACTIONAL,
                MessageBody = $"Your Cyllio verification code is {code}. It expires in 5 minutes. Do not share this code.",
                TimeToLive = Math.Min(remaining, 300)
            }, ct);
            // Acceptance is not a delivery guarantee; delivery outcomes arrive through the configuration set.
            return result.HttpStatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices && !string.IsNullOrWhiteSpace(result.MessageId);
        }
        catch (AmazonServiceException error)
        {
            logger.LogWarning("AWS verification SMS request failed (HTTP {Status}).", (int)error.StatusCode);
            return false;
        }
        catch (AmazonClientException)
        {
            logger.LogWarning("AWS verification SMS client is unavailable.");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("AWS verification SMS request timed out.");
            return false;
        }
    }

    public void Dispose()
    {
        if (ownsClient) client?.Dispose();
    }
}
