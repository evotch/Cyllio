using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cyllio.Common.Accounts;

namespace Cyllio.Identity;

internal static partial class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/verification/start", StartVerification).RequireRateLimiting("verification");
        auth.MapPost("/verification/confirm", ConfirmVerification).RequireRateLimiting("verification");
        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);
        auth.MapPost("/refresh", Refresh);
        auth.MapPost("/logout", Logout).RequireAuthorization();
        auth.MapPost("/logout-all", LogoutAll).RequireAuthorization();
        if (app.Services.GetRequiredService<OtpDelivery>().IsDevelopment)
            app.MapGet("/dev/otp/{verificationId}", (string verificationId, DevelopmentOtpDelivery delivery) =>
                delivery.Get(verificationId) is { } code ? Results.Ok(new { code }) : Results.NotFound());
    }

    private static async Task<IResult> StartVerification(VerificationStartRequest body, HttpContext context,
        AccountDatabase database, AccountTokens tokens, OtpDelivery delivery, CancellationToken cancellationToken)
    {
        if (body.PhoneNumber is null || !PhonePattern().IsMatch(body.PhoneNumber) || body.Channel is not ("sms" or "voice"))
            return AccountApi.Invalid("Use an E.164 phone number and sms or voice channel.");
        if (!delivery.IsAvailable)
            return Results.Problem(statusCode: 503, title: "OTP provider is not configured");
        if (body.Channel != "sms" && !delivery.IsDevelopment)
            return AccountApi.Invalid("Only sms verification is supported by the configured provider.");
        if (!delivery.Supports(body.PhoneNumber))
            return AccountApi.Invalid("SMS verification is not configured for this destination country.");
        var now = DateTime.UtcNow;
        var phoneHash = tokens.PhoneHash(body.PhoneNumber);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var challenge = new VerificationChallenge
        {
            // The per-phone DynamoDB reservation enforces cooldown across instances.
            PhoneHash = phoneHash,
            ExpiresAt = now.AddMinutes(5)
        };
        challenge.CodeHash = tokens.PhoneHash("otp:" + challenge.Id + ":" + code);
        if (!await database.StartChallengeAsync(challenge, cancellationToken))
        {
            context.Response.Headers.RetryAfter = "30";
            return Results.Problem(statusCode: 429, title: "Verification cooldown");
        }
        if (!await delivery.SendAsync(body.PhoneNumber, challenge.Id, code, challenge.ExpiresAt, cancellationToken))
        {
            // Keep the cooldown reservation, but make an undelivered code unusable.
            challenge.ExpiresAt = DateTime.UtcNow;
            await database.SaveChallengeAsync(challenge, cancellationToken);
            return Results.Problem(statusCode: 503, title: "SMS delivery unavailable", detail: "Please try again later.");
        }
        return Results.Accepted(value: new { verificationId = challenge.Id, challenge.ExpiresAt, retryAfterSeconds = 30 });
    }

    private static async Task<IResult> ConfirmVerification(VerificationConfirmRequest body, AccountDatabase database, AccountTokens tokens, CancellationToken cancellationToken)
    {
        if (body.Device is null || string.IsNullOrWhiteSpace(body.Device.InstallationId) || body.Device.InstallationId.Length > 128 ||
            body.Device.Platform is not ("ios" or "android") || body.Code is null || !CodePattern().IsMatch(body.Code))
            return AccountApi.Invalid("Provide a six-digit code and an ios/android installation.");
        var now = DateTime.UtcNow;
        // Persist each attempt before checking the code; concurrent attempts cannot exceed the limit.
        VerificationChallenge? challenge = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            challenge = await database.GetChallengeAsync(body.VerificationId, cancellationToken);
            if (challenge is null || challenge.ExpiresAt <= now || challenge.ConfirmedAt is not null || challenge.Attempts >= 5)
                return Results.Unauthorized();
            challenge.Attempts++;
            if (await database.SaveChallengeAsync(challenge, cancellationToken)) break;
            challenge = null;
        }
        if (challenge is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.CodeHash),
            Convert.FromHexString(tokens.PhoneHash("otp:" + challenge.Id + ":" + body.Code)))) return Results.Unauthorized();
        var proof = AccountTokens.Secret();
        challenge.ProofHash = AccountTokens.Hash(proof);
        challenge.ConfirmedAt = now;
        challenge.ExpiresAt = now.AddMinutes(5);
        challenge.InstallationId = body.Device.InstallationId;
        challenge.Platform = body.Device.Platform;
        return await database.SaveChallengeAsync(challenge, cancellationToken)
            ? Results.Ok(new { registrationToken = proof, expiresAt = challenge.ExpiresAt }) : Results.Unauthorized();
    }

    private static async Task<IResult> Register(RegisterRequest body, HttpRequest request, AccountDatabase database, AccountTokens tokens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Length > 100 || string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100 ||
            !DeviceValidation.ValidKeys(body.IdentityKey, body.SignedPreKey) || !DeviceValidation.ValidPush(body.Push))
            return AccountApi.Invalid("Provide a display name, device name, base64 public keys and a valid push registration.");
        var proofHash = ProofHash(request);
        if (proofHash is null) return Results.Unauthorized();
        var challenge = await ValidProof(database, proofHash, cancellationToken);
        if (challenge is null) return Results.Unauthorized();
        if (await database.FindPhoneAsync(challenge.PhoneHash, cancellationToken) is not null)
            return Results.Problem(statusCode: 409, title: "Account already registered");
        var device = new AccountDevice
        {
            InstallationId = challenge.InstallationId!,
            Platform = challenge.Platform!,
            Name = body.Name,
            IdentityKey = body.IdentityKey,
            SignedPreKey = body.SignedPreKey,
            Push = body.Push
        };
        var refresh = AccountTokens.Secret();
        var accountSession = NewSession(device.Id, refresh);
        var account = new Account { PhoneHash = challenge.PhoneHash, DisplayName = body.DisplayName, Devices = [device], Sessions = [accountSession] };
        if (!await database.SaveAccountAsync(account, cancellationToken, challenge))
            return Results.Unauthorized();
        return Results.Json(tokens.Issue(account, accountSession, refresh), statusCode: 201);
    }

    private static async Task<VerificationChallenge?> ValidProof(AccountDatabase database, string hash, CancellationToken ct)
    {
        var challenge = await database.FindProofAsync(hash, ct);
        return challenge is { ConsumedAt: null, ConfirmedAt: not null } && challenge.ExpiresAt > DateTime.UtcNow ? challenge : null;
    }

    private static async Task<IResult> Login(LoginRequest body, HttpRequest request, AccountDatabase database, AccountTokens tokens, CancellationToken cancellationToken)
    {
        var proofHash = ProofHash(request);
        if (proofHash is null || string.IsNullOrWhiteSpace(body.DeviceId)) return Results.Unauthorized();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var challenge = await ValidProof(database, proofHash, cancellationToken);
            if (challenge is null) return Results.Unauthorized();
            var account = await database.FindPhoneAsync(challenge.PhoneHash, cancellationToken);
            if (account is null || !account.Devices.Any(x => x.Id == body.DeviceId && x.InstallationId == challenge.InstallationId && x.RevokedAt == null))
                return Results.Unauthorized();
            var now = DateTime.UtcNow;
            account.Sessions.RemoveAll(x => x.RevokedAt != null || x.ExpiresAt <= now);
            if (account.Sessions.Count >= 100) return Results.Problem(statusCode: 409, title: "Session limit reached");
            var refresh = AccountTokens.Secret();
            var accountSession = NewSession(body.DeviceId, refresh);
            account.Sessions.Add(accountSession);
            if (await database.SaveAccountAsync(account, cancellationToken, challenge))
                return Results.Ok(tokens.Issue(account, accountSession, refresh));
        }
        return Results.Problem(statusCode: 503, title: "Account update contention");
    }

    private static async Task<IResult> Refresh(RefreshRequest body, AccountDatabase database, AccountTokens tokens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.RefreshToken) || body.RefreshToken.Length > 256) return Results.Unauthorized();
        var oldHash = AccountTokens.Hash(body.RefreshToken);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var account = await database.FindRefreshAsync(oldHash, cancellationToken);
            var accountSession = account?.Sessions.SingleOrDefault(x => x.RefreshTokenHash == oldHash && x.RevokedAt == null && x.ExpiresAt > DateTime.UtcNow);
            if (account is null || accountSession is null || !account.Devices.Any(x => x.Id == accountSession.DeviceId && x.RevokedAt == null)) return Results.Unauthorized();
            var refresh = AccountTokens.Secret();
            accountSession.RefreshTokenHash = AccountTokens.Hash(refresh);
            if (await database.SaveAccountAsync(account, cancellationToken))
                return Results.Ok(tokens.Issue(account, accountSession, refresh));
        }
        return Results.Problem(statusCode: 503, title: "Account update contention");
    }

    private static async Task<IResult> Logout(HttpContext context, AccountDatabase database, CancellationToken cancellationToken)
    {
        await database.MutateAccountAsync(context.User.UserId(), account =>
        {
            var session = account.Sessions.SingleOrDefault(x => x.Id == context.User.SessionId());
            if (session is null) return false;
            session.RevokedAt = DateTime.UtcNow;
            return true;
        }, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAll(HttpContext context, AccountDatabase database, CancellationToken cancellationToken)
    {
        await database.MutateAccountAsync(context.User.UserId(), account =>
        {
            foreach (var session in account.Sessions) session.RevokedAt = DateTime.UtcNow;
            account.SecurityVersion++;
            return true;
        }, cancellationToken);
        return Results.NoContent();
    }

    private static AccountSession NewSession(string deviceId, string refresh) => new()
    { DeviceId = deviceId, RefreshTokenHash = AccountTokens.Hash(refresh), ExpiresAt = DateTime.UtcNow.AddDays(30) };

    private static string? ProofHash(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var proof = authorization[7..];
        return proof.Length is > 0 and <= 256 ? AccountTokens.Hash(proof) : null;
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")] private static partial Regex PhonePattern();
    [GeneratedRegex(@"^[0-9]{6}$")] private static partial Regex CodePattern();
}
