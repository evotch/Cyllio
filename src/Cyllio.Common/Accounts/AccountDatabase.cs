using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;

namespace Cyllio.Common.Accounts;

public sealed class AccountDatabase
{
    public MongoClient Client { get; }
    public IMongoDatabase Database { get; }
    public IMongoCollection<Account> Accounts { get; }
    public IMongoCollection<VerificationChallenge> Challenges { get; }

    public AccountDatabase(IConfiguration configuration)
    {
        var connection = configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017/?replicaSet=rs0";
        var settings = MongoClientSettings.FromConnectionString(connection);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        Client = new MongoClient(settings);
        Database = Client.GetDatabase(configuration["Mongo:Database"] ?? "cyllio_accounts");
        Accounts = Database.GetCollection<Account>("accounts");
        Challenges = Database.GetCollection<VerificationChallenge>("verification_challenges");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Accounts.Indexes.CreateOneAsync(new CreateIndexModel<Account>(
            Builders<Account>.IndexKeys.Ascending(x => x.PhoneHash), new CreateIndexOptions { Unique = true }), cancellationToken: cancellationToken);
        await Accounts.Indexes.CreateOneAsync(new CreateIndexModel<Account>(
            Builders<Account>.IndexKeys.Ascending("Sessions.RefreshTokenHash")), cancellationToken: cancellationToken);
        await Challenges.Indexes.CreateOneAsync(new CreateIndexModel<VerificationChallenge>(
            Builders<VerificationChallenge>.IndexKeys.Ascending(x => x.ProofHash)), cancellationToken: cancellationToken);
        await Challenges.Indexes.CreateOneAsync(new CreateIndexModel<VerificationChallenge>(
            Builders<VerificationChallenge>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken);
        await Challenges.Indexes.CreateOneAsync(new CreateIndexModel<VerificationChallenge>(
            Builders<VerificationChallenge>.IndexKeys.Ascending(x => x.PhoneHash).Descending(x => x.CreatedAt)), cancellationToken: cancellationToken);
    }
}

public sealed class AccountDatabaseHealthCheck(AccountDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Account database is unavailable.");
        }
    }
}
