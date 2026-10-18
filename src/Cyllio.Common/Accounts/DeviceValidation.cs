namespace Cyllio.Common.Accounts;

public static class DeviceValidation
{
    public static bool ValidKeys(string? identityKey, SignedPreKey? signedPreKey) =>
        signedPreKey is not null && signedPreKey.KeyId >= 0 && PublicKey(identityKey) &&
        PublicKey(signedPreKey.PublicKey) && Bytes(signedPreKey.Signature) == 64;

    public static bool ValidPush(PushRegistration? push) => push is null ||
        push.Provider is "apns" or "fcm" && !string.IsNullOrWhiteSpace(push.Token) && push.Token.Length <= 4096;

    private static bool PublicKey(string? value) => Bytes(value) is 32 or 33;
    private static int Bytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return -1;
        try { return Convert.FromBase64String(value).Length; }
        catch (FormatException) { return -1; }
    }
}
