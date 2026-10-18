using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Cyllio.Common.Accounts;

public sealed class AccountTokens
{
    public const string Issuer = "cyllio.identity";
    public const string Audience = "cyllio.services";
    public SymmetricSecurityKey SigningKey { get; }
    private readonly byte[] lookupKey;

    public AccountTokens(IConfiguration configuration)
    {
        var key = configuration["Auth:SigningKey"];
        var phoneKey = configuration["Auth:PhoneHashKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32 ||
            string.IsNullOrWhiteSpace(phoneKey) || Encoding.UTF8.GetByteCount(phoneKey) < 32 || key == phoneKey)
            throw new InvalidOperationException("Configure separate Auth:SigningKey and Auth:PhoneHashKey secrets of at least 32 bytes.");
        SigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        lookupKey = Encoding.UTF8.GetBytes(phoneKey);
    }

    public string PhoneHash(string phone) => Convert.ToHexString(HMACSHA256.HashData(lookupKey, Encoding.UTF8.GetBytes(phone)));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public TokenResponse Issue(Account account, AccountSession session, string refreshToken)
    {
        var expiry = DateTime.UtcNow.AddMinutes(10);
        var jwt = new JwtSecurityToken(Issuer, Audience,
            [new Claim("sub", account.Id), new Claim("deviceId", session.DeviceId), new Claim("sessionId", session.Id),
             new Claim("securityVersion", account.SecurityVersion.ToString()), new Claim("jti", Guid.NewGuid().ToString())],
            notBefore: DateTime.UtcNow, expires: expiry,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(jwt), refreshToken, expiry, account.Id, session.DeviceId, session.Id);
    }
}
