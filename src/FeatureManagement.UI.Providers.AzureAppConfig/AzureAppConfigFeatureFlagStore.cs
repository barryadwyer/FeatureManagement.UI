using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeatureManagement.UI.Providers.AzureAppConfig;

public sealed class AzureAppConfigFeatureFlagStoreOptions
{
    /// <summary>Optional label; null uses the unlabeled flags.</summary>
    public string? Label { get; set; }
}

/// <summary>Stores each flag as a native Azure App Configuration feature flag (<c>.appconfig.featureflag/{name}</c>).</summary>
public sealed class AzureAppConfigFeatureFlagStore(ConfigurationClient client, AzureAppConfigFeatureFlagStoreOptions options)
    : IFeatureFlagStore
{
    private const string Prefix = ".appconfig.featureflag/";
    private const string ContentType = "application/vnd.microsoft.appconfig.ff+json;charset=utf-8";

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<FeatureFlag>();
        var selector = new SettingSelector { KeyFilter = Prefix + "*", LabelFilter = options.Label ?? "\0" };
        await foreach (var setting in client.GetConfigurationSettingsAsync(selector, ct))
        {
            result.Add(Map(setting));
        }

        return result;
    }

    public async Task<FeatureFlag?> GetAsync(string name, CancellationToken ct = default)
    {
        var setting = await TryGetAsync(name, ct);
        return setting is null ? null : Map(setting);
    }

    public async Task<FeatureFlag> CreateAsync(FeatureFlag flag, CancellationToken ct = default)
    {
        var setting = Build(flag, null);
        try
        {
            var response = await client.AddConfigurationSettingAsync(setting, ct);
            return Map(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            throw new FeatureFlagConflictException($"Feature flag '{flag.Name}' already exists.");
        }
    }

    public async Task<FeatureFlag> UpdateAsync(FeatureFlag flag, string? expectedETag, CancellationToken ct = default)
    {
        var existing = await TryGetAsync(flag.Name, ct) ?? throw new FeatureFlagNotFoundException(flag.Name);
        var etag = expectedETag is null ? existing.ETag : new ETag(expectedETag);
        var setting = Build(flag, existing.Value, etag);
        try
        {
            var response = await client.SetConfigurationSettingAsync(setting, onlyIfUnchanged: true, ct);
            return Map(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            throw new FeatureFlagConflictException($"Feature flag '{flag.Name}' was modified by someone else.");
        }
    }

    public async Task DeleteAsync(string name, string? expectedETag, CancellationToken ct = default)
    {
        var existing = await TryGetAsync(name, ct) ?? throw new FeatureFlagNotFoundException(name);
        var etag = expectedETag is null ? existing.ETag : new ETag(expectedETag);
        try
        {
            var toDelete = new ConfigurationSetting(Prefix + name, existing.Value, options.Label, etag);
            await client.DeleteConfigurationSettingAsync(toDelete, onlyIfUnchanged: true, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            throw new FeatureFlagConflictException($"Feature flag '{name}' was modified by someone else.");
        }
    }

    private async Task<ConfigurationSetting?> TryGetAsync(string name, CancellationToken ct)
    {
        try
        {
            return (await client.GetConfigurationSettingAsync(Prefix + name, options.Label, ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private ConfigurationSetting Build(FeatureFlag flag, string? existingValue, ETag etag = default)
    {
        var existing = existingValue is null ? null : JsonNode.Parse(existingValue);
        return new ConfigurationSetting(Prefix + flag.Name, FeatureFlagSchema.ToJson(flag, existing).ToJsonString(), options.Label, etag)
        {
            ContentType = ContentType,
        };
    }

    private static FeatureFlag Map(ConfigurationSetting s) =>
        FeatureFlagSchema.FromJson(JsonNode.Parse(s.Value)!, s.ETag.ToString("H"));
}

public static class AzureAppConfigFeatureFlagStoreExtensions
{
    public static IServiceCollection AddAzureAppConfigFeatureFlagStore(
        this IServiceCollection services, Uri endpoint, TokenCredential credential,
        Action<AzureAppConfigFeatureFlagStoreOptions>? configure = null) =>
        services.AddAzureAppConfigFeatureFlagStore(new ConfigurationClient(endpoint, credential), configure);

    public static IServiceCollection AddAzureAppConfigFeatureFlagStore(
        this IServiceCollection services, ConfigurationClient client,
        Action<AzureAppConfigFeatureFlagStoreOptions>? configure = null)
    {
        var options = new AzureAppConfigFeatureFlagStoreOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlagStore>(new AzureAppConfigFeatureFlagStore(client, options)));
        return services;
    }
}
