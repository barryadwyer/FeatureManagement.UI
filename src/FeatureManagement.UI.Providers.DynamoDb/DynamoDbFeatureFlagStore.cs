using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeatureManagement.UI.Providers.DynamoDb;

public sealed class DynamoDbFeatureFlagStoreOptions
{
    public string TableName { get; set; } = "FeatureFlags";

    /// <summary>Create an on-demand table (partition key <c>Name</c>) when it does not exist.</summary>
    public bool AutoCreateTable { get; set; }
}

/// <summary>One item per flag: Name (key), Document (Microsoft schema JSON), Version (opaque ETag).</summary>
public sealed class DynamoDbFeatureFlagStore(IAmazonDynamoDB client, DynamoDbFeatureFlagStoreOptions options) : IFeatureFlagStore
{
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        var result = new List<FeatureFlag>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var page = await client.ScanAsync(new ScanRequest { TableName = options.TableName, ExclusiveStartKey = startKey }, ct);
            result.AddRange(page.Items.Select(Map));
            startKey = page.LastEvaluatedKey is { Count: > 0 } ? page.LastEvaluatedKey : null;
        }
        while (startKey is not null);

        return result;
    }

    public async Task<FeatureFlag?> GetAsync(string name, CancellationToken ct = default)
    {
        var item = await GetItemAsync(name, ct);
        return item is null ? null : Map(item);
    }

    public async Task<FeatureFlag> CreateAsync(FeatureFlag flag, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        var json = FeatureFlagSchema.ToJson(flag);
        var version = NewVersion();
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = options.TableName,
                Item = Item(flag.Name, json, version),
                ConditionExpression = "attribute_not_exists(#n)",
                ExpressionAttributeNames = new() { ["#n"] = "Name" },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new FeatureFlagConflictException($"Feature flag '{flag.Name}' already exists.");
        }

        return FeatureFlagSchema.FromJson(json, version);
    }

    public async Task<FeatureFlag> UpdateAsync(FeatureFlag flag, string? expectedETag, CancellationToken ct = default)
    {
        var existing = await GetItemAsync(flag.Name, ct) ?? throw new FeatureFlagNotFoundException(flag.Name);
        var oldVersion = existing["Version"].S;
        if (expectedETag is not null && expectedETag != oldVersion)
        {
            throw Stale(flag.Name);
        }

        var json = FeatureFlagSchema.ToJson(flag, JsonNode.Parse(existing["Document"].S));
        var version = NewVersion();
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = options.TableName,
                Item = Item(flag.Name, json, version),
                ConditionExpression = "Version = :old",
                ExpressionAttributeValues = new() { [":old"] = new AttributeValue(oldVersion) },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            throw Stale(flag.Name);
        }

        return FeatureFlagSchema.FromJson(json, version);
    }

    public async Task DeleteAsync(string name, string? expectedETag, CancellationToken ct = default)
    {
        var existing = await GetItemAsync(name, ct) ?? throw new FeatureFlagNotFoundException(name);
        var oldVersion = existing["Version"].S;
        if (expectedETag is not null && expectedETag != oldVersion)
        {
            throw Stale(name);
        }

        try
        {
            await client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = options.TableName,
                Key = new() { ["Name"] = new AttributeValue(name) },
                ConditionExpression = "Version = :old",
                ExpressionAttributeValues = new() { [":old"] = new AttributeValue(oldVersion) },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            throw Stale(name);
        }
    }

    private static string NewVersion() => Guid.NewGuid().ToString("N");

    private static FeatureFlagConflictException Stale(string name) =>
        new($"Feature flag '{name}' was modified by someone else.");

    private static FeatureFlag Map(Dictionary<string, AttributeValue> item) =>
        FeatureFlagSchema.FromJson(JsonNode.Parse(item["Document"].S)!, item["Version"].S);

    private static Dictionary<string, AttributeValue> Item(string name, JsonNode json, string version) => new()
    {
        ["Name"] = new AttributeValue(name),
        ["Document"] = new AttributeValue(json.ToJsonString()),
        ["Version"] = new AttributeValue(version),
    };

    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string name, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = options.TableName,
            Key = new() { ["Name"] = new AttributeValue(name) },
            ConsistentRead = true,
        }, ct);
        return response.IsItemSet ? response.Item : null;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_initialized || !options.AutoCreateTable)
        {
            return;
        }

        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                await client.CreateTableAsync(new CreateTableRequest
                {
                    TableName = options.TableName,
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                    AttributeDefinitions = [new AttributeDefinition("Name", ScalarAttributeType.S)],
                    KeySchema = [new KeySchemaElement("Name", KeyType.HASH)],
                }, ct);
            }
            catch (ResourceInUseException)
            {
                // Table already exists.
            }

            while ((await client.DescribeTableAsync(options.TableName, ct)).Table.TableStatus != TableStatus.ACTIVE)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }
}

public static class DynamoDbFeatureFlagStoreExtensions
{
    /// <summary>Uses a registered <see cref="IAmazonDynamoDB"/> when present, otherwise the default AWS credential chain.</summary>
    public static IServiceCollection AddDynamoDbFeatureFlagStore(
        this IServiceCollection services, Action<DynamoDbFeatureFlagStoreOptions>? configure = null)
    {
        var options = new DynamoDbFeatureFlagStoreOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlagStore>(sp =>
            new DynamoDbFeatureFlagStore(sp.GetService<IAmazonDynamoDB>() ?? new AmazonDynamoDBClient(), options)));
        return services;
    }
}
