using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cyllio.Common.Accounts;
using MongoDB.Driver;

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
        var now = DateTime.UtcNow;
        var phoneHash = tokens.PhoneHash(body.PhoneNumber);
        if (await database.Challenges.Find(x => x.PhoneHash == phoneHash && x.CreatedAt > now.AddSeconds(-30)).AnyAsync(cancellationToken))
        {
            context.Response.Headers.RetryAfter = "30";
            return Results.Problem(statusCode: 429, title: "Verification cooldown", detail: "Wait at least 30 seconds before retrying.");
        }
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var challenge = new VerificationChallenge
        {
            // A unique per-phone window also prevents concurrent requests bypassing the cooldown.
            Id = AccountTokens.Hash(phoneHash + ":" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30)),
            PhoneHash = phoneHash,
            ExpiresAt = now.AddMinutes(5)
        };
        challenge.CodeHash = tokens.PhoneHash("otp:" + challenge.Id + ":" + code);
        try { await database.Challenges.InsertOneAsync(challenge, cancellationToken: cancellationToken); }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { return Results.Problem(statusCode: 429, title: "Verification cooldown"); }
        if (!await delivery.SendAsync(body.PhoneNumber, challenge.Id, code, challenge.ExpiresAt, cancellationToken))
        {
            // Keep the cooldown reservation, but make an undelivered code unusable.
            await database.Challenges.UpdateOneAsync(x => x.Id == challenge.Id,
                Builders<VerificationChallenge>.Update.Set(x => x.ExpiresAt, DateTime.UtcNow), cancellationToken: cancellationToken);
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
        var challenge = await database.Challenges.FindOneAndUpdateAsync(
            x => x.Id == body.VerificationId && x.ExpiresAt > now && x.ConfirmedAt == null && x.Attempts < 5,
            Builders<VerificationChallenge>.Update.Inc(x => x.Attempts, 1),
            new FindOneAndUpdateOptions<VerificationChallenge> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        if (challenge is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.CodeHash),
            Convert.FromHexString(tokens.PhoneHash("otp:" + challenge.Id + ":" + body.Code)))) return Results.Unauthorized();
        var proof = AccountTokens.Secret();
        var updated = await database.Challenges.UpdateOneAsync(x => x.Id == challenge.Id && x.ConfirmedAt == null && x.Attempts == challenge.Attempts,
            Builders<VerificationChallenge>.Update.Set(x => x.ProofHash, AccountTokens.Hash(proof)).Set(x => x.ConfirmedAt, now)
                .Set(x => x.ExpiresAt, now.AddMinutes(5)).Set(x => x.InstallationId, body.Device.InstallationId).Set(x => x.Platform, body.Device.Platform),
            cancellationToken: cancellationToken);
        return updated.ModifiedCount == 1 ? Results.Ok(new { registrationToken = proof, expiresAt = now.AddMinutes(5) }) : Results.Unauthorized();
    }

    private static async Task<IResult> Register(RegisterRequest body, HttpRequest request, AccountDatabase database, AccountTokens tokens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Length > 100 || string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100 ||
            !DeviceValidation.ValidKeys(body.IdentityKey, body.SignedPreKey) || !DeviceValidation.ValidPush(body.Push))
            return AccountApi.Invalid("Provide a display name, device name, base64 public keys and a valid push registration.");
        var proofHash = ProofHash(request);
        if (proofHash is null) return Results.Unauthorized();
        using var transaction = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        try
        {
            return await transaction.WithTransactionAsync<IResult>(async (session, ct) =>
            {
                var now = DateTime.UtcNow;
                var challenge = await database.Challenges.Find(session, x => x.ProofHash == proofHash && x.ConsumedAt == null && x.ExpiresAt > now).FirstOrDefaultAsync(ct);
                if (challenge is null) return Results.Unauthorized();
                if (await database.Accounts.Find(session, x => x.PhoneHash == challenge.PhoneHash).AnyAsync(ct))
                    return Results.Problem(statusCode: 409, title: "Account already registered", detail: "Use the login flow for an existing account.");
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
                await database.Accounts.InsertOneAsync(session, account, cancellationToken: ct);
                await database.Challenges.UpdateOneAsync(session, x => x.Id == challenge.Id, Builders<VerificationChallenge>.Update.Set(x => x.ConsumedAt, now), cancellationToken: ct);
                return Results.Json(tokens.Issue(account, accountSession, refresh), statusCode: 201);
            }, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { return Results.Problem(statusCode: 409, title: "Account already registered"); }
    }

    private static async Task<IResult> Login(LoginRequest body, HttpRequest request, AccountDatabase database, AccountTokens tokens, CancellationToken cancellationToken)
    {
        var proofHash = ProofHash(request);
        if (proofHash is null || string.IsNullOrWhiteSpace(body.DeviceId)) return Results.Unauthorized();
        using var transaction = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await transaction.WithTransactionAsync<IResult>(async (session, ct) =>
        {
            var now = DateTime.UtcNow;
            var challenge = await database.Challenges.Find(session, x => x.ProofHash == proofHash && x.ConsumedAt == null && x.ExpiresAt > now).FirstOrDefaultAsync(ct);
            if (challenge is null) return Results.Unauthorized();
            var account = await database.Accounts.Find(session, x => x.PhoneHash == challenge.PhoneHash).FirstOrDefaultAsync(ct);
            if (account is null || !account.Devices.Any(x => x.Id == body.DeviceId && x.InstallationId == challenge.InstallationId && x.RevokedAt == null))
                return Results.Unauthorized();
            // New-device linking requires trusted-device approval and is not silently allowed by OTP login.
            account.Sessions.RemoveAll(x => x.RevokedAt != null || x.ExpiresAt <= now);
            if (account.Sessions.Count >= 100) return Results.Problem(statusCode: 409, title: "Session limit reached");
            var refresh = AccountTokens.Secret();
            var accountSession = NewSession(body.DeviceId, refresh);
            account.Sessions.Add(accountSession);
            await database.Accounts.UpdateOneAsync(session, x => x.Id == account.Id, Builders<Account>.Update.Set(x => x.Sessions, account.Sessions), cancellationToken: ct);
            await database.Challenges.UpdateOneAsync(session, x => x.Id == challenge.Id, Builders<VerificationChallenge>.Update.Set(x => x.ConsumedAt, now), cancellationToken: ct);
            return Results.Ok(tokens.Issue(account, accountSession, refresh));
        }, cancellationToken: cancellationToken);
    }

    private static async Task<IResult> Refresh(RefreshRequest body, AccountDatabase database, AccountTokens tokens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.RefreshToken) || body.RefreshToken.Length > 256) return Results.Unauthorized();
        var oldHash = AccountTokens.Hash(body.RefreshToken);
        var now = DateTime.UtcNow;
        using var transaction = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await transaction.WithTransactionAsync<IResult>(async (session, ct) =>
        {
            var account = await database.Accounts.Find(session, Builders<Account>.Filter.ElemMatch(x => x.Sessions,
                x => x.RefreshTokenHash == oldHash && x.RevokedAt == null && x.ExpiresAt > now)).FirstOrDefaultAsync(ct);
            var accountSession = account?.Sessions.SingleOrDefault(x => x.RefreshTokenHash == oldHash && x.RevokedAt == null && x.ExpiresAt > now);
            if (account is null || accountSession is null || !account.Devices.Any(x => x.Id == accountSession.DeviceId && x.RevokedAt == null)) return Results.Unauthorized();
            var refresh = AccountTokens.Secret();
            var update = Builders<Account>.Update.Set("Sessions.$[session].RefreshTokenHash", AccountTokens.Hash(refresh));
            var changed = await database.Accounts.UpdateOneAsync(session, x => x.Id == account.Id, update,
                new UpdateOptions { ArrayFilters = [new BsonDocumentArrayFilterDefinition<MongoDB.Bson.BsonDocument>(new("session._id", accountSession.Id))] }, ct);
            if (changed.ModifiedCount != 1) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(account, accountSession, refresh));
        }, cancellationToken: cancellationToken);
    }

    private static async Task<IResult> Logout(HttpContext context, AccountDatabase database, CancellationToken cancellationToken)
    {
        var sessionId = context.User.SessionId();
        await database.Accounts.UpdateOneAsync(x => x.Id == context.User.UserId(),
            Builders<Account>.Update.Set("Sessions.$[session].RevokedAt", DateTime.UtcNow),
            new UpdateOptions { ArrayFilters = [new BsonDocumentArrayFilterDefinition<MongoDB.Bson.BsonDocument>(new("session._id", sessionId))] }, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAll(HttpContext context, AccountDatabase database, CancellationToken cancellationToken)
    {
        await database.Accounts.UpdateOneAsync(x => x.Id == context.User.UserId(),
            Builders<Account>.Update.Set("Sessions.$[].RevokedAt", DateTime.UtcNow).Inc(x => x.SecurityVersion, 1), cancellationToken: cancellationToken);
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
