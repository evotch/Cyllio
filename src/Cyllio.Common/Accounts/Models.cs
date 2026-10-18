namespace Cyllio.Common.Accounts;

public sealed class Account
{
    public long Revision { get; set; }
    public string Id { get; set; } = Guid.CreateVersion7().ToString();
    public string PhoneHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? About { get; set; }
    public string? AvatarMediaId { get; set; }
    public long ProfileVersion { get; set; } = 1;
    public long SecurityVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<AccountDevice> Devices { get; set; } = [];
    public List<AccountSession> Sessions { get; set; } = [];
    public PrivacySettings Privacy { get; set; } = new();
    public long PrivacyVersion { get; set; } = 1;
}

public sealed class AccountDevice
{
    public string Id { get; set; } = Guid.CreateVersion7().ToString();
    public string InstallationId { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Name { get; set; } = "";
    public string IdentityKey { get; set; } = "";
    public SignedPreKey SignedPreKey { get; set; } = new(0, "", "");
    public PushRegistration? Push { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}

public sealed class AccountSession
{
    public string Id { get; set; } = Guid.CreateVersion7().ToString();
    public string DeviceId { get; set; } = "";
    public string RefreshTokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class VerificationChallenge
{
    public long Revision { get; set; }
    public string Id { get; set; } = Guid.CreateVersion7().ToString();
    public string PhoneHash { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public string? ProofHash { get; set; }
    public string? InstallationId { get; set; }
    public string? Platform { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public sealed record SignedPreKey(int KeyId, string PublicKey, string Signature);
public sealed record PushRegistration(string Provider, string Token);
public sealed record VerificationStartRequest(string PhoneNumber, string Channel = "sms", string Locale = "en-US");
public sealed record VerificationDevice(string InstallationId, string Platform, string? AppVersion);
public sealed record VerificationConfirmRequest(string VerificationId, string Code, VerificationDevice Device);
public sealed record RegisterRequest(string DisplayName, string Name, string IdentityKey, SignedPreKey SignedPreKey, PushRegistration? Push);
public sealed record LoginRequest(string DeviceId);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ProfileUpdateRequest(string DisplayName, string? About, string? AvatarMediaId);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, string UserId, string DeviceId, string SessionId);
