namespace Cyllio.Common.Accounts;

public sealed record AudiencePreference(string From = "contacts");
public sealed record MessagePreference(string From = "contacts", string Unknown = "requests");
public sealed record CallPreference(string From = "contacts", string Unknown = "silence", string Voice = "contacts", string Video = "contacts");
public sealed record GroupPreference(string Invites = "contacts");
public sealed record PresencePreference(string Online = "contacts", string LastSeen = "contacts", string Typing = "contacts");
public sealed record ProfilePreference(string Photo = "contacts", string About = "contacts");
public sealed record ReceiptPreference(bool Read = true);
public sealed record DiscoveryPreference(string PhoneNumber = "contacts", bool Username = false);
public sealed record MediaPreference(string AutoDownload = "wifi");

public sealed class PrivacySettings
{
    public MessagePreference Messages { get; set; } = new();
    public CallPreference Calls { get; set; } = new();
    public AudiencePreference VoiceMessages { get; set; } = new();
    public GroupPreference Groups { get; set; } = new();
    public PresencePreference Presence { get; set; } = new();
    public ProfilePreference Profile { get; set; } = new();
    public ReceiptPreference Receipts { get; set; } = new();
    public DiscoveryPreference Discovery { get; set; } = new();
    public bool LinkPreviews { get; set; } = false;
    public MediaPreference Media { get; set; } = new();

    public bool IsValid()
    {
        if (Messages is null || Calls is null || VoiceMessages is null || Groups is null || Presence is null ||
            Profile is null || Receipts is null || Discovery is null || Media is null) return false;
        string[] audiences = [Messages.From, Calls.From, Calls.Voice, Calls.Video, VoiceMessages.From,
            Groups.Invites, Presence.Online, Presence.LastSeen, Presence.Typing, Profile.Photo, Profile.About, Discovery.PhoneNumber];
        return audiences.All(value => value is "everyone" or "contacts" or "nobody") &&
            Messages.Unknown is "allow" or "requests" or "block" &&
            Calls.Unknown is "ring" or "silence" or "block" &&
            Media.AutoDownload is "never" or "wifi" or "wifi_and_cellular";
    }
}
