using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeatureManagement.UI.Providers.Json;

public sealed class JsonFeatureFlagStoreOptions
{
    /// <summary>Path of the appsettings-style JSON file (e.g. appsettings.json).</summary>
    public string FilePath { get; set; } = "appsettings.json";
}

/// <summary>
/// Stores flags in <c>feature_management:feature_flags</c> of a JSON settings file. Other content is preserved,
/// but comments in the file are not.
/// </summary>
public sealed class JsonFeatureFlagStore(JsonFeatureFlagStoreOptions options) : IFeatureFlagStore
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default) =>
        await WithLock(async () => (await ReadFlagsAsync(ct)).Select(n => Map(n!)).ToList(), ct);

    public async Task<FeatureFlag?> GetAsync(string name, CancellationToken ct = default) =>
        await WithLock(async () =>
        {
            var node = Find(await ReadFlagsAsync(ct), name);
            return node is null ? null : Map(node);
        }, ct);

    public async Task<FeatureFlag> CreateAsync(FeatureFlag flag, CancellationToken ct = default) =>
        await WithLock(async () =>
        {
            var (root, array) = await ReadRootAsync(ct);
            if (Find(array, flag.Name) is not null)
            {
                throw new FeatureFlagConflictException($"Feature flag '{flag.Name}' already exists.");
            }

            var json = FeatureFlagSchema.ToJson(flag);
            array.Add(json);
            await WriteAsync(root, ct);
            return Map(json);
        }, ct);

    public async Task<FeatureFlag> UpdateAsync(FeatureFlag flag, string? expectedETag, CancellationToken ct = default) =>
        await WithLock(async () =>
        {
            var (root, array) = await ReadRootAsync(ct);
            var existing = Find(array, flag.Name) ?? throw new FeatureFlagNotFoundException(flag.Name);
            CheckETag(existing, expectedETag, flag.Name);
            var json = FeatureFlagSchema.ToJson(flag, existing);
            array[array.IndexOf(existing)] = json;
            await WriteAsync(root, ct);
            return Map(json);
        }, ct);

    public async Task DeleteAsync(string name, string? expectedETag, CancellationToken ct = default) =>
        await WithLock(async () =>
        {
            var (root, array) = await ReadRootAsync(ct);
            var existing = Find(array, name) ?? throw new FeatureFlagNotFoundException(name);
            CheckETag(existing, expectedETag, name);
            array.Remove(existing);
            await WriteAsync(root, ct);
            return 0;
        }, ct);

    private static FeatureFlag Map(JsonNode n) => FeatureFlagSchema.FromJson(n, FeatureFlagSchema.ComputeETag(n));

    private static JsonNode? Find(JsonArray array, string name) =>
        array.FirstOrDefault(n => string.Equals(n?["id"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

    private static void CheckETag(JsonNode existing, string? expected, string name)
    {
        if (expected is not null && expected != FeatureFlagSchema.ComputeETag(existing))
        {
            throw new FeatureFlagConflictException($"Feature flag '{name}' was modified by someone else.");
        }
    }

    private async Task<JsonArray> ReadFlagsAsync(CancellationToken ct) => (await ReadRootAsync(ct, create: false)).Array;

    private async Task<(JsonObject Root, JsonArray Array)> ReadRootAsync(CancellationToken ct, bool create = true)
    {
        JsonObject root = new();
        if (File.Exists(options.FilePath))
        {
            await using var stream = File.OpenRead(options.FilePath);
            root = JsonNode.Parse(stream, documentOptions: ReadOptions) as JsonObject ?? new JsonObject();
        }

        var section = root["feature_management"] as JsonObject;
        if (section is null)
        {
            section = new JsonObject();
            if (create)
            {
                root["feature_management"] = section;
            }
        }

        var array = section["feature_flags"] as JsonArray;
        if (array is null)
        {
            array = new JsonArray();
            if (create)
            {
                section["feature_flags"] = array;
            }
        }

        return (root, array);
    }

    private async Task WriteAsync(JsonObject root, CancellationToken ct)
    {
        var temp = options.FilePath + ".tmp";
        await File.WriteAllTextAsync(temp, root.ToJsonString(WriteOptions), ct);
        File.Move(temp, options.FilePath, overwrite: true);
    }

    private async Task<T> WithLock<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class JsonFeatureFlagStoreExtensions
{
    public static IServiceCollection AddJsonFeatureFlagStore(
        this IServiceCollection services, Action<JsonFeatureFlagStoreOptions>? configure = null)
    {
        var options = new JsonFeatureFlagStoreOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlagStore>(new JsonFeatureFlagStore(options)));
        return services;
    }
}
