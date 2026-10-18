using System.Collections.Concurrent;

namespace Cyllio.Identity;

// Explicitly enabled local test provider. Never log codes or expose this route in production.
internal sealed class DevelopmentOtpDelivery
{
    private readonly ConcurrentDictionary<string, (string Code, DateTime ExpiresAt)> codes = new();
    public void Store(string id, string code, DateTime expiresAt)
    {
        foreach (var item in codes.Where(x => x.Value.ExpiresAt <= DateTime.UtcNow)) codes.TryRemove(item.Key, out _);
        codes[id] = (code, expiresAt);
    }
    public string? Get(string id) => codes.TryGetValue(id, out var entry) && entry.ExpiresAt > DateTime.UtcNow ? entry.Code : null;
}
