using Cyllio.Common.Accounts;

namespace Cyllio.Device;

internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var devices = app.MapGroup("/api/v1/devices").RequireAuthorization();
        devices.MapGet("/", async (HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            var account = (await database.GetAccountAsync(context.User.UserId(), ct))!;
            return Results.Ok(new
            {
                devices = account.Devices.Where(x => x.RevokedAt == null).Select(x =>
                new { deviceId = x.Id, x.InstallationId, x.Platform, x.Name, x.CreatedAt })
            });
        });
        devices.MapPut("/{deviceId}/push-token", async (string deviceId, PushRegistration body, HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            // Push capability updates belong to the authenticated account-specific device.
            if (deviceId != context.User.DeviceId()) return Results.NotFound();
            if (!DeviceValidation.ValidPush(body)) return AccountApi.Invalid("Use apns/fcm and a nonempty push token.");
            await database.MutateAccountAsync(context.User.UserId(), account =>
            {
                var device = account.Devices.SingleOrDefault(x => x.Id == deviceId && x.RevokedAt == null);
                if (device is null) return false;
                device.Push = body;
                return true;
            }, ct);
            return Results.NoContent();
        });
        devices.MapDelete("/{deviceId}", async (string deviceId, HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            var changed = await database.MutateAccountAsync(context.User.UserId(), account =>
            {
                var device = account.Devices.SingleOrDefault(x => x.Id == deviceId && x.RevokedAt == null);
                if (device is null) return false;
                device.RevokedAt = DateTime.UtcNow;
                foreach (var session in account.Sessions.Where(x => x.DeviceId == deviceId)) session.RevokedAt = DateTime.UtcNow;
                return true;
            }, ct);
            return changed ? Results.NoContent() : Results.NotFound();
        });
    }
}
