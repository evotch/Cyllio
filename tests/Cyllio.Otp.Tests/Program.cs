using System.Net;
using Amazon;
using Amazon.PinpointSMSVoiceV2;
using Amazon.PinpointSMSVoiceV2.Model;
using Amazon.Runtime;
using Cyllio.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static IConfiguration Config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value))).Build();

var development = new DevelopmentOtpDelivery();
var logger = NullLogger<OtpDelivery>.Instance;
var environment = new TestEnvironment { EnvironmentName = "Development" };
var enabled = Config(("Auth:EnableDevelopmentOtp", "true"), ("Auth:Sms:OriginationIdentities:US", ""), ("Auth:Sms:OriginationIdentities:BG", ""));
using var delivery = new OtpDelivery(enabled, environment, development, logger);
Check(delivery.IsDevelopment && delivery.IsAvailable, "Empty Compose sender fields must preserve explicit development mode.");
Check(await delivery.SendAsync("+12065550123", "local", "123456", DateTime.UtcNow.AddMinutes(5), default), "Local send failed.");
Check(development.Get("local") == "123456", "Development code was not stored.");
await delivery.SendAsync("+12065550123", "expired", "123456", DateTime.UtcNow.AddSeconds(-1), default);
Check(development.Get("expired") is null, "Expired code was exposed.");
environment.EnvironmentName = "Production";
using (var production = new OtpDelivery(enabled, environment, development, logger))
    Check(!production.IsAvailable, "Production cannot use development delivery.");
environment.EnvironmentName = "Development";
foreach (var setting in new[] { "Auth:Sms:OriginationIdentities:US", "Auth:Sms:ConfigurationSet", "Auth:Sms:ConnectionString", "Auth:Sms:Sender" })
{
    using var partial = new OtpDelivery(Config(("Auth:EnableDevelopmentOtp", "true"), (setting, "partial")), environment, development, logger);
    Check(!partial.IsDevelopment && !partial.IsAvailable, "Partial AWS or obsolete ACS settings must fail closed.");
}
const string usSender = "arn:aws:sms-voice:us-west-2:123456789012:phone-number/phone-us";
const string bgSender = "arn:aws:sms-voice:us-west-2:123456789012:phone-number/phone-bg";
var awsConfig = Config(("Auth:EnableDevelopmentOtp", "true"), ("Auth:Sms:Region", "us-west-2"),
    ("Auth:Sms:ConfigurationSet", "cyllio-stg-otp"), ("Auth:Sms:OriginationIdentities:US", usSender), ("Auth:Sms:OriginationIdentities:BG", bgSender));
foreach (var outcome in new[] { "accepted", "rejected", "missing-id", "exception", "credentials", "timeout" })
{
    using var client = new FakeSmsClient { Outcome = outcome };
    using var aws = new OtpDelivery(awsConfig, environment, development, logger, client);
    Check(aws.IsAvailable && !aws.IsDevelopment, "AWS must override development mode.");
    var successful = await aws.SendAsync("+12065550123", outcome, "654321", DateTime.UtcNow.AddMinutes(5), default);
    Check(successful == (outcome == "accepted"), "Wrong result for " + outcome);
    var request = client.Request ?? throw new Exception("SDK did not receive a request.");
    Check(request.OriginationIdentity == usSender && request.DestinationPhoneNumber == "+12065550123" && request.MessageBody.Contains("654321"), "Incorrect US SMS payload.");
    Check(request.ConfigurationSetName == "cyllio-stg-otp" && request.MessageType == MessageType.TRANSACTIONAL && request.TimeToLive is > 0 and <= 300, "Missing transactional configuration or TTL.");
    Check(development.Get(outcome) is null, "AWS code leaked into development retrieval.");
}
using var countryClient = new FakeSmsClient();
using var countries = new OtpDelivery(awsConfig, environment, development, logger, countryClient);
Check(await countries.SendAsync("+359888123456", "bg", "123456", DateTime.UtcNow.AddMinutes(5), default), "Bulgarian send failed.");
Check(countryClient.Request?.OriginationIdentity == bgSender, "Bulgarian recipient used the US identity.");
Check(!countries.Supports("+14165550123"), "Canadian +1 number was incorrectly routed to the US short code.");
Check(!countries.Supports("+447700900123") && !countries.Supports("invalid"), "Unsupported/invalid country accepted.");
var calls = countryClient.Calls;
Check(!await countries.SendAsync("+14165550123", "ca", "123456", DateTime.UtcNow.AddMinutes(5), default), "Unsupported country sent SMS.");
Check(!await countries.SendAsync("+12065550123", "expired-live", "123456", DateTime.UtcNow.AddSeconds(-1), default), "Expired code sent SMS.");
Check(countryClient.Calls == calls, "Rejected requests reached the SDK.");
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await countries.SendAsync("+12065550123", "cancel", "123456", DateTime.UtcNow.AddMinutes(5), cancellation.Token);
    throw new Exception("Request cancellation must propagate.");
}
catch (OperationCanceledException) { }
Check(countryClient.Calls == calls, "Cancelled request reached the SDK.");
Console.WriteLine("OTP checks passed: environment isolation, expiry, partial/legacy configuration, US/BG routing, non-US +1 rejection, AWS acceptance/errors, TTL, cancellation and code non-disclosure. No SMS sent.");

sealed class FakeSmsClient : AmazonPinpointSMSVoiceV2Client
{
    public FakeSmsClient() : base(new AnonymousAWSCredentials(), RegionEndpoint.USWest2) { }
    public string Outcome { get; init; } = "accepted";
    public SendTextMessageRequest? Request { get; private set; }
    public int Calls { get; private set; }
    public override Task<SendTextMessageResponse> SendTextMessageAsync(SendTextMessageRequest request, CancellationToken cancellationToken = default)
    {
        Request = request; Calls++;
        if (Outcome == "exception") throw new AmazonPinpointSMSVoiceV2Exception("private provider error") { StatusCode = HttpStatusCode.Forbidden };
        if (Outcome == "credentials") throw new AmazonClientException("private credentials error");
        if (Outcome == "timeout") throw new OperationCanceledException();
        return Task.FromResult(new SendTextMessageResponse
        {
            HttpStatusCode = Outcome == "rejected" ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
            MessageId = Outcome == "missing-id" ? null : "test-message"
        });
    }
}
sealed class TestEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "OtpTests";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
