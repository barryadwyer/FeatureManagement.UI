using System.Text.Json.Nodes;
using Amazon.AppConfig;
using Amazon.AppConfig.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeatureManagement.UI.Providers.AwsAppConfig;

public sealed class AwsAppConfigFeatureFlagStoreOptions
{
    public required string ApplicationId { get; set; }

    /// <summary>A freeform configuration profile hosted in AWS AppConfig.</summary>
    public required string ConfigurationProfileId { get; set; }

    /// <summary>When both are set, every change starts a deployment of the new version.</summary>
    public string? EnvironmentId { get; set; }

    public string? DeploymentStrategyId { get; set; }
}

/// <summary>
/// Keeps all flags in one hosted configuration document (<c>feature_management.feature_flags</c>). The ETag of
/// every flag is the hosted version number, so any concurrent change to the profile is detected.
/// </summary>
public sealed class AwsAppConfigFeatureFlagStore(IAmazonAppConfig client, AwsAppConfigFeatureFlagStoreOptions options)
    : IFeatureFlagStore
{
    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default)
    {
        var (_, array, version) = await ReadAsync(ct);
        return array.Select(n => FeatureFlagSchema.FromJson(n!, version)).ToList();
    }

    public async Task<FeatureFlag?> GetAsync(string name, CancellationToken ct = default)
    {
        var (_, array, version) = await ReadAsync(ct);
        var node = Find(array, name);
        return node is null ? null : FeatureFlagSchema.FromJson(node, version);
    }

    public async Task<FeatureFlag> CreateAsync(FeatureFlag flag, CancellationToken ct = default)
    {
        var (root, array, version) = await ReadAsync(ct);
        if (Find(array, flag.Name) is not null)
        {
            throw new FeatureFlagConflictException($"Feature flag '{flag.Name}' already exists.");
        }

        var json = FeatureFlagSchema.ToJson(flag);
        array.Add(json);
        var newVersion = await WriteAsync(root, version, flag.Name, ct);
        return FeatureFlagSchema.FromJson(json, newVersion);
    }

    public async Task<FeatureFlag> UpdateAsync(FeatureFlag flag, string? expectedETag, CancellationToken ct = default)
    {
        var (root, array, version) = await ReadAsync(ct);
        var existing = Find(array, flag.Name) ?? throw new FeatureFlagNotFoundException(flag.Name);
        Check(expectedETag, version, flag.Name);
        var json = FeatureFlagSchema.ToJson(flag, existing);
        array[array.IndexOf(existing)] = json;
        var newVersion = await WriteAsync(root, version, flag.Name, ct);
        return FeatureFlagSchema.FromJson(json, newVersion);
    }

    public async Task DeleteAsync(string name, string? expectedETag, CancellationToken ct = default)
    {
        var (root, array, version) = await ReadAsync(ct);
        var existing = Find(array, name) ?? throw new FeatureFlagNotFoundException(name);
        Check(expectedETag, version, name);
        array.Remove(existing);
        await WriteAsync(root, version, name, ct);
    }

    private static JsonNode? Find(JsonArray array, string name) =>
        array.FirstOrDefault(n => string.Equals(n?["id"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

    private static void Check(string? expected, string actual, string name)
    {
        if (expected is not null && expected != actual)
        {
            throw new FeatureFlagConflictException($"Feature flag '{name}' was modified by someone else.");
        }
    }

    private async Task<(JsonObject Root, JsonArray Array, string Version)> ReadAsync(CancellationToken ct)
    {
        var list = await client.ListHostedConfigurationVersionsAsync(new ListHostedConfigurationVersionsRequest
        {
            ApplicationId = options.ApplicationId,
            ConfigurationProfileId = options.ConfigurationProfileId,
            MaxResults = 1,
        }, ct);

        var root = new JsonObject();
        var version = "0";
        if (list.Items is { Count: > 0 })
        {
            var latest = await client.GetHostedConfigurationVersionAsync(new GetHostedConfigurationVersionRequest
            {
                ApplicationId = options.ApplicationId,
                ConfigurationProfileId = options.ConfigurationProfileId,
                VersionNumber = list.Items[0].VersionNumber,
            }, ct);
            root = JsonNode.Parse(latest.Content) as JsonObject ?? new JsonObject();
            version = latest.VersionNumber.GetValueOrDefault().ToString();
        }

        var section = root["feature_management"] as JsonObject ?? new JsonObject();
        root["feature_management"] = section;
        var array = section["feature_flags"] as JsonArray ?? new JsonArray();
        section["feature_flags"] = array;
        return (root, array, version);
    }

    private async Task<string> WriteAsync(JsonObject root, string baseVersion, string name, CancellationToken ct)
    {
        try
        {
            var created = await client.CreateHostedConfigurationVersionAsync(new CreateHostedConfigurationVersionRequest
            {
                ApplicationId = options.ApplicationId,
                ConfigurationProfileId = options.ConfigurationProfileId,
                ContentType = "application/json",
                Content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())),
                LatestVersionNumber = int.Parse(baseVersion),
            }, ct);

            if (options.EnvironmentId is not null && options.DeploymentStrategyId is not null)
            {
                await client.StartDeploymentAsync(new StartDeploymentRequest
                {
                    ApplicationId = options.ApplicationId,
                    EnvironmentId = options.EnvironmentId,
                    DeploymentStrategyId = options.DeploymentStrategyId,
                    ConfigurationProfileId = options.ConfigurationProfileId,
                    ConfigurationVersion = created.VersionNumber.GetValueOrDefault().ToString(),
                }, ct);
            }

            return created.VersionNumber.GetValueOrDefault().ToString();
        }
        catch (ConflictException)
        {
            throw new FeatureFlagConflictException($"Feature flag '{name}' was modified by someone else.");
        }
    }
}

public static class AwsAppConfigFeatureFlagStoreExtensions
{
    /// <summary>Uses a registered <see cref="IAmazonAppConfig"/> when present, otherwise the default AWS credential chain.</summary>
    public static IServiceCollection AddAwsAppConfigFeatureFlagStore(
        this IServiceCollection services, string applicationId, string configurationProfileId,
        Action<AwsAppConfigFeatureFlagStoreOptions>? configure = null)
    {
        var options = new AwsAppConfigFeatureFlagStoreOptions
        {
            ApplicationId = applicationId,
            ConfigurationProfileId = configurationProfileId,
        };
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlagStore>(sp =>
            new AwsAppConfigFeatureFlagStore(sp.GetService<IAmazonAppConfig>() ?? new AmazonAppConfigClient(), options)));
        return services;
    }
}
