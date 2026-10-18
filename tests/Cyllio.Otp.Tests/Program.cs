using Azure;
using Azure.Communication.Sms;
using Azure.Communication.Sms.Models;
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
var enabled = Config(("Auth:EnableDevelopmentOtp", "true"));
var delivery = new OtpDelivery(enabled, environment, development, logger);
Check(delivery.IsDevelopment && delivery.IsAvailable, "Explicit development mode must work.");
Check(await delivery.SendAsync("+12065550123", "local", "123456", DateTime.UtcNow.AddMinutes(5), default), "Local send failed.");
Check(development.Get("local") == "123456", "Development code was not stored.");
await delivery.SendAsync("+12065550123", "expired", "123456", DateTime.UtcNow.AddSeconds(-1), default);
Check(development.Get("expired") is null, "Expired code was exposed.");
environment.EnvironmentName = "Production";
Check(!new OtpDelivery(enabled, environment, development, logger).IsAvailable, "Production cannot use development delivery.");
environment.EnvironmentName = "Development";
foreach (var setting in new[] { "Auth:Sms:Sender", "Auth:Sms:ConnectionString" })
{
    var partial = new OtpDelivery(Config(("Auth:EnableDevelopmentOtp", "true"), (setting, "partial")), environment, development, logger);
    Check(!partial.IsDevelopment && !partial.IsAvailable, "Partial ACS settings must fail closed.");
}
var azureConfig = Config(("Auth:EnableDevelopmentOtp", "true"), ("Auth:Sms:Sender", "+12065550000"),
    ("Auth:Sms:ConnectionString", "test-only; injected client"));
foreach (var outcome in new[] { "accepted", "rejected", "exception", "timeout" })
{
    var client = new FakeSmsClient { Outcome = outcome };
    var azure = new OtpDelivery(azureConfig, environment, development, logger, client);
    Check(azure.IsAvailable && !azure.IsDevelopment, "ACS must override development mode.");
    var successful = await azure.SendAsync("+12065550123", outcome, "654321", DateTime.UtcNow.AddMinutes(5), default);
    Check(successful == (outcome == "accepted"), "Wrong result for " + outcome);
    Check(client.From == "+12065550000" && client.To == "+12065550123" && client.Message!.Contains("654321"), "Incorrect SMS payload.");
    Check(development.Get(outcome) is null, "ACS code leaked into development retrieval.");
}
var cancelClient = new FakeSmsClient { Outcome = "timeout" };
var cancelDelivery = new OtpDelivery(azureConfig, environment, development, logger, cancelClient);
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await cancelDelivery.SendAsync("+12065550123", "cancel", "123456", DateTime.UtcNow.AddMinutes(5), cancellation.Token);
    throw new Exception("Request cancellation must propagate.");
}
catch (OperationCanceledException) { }
Console.WriteLine("OTP checks passed: environment isolation, expiry, partial configuration, ACS acceptance/rejection, provider errors, timeouts, cancellation and code non-disclosure. No SMS sent.");

sealed class FakeSmsClient : SmsClient
{
    public string Outcome { get; init; } = "accepted";
    public string? From { get; private set; }
    public string? To { get; private set; }
    public string? Message { get; private set; }
    public override Task<Response<SmsSendResult>> SendAsync(string from, string to, string message,
        SmsSendOptions? options = null, CancellationToken cancellationToken = default)
    {
        From = from; To = to; Message = message;
        if (Outcome == "exception") throw new RequestFailedException(503, "private provider error");
        if (Outcome == "timeout") throw new OperationCanceledException();
        return Task.FromResult(Response.FromValue(SmsModelFactory.SmsSendResult(to, "test", 202, Outcome == "accepted", null), null!));
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
