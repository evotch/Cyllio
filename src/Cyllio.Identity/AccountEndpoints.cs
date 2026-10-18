using Cyllio.Common.Accounts;

namespace Cyllio.Identity;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/users/me", async (HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            var account = (await database.GetAccountAsync(context.User.UserId(), ct))!;
            context.Response.Headers.ETag = $"\"{account.ProfileVersion}\"";
            return Results.Ok(new { userId = account.Id, account.DisplayName, account.About, account.AvatarMediaId, version = account.ProfileVersion });
        });
        api.MapPatch("/users/me", async (ProfileUpdateRequest body, HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Length > 100 || body.About?.Length > 500 || body.AvatarMediaId?.Length > 128)
                return AccountApi.Invalid("Display name is required (max 100); about max 500 and avatar reference max 128 characters.");
            if (!TryVersion(context.Request, out var version)) return AccountApi.VersionRequired();
            Account? account = null;
            var changed = await database.MutateAccountAsync(context.User.UserId(), current =>
            {
                if (current.ProfileVersion != version) return false;
                current.DisplayName = body.DisplayName;
                current.About = body.About;
                current.AvatarMediaId = body.AvatarMediaId;
                current.ProfileVersion++;
                account = current;
                return true;
            }, ct);
            if (!changed) return Results.Problem(statusCode: 412, title: "Profile version changed");
            context.Response.Headers.ETag = $"\"{account!.ProfileVersion}\"";
            return Results.Ok(new { userId = account.Id, account.DisplayName, account.About, account.AvatarMediaId, version = account.ProfileVersion });
        });
        api.MapGet("/privacy/settings", async (HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            var account = (await database.GetAccountAsync(context.User.UserId(), ct))!;
            context.Response.Headers.ETag = $"\"{account.PrivacyVersion}\"";
            return Results.Ok(account.Privacy);
        });
        api.MapPut("/privacy/settings", async (PrivacySettings body, HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            if (!body.IsValid()) return AccountApi.Invalid("Invalid privacy preference value.");
            if (!TryVersion(context.Request, out var version)) return AccountApi.VersionRequired();
            Account? account = null;
            var changed = await database.MutateAccountAsync(context.User.UserId(), current =>
            {
                if (current.PrivacyVersion != version) return false;
                current.Privacy = body;
                current.PrivacyVersion++;
                account = current;
                return true;
            }, ct);
            if (!changed) return Results.Problem(statusCode: 412, title: "Privacy version changed");
            context.Response.Headers.ETag = $"\"{account!.PrivacyVersion}\"";
            return Results.Ok(account.Privacy);
        });
    }

    private static bool TryVersion(HttpRequest request, out long version) =>
        long.TryParse(request.Headers.IfMatch.ToString().Trim('"'), out version) && version > 0;
}
