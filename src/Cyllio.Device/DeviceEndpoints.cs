using Cyllio.Common.Accounts;
using MongoDB.Driver;

namespace Cyllio.Device;

internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var devices = app.MapGroup("/api/v1/devices").RequireAuthorization();
        devices.MapGet("/", async (HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            var account = await database.Accounts.Find(x => x.Id == context.User.UserId()).SingleAsync(ct);
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
            var filter = Builders<Account>.Filter.Eq(x => x.Id, context.User.UserId()) &
                Builders<Account>.Filter.ElemMatch(x => x.Devices, x => x.Id == deviceId && x.RevokedAt == null);
            await database.Accounts.UpdateOneAsync(filter, Builders<Account>.Update.Set("Devices.$.Push", body), cancellationToken: ct);
            return Results.NoContent();
        });
        devices.MapDelete("/{deviceId}", async (string deviceId, HttpContext context, AccountDatabase database, CancellationToken ct) =>
        {
            var filter = Builders<Account>.Filter.Eq(x => x.Id, context.User.UserId()) &
                Builders<Account>.Filter.ElemMatch(x => x.Devices, x => x.Id == deviceId && x.RevokedAt == null);
            var result = await database.Accounts.UpdateOneAsync(filter,
                Builders<Account>.Update.Set("Devices.$[device].RevokedAt", DateTime.UtcNow).Set("Sessions.$[session].RevokedAt", DateTime.UtcNow),
                new UpdateOptions
                {
                    ArrayFilters = [
                    new BsonDocumentArrayFilterDefinition<MongoDB.Bson.BsonDocument>(new("device._id", deviceId)),
                    new BsonDocumentArrayFilterDefinition<MongoDB.Bson.BsonDocument>(new("session.DeviceId", deviceId))]
                }, ct);
            return result.MatchedCount == 0 ? Results.NotFound() : Results.NoContent();
        });
    }
}
