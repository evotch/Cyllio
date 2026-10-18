using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cyllio.Common.Accounts;

// Strongly consistent primary-key lookups; conditional transactions serialize account changes.
public sealed class AccountDatabase : IDisposable
{
    private readonly AmazonDynamoDBClient client;
    private readonly string table;
    private readonly bool createTable;

    public AccountDatabase(IConfiguration configuration)
    {
        table = configuration["DynamoDb:TableName"] ?? "cyllio-local-entities";
        var endpoint = configuration["DynamoDb:ServiceUrl"];
        var config = new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(configuration["AWS_REGION"] ?? "us-west-2") };
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            // Dummy credentials belong only to the local database client, never the SMS client.
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                uri.Host is not ("localhost" or "127.0.0.1" or "dynamodb"))
                throw new InvalidOperationException("DynamoDb:ServiceUrl must reference DynamoDB Local.");
            config.ServiceURL = endpoint;
            client = new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"), config);
            createTable = true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(configuration["DynamoDb:TableName"]))
                throw new InvalidOperationException("DynamoDb:TableName is required for AWS.");
            client = new AmazonDynamoDBClient(config);
        }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await CheckAsync(ct); return; }
            catch (ResourceNotFoundException) when (createTable)
            {
                try
                {
                    await client.CreateTableAsync(new CreateTableRequest
                    {
                        TableName = table,
                        BillingMode = BillingMode.PAY_PER_REQUEST,
                        AttributeDefinitions = [new("PK", ScalarAttributeType.S), new("SK", ScalarAttributeType.S)],
                        KeySchema = [new("PK", KeyType.HASH), new("SK", KeyType.RANGE)]
                    }, ct);
                    await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
                    {
                        TableName = table,
                        TimeToLiveSpecification = new() { AttributeName = "expiresAt", Enabled = true }
                    }, ct);
                }
                catch (ResourceInUseException) { }
            }
            catch (AmazonServiceException) when (createTable && attempt < 30) { }
            if (attempt >= 30) throw new TimeoutException("DynamoDB Local did not become ready.");
            await Task.Delay(1000, ct);
        }
    }

    public async Task CheckAsync(CancellationToken ct) => await client.DescribeTableAsync(table, ct);
    private static Dictionary<string, AttributeValue> Key(string key) => new() { ["PK"] = new(key), ["SK"] = new("DATA") };
    private async Task<Dictionary<string, AttributeValue>?> ReadAsync(string key, CancellationToken ct)
    {
        var response = await client.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(key), ConsistentRead = true }, ct);
        return response.Item is { Count: > 0 } ? response.Item : null;
    }
    private async Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
    {
        var item = await ReadAsync(key, ct);
        if (item is null) return null;
        return JsonSerializer.Deserialize<T>(item["payload"].S);
    }
    public Task<Account?> GetAccountAsync(string? id, CancellationToken ct) => GetAsync<Account>("ACCOUNT#" + id, ct);
    public Task<VerificationChallenge?> GetChallengeAsync(string id, CancellationToken ct) => GetAsync<VerificationChallenge>("CHALLENGE#" + id, ct);
    private async Task<string?> LookupAsync(string key, CancellationToken ct) => (await ReadAsync(key, ct))?.GetValueOrDefault("target")?.S;
    public async Task<Account?> FindPhoneAsync(string hash, CancellationToken ct) => await GetAccountAsync(await LookupAsync("PHONE#" + hash, ct), ct);
    public async Task<Account?> FindRefreshAsync(string hash, CancellationToken ct) => await GetAccountAsync(await LookupAsync("REFRESH#" + hash, ct), ct);
    public async Task<VerificationChallenge?> FindProofAsync(string hash, CancellationToken ct)
    {
        var id = await LookupAsync("PROOF#" + hash, ct);
        return id is null ? null : await GetChallengeAsync(id, ct);
    }
    private TransactWriteItem Put(string key, object value, long revision, DateTime? expires = null)
    {
        var item = Key(key);
        item["payload"] = new(JsonSerializer.Serialize(value));
        item["revision"] = new() { N = (revision + 1).ToString(CultureInfo.InvariantCulture) };
        if (expires.HasValue) item["expiresAt"] = new() { N = new DateTimeOffset(expires.Value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
        return new()
        {
            Put = new()
            {
                TableName = table,
                Item = item,
                ConditionExpression = revision == 0 ? "attribute_not_exists(PK)" : "revision = :revision",
                ExpressionAttributeValues = revision == 0 ? null : new() { [":revision"] = new() { N = revision.ToString(CultureInfo.InvariantCulture) } }
            }
        };
    }
    private TransactWriteItem Link(string key, string target, DateTime? expires = null, bool unique = false)
    {
        var item = Key(key);
        item["target"] = new(target);
        if (expires.HasValue) item["expiresAt"] = new() { N = new DateTimeOffset(expires.Value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
        return new() { Put = new() { TableName = table, Item = item, ConditionExpression = unique ? "attribute_not_exists(PK)" : null } };
    }
    private async Task<bool> WriteAsync(List<TransactWriteItem> items, CancellationToken ct)
    {
        try
        {
            await client.TransactWriteItemsAsync(new() { TransactItems = items }, ct);
            return true;
        }
        catch (TransactionCanceledException ex) when (ex.CancellationReasons?.Any(x => x.Code is "ConditionalCheckFailed" or "TransactionConflict") == true) { return false; }
        catch (TransactionConflictException) { return false; }
    }
    public async Task<bool> StartChallengeAsync(VerificationChallenge challenge, CancellationToken ct)
    {
        var reservation = Link("COOLDOWN#" + challenge.PhoneHash, challenge.Id, challenge.CreatedAt.AddSeconds(31));
        reservation.Put.Item["availableAt"] = new() { N = new DateTimeOffset(challenge.CreatedAt.AddSeconds(30)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
        reservation.Put.ConditionExpression = "attribute_not_exists(PK) OR availableAt <= :now";
        reservation.Put.ExpressionAttributeValues = new() { [":now"] = new() { N = new DateTimeOffset(challenge.CreatedAt).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) } };
        challenge.Revision = 1;
        return await WriteAsync([reservation, Put("CHALLENGE#" + challenge.Id, challenge, 0, challenge.ExpiresAt)], ct);
    }
    public Task<bool> SaveChallengeAsync(VerificationChallenge challenge, CancellationToken ct)
    {
        var revision = challenge.Revision++;
        var writes = new List<TransactWriteItem> { Put("CHALLENGE#" + challenge.Id, challenge, revision, challenge.ExpiresAt) };
        if (challenge.ProofHash is not null) writes.Add(Link("PROOF#" + challenge.ProofHash, challenge.Id, challenge.ExpiresAt));
        return WriteAsync(writes, ct);
    }
    public async Task<bool> SaveAccountAsync(Account account, CancellationToken ct, VerificationChallenge? consume = null)
    {
        var previous = account.Revision == 0 ? null : await GetAccountAsync(account.Id, ct);
        var revision = account.Revision++;
        var writes = new List<TransactWriteItem> { Put("ACCOUNT#" + account.Id, account, revision) };
        if (revision == 0) writes.Add(Link("PHONE#" + account.PhoneHash, account.Id, unique: true));
        // Lookups may outlive rotation/revocation until TTL, but authorization always rechecks the account.
        foreach (var session in account.Sessions.Where(x => x.RevokedAt == null && x.ExpiresAt > DateTime.UtcNow &&
            previous?.Sessions.Any(old => old.RefreshTokenHash == x.RefreshTokenHash) != true))
            writes.Add(Link("REFRESH#" + session.RefreshTokenHash, account.Id, session.ExpiresAt));
        if (consume is not null)
        {
            consume.ConsumedAt = DateTime.UtcNow;
            var challengeRevision = consume.Revision++;
            var consumeWrite = Put("CHALLENGE#" + consume.Id, consume, challengeRevision, consume.ExpiresAt);
            consumeWrite.Put.ConditionExpression += " AND expiresAt > :now";
            consumeWrite.Put.ExpressionAttributeValues[":now"] = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
            writes.Add(consumeWrite);
        }
        return await WriteAsync(writes, ct);
    }
    public async Task<bool> MutateAccountAsync(string id, Func<Account, bool> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var account = await GetAccountAsync(id, ct);
            if (account is null || !change(account)) return false;
            if (await SaveAccountAsync(account, ct)) return true;
            await Task.Delay(Random.Shared.Next(5, 30), ct);
        }
        throw new TimeoutException("Account update contention.");
    }
    public void Dispose() => client.Dispose();
}

public sealed class AccountDatabaseHealthCheck(AccountDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { await database.CheckAsync(cancellationToken); return HealthCheckResult.Healthy(); }
        catch (Exception exception) when (exception is AmazonServiceException or TimeoutException)
        { return HealthCheckResult.Unhealthy("Account database is unavailable."); }
    }
}
