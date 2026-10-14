using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeatureManagement.UI;

public sealed class FeatureFlagAdminOptions
{
    /// <summary>Default lifetime applied to release toggles without an explicit expiry.</summary>
    public TimeSpan ReleaseToggleDefaultLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Default lifetime for long-lived feature flags; null means no expiry.</summary>
    public TimeSpan? FeatureFlagDefaultLifetime { get; set; }

    /// <summary>Flags expiring within this window are reported as <see cref="ExpiryStatus.ExpiringSoon"/>.</summary>
    public TimeSpan ExpiringSoonWindow { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Health status reported when expired flags exist.</summary>
    public HealthStatus ExpiredHealthStatus { get; set; } = HealthStatus.Degraded;
}

/// <summary>Validation, expiry defaults, auditing and ETag handling on top of an <see cref="IFeatureFlagStore"/>.</summary>
public sealed partial class FeatureFlagAdminService(
    IFeatureFlagStore store,
    IFeatureFlagAuditLog audit,
    IOptions<FeatureFlagAdminOptions> options,
    TimeProvider time)
{
    private readonly FeatureFlagAdminOptions _options = options.Value;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex NameRegex();

    public FeatureFlagAdminOptions Options => _options;

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default) =>
        (await store.ListAsync(ct)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(WithExpiry).ToList();

    public async Task<FeatureFlag?> GetAsync(string name, CancellationToken ct = default)
    {
        var flag = await store.GetAsync(name, ct);
        return flag is null ? null : WithExpiry(flag);
    }

    public async Task<FeatureFlag> CreateAsync(FeatureFlag flag, string user, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        flag = ApplyDefaults(flag with { CreatedUtc = now, ETag = null }, now);
        Validate(flag);
        var created = WithExpiry(await store.CreateAsync(flag, ct));
        await audit.AppendAsync(new AuditEntry(now, user, "Create", created.Name, null, created), ct);
        return created;
    }

    public async Task<FeatureFlag> UpdateAsync(FeatureFlag flag, string? expectedETag, string user, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var before = await store.GetAsync(flag.Name, ct) ?? throw new FeatureFlagNotFoundException(flag.Name);
        flag = ApplyDefaults(flag with { CreatedUtc = before.CreatedUtc ?? now, ETag = null }, now);
        Validate(flag);
        var updated = WithExpiry(await store.UpdateAsync(flag, expectedETag, ct));
        await audit.AppendAsync(new AuditEntry(now, user, "Update", updated.Name, WithExpiry(before), updated), ct);
        return updated;
    }

    public async Task DeleteAsync(string name, string? expectedETag, string user, CancellationToken ct = default)
    {
        var before = await store.GetAsync(name, ct) ?? throw new FeatureFlagNotFoundException(name);
        await store.DeleteAsync(name, expectedETag, ct);
        await audit.AppendAsync(new AuditEntry(time.GetUtcNow(), user, "Delete", name, WithExpiry(before), null), ct);
    }

    public Task<IReadOnlyList<AuditEntry>> AuditAsync(string? name, int take, CancellationToken ct = default) =>
        audit.ListAsync(name, take, ct);

    public ExpiryStatus GetExpiryStatus(FeatureFlag flag)
    {
        if (flag.ExpiresUtc is not { } expires)
        {
            return ExpiryStatus.None;
        }

        var now = time.GetUtcNow();
        return expires <= now ? ExpiryStatus.Expired
            : expires - now <= _options.ExpiringSoonWindow ? ExpiryStatus.ExpiringSoon
            : ExpiryStatus.Active;
    }

    private FeatureFlag WithExpiry(FeatureFlag f) => f with { Expiry = GetExpiryStatus(f) };

    private FeatureFlag ApplyDefaults(FeatureFlag flag, DateTimeOffset now)
    {
        // Release toggles are plain on/off switches; the built-in filters only apply to feature flags.
        if (flag.Kind == FeatureKind.ReleaseToggle)
        {
            flag = flag with { Percentage = null, TimeWindow = null, Targeting = null };
        }

        if (flag.ExpiresUtc is not null)
        {
            return flag;
        }

        TimeSpan? lifetime = flag.Kind == FeatureKind.ReleaseToggle
            ? _options.ReleaseToggleDefaultLifetime
            : _options.FeatureFlagDefaultLifetime;
        return lifetime is { } l ? flag with { ExpiresUtc = now + l } : flag;
    }

    public static void Validate(FeatureFlag flag)
    {
        var errors = new List<string>();
        if (!NameRegex().IsMatch(flag.Name ?? string.Empty))
        {
            errors.Add("Name must be 1-128 characters: letters, digits, '.', '_' or '-', starting with a letter or digit.");
        }

        if (flag.Percentage is { Value: < 0 or > 100 })
        {
            errors.Add("Percentage must be between 0 and 100.");
        }

        if (flag.TimeWindow is { Start: { } s, End: { } e } && s >= e)
        {
            errors.Add("Time window start must be before end.");
        }

        if (flag.Targeting is { } t)
        {
            if (t.DefaultRolloutPercentage is < 0 or > 100)
            {
                errors.Add("Default rollout percentage must be between 0 and 100.");
            }

            if (t.Groups.Any(g => string.IsNullOrWhiteSpace(g.Name) || g.RolloutPercentage is < 0 or > 100))
            {
                errors.Add("Each group needs a name and a rollout percentage between 0 and 100.");
            }

            if (t.Users.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add("User names must not be blank.");
            }
        }

        if (errors.Count > 0)
        {
            throw new FeatureFlagValidationException(errors);
        }
    }
}

/// <summary>Bounded, in-process audit log. Register your own <see cref="IFeatureFlagAuditLog"/> to persist.</summary>
public sealed class InMemoryFeatureFlagAuditLog : IFeatureFlagAuditLog
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAsync(string? flagName, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(_entries
            .Where(e => flagName is null || string.Equals(e.FlagName, flagName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.TimestampUtc)
            .Take(take)
            .ToList());
}

/// <summary>Reports (and logs) flags past their expiry so CI/monitoring can catch stale toggles.</summary>
public sealed class ExpiredFeatureFlagsHealthCheck(
    FeatureFlagAdminService service,
    ILogger<ExpiredFeatureFlagsHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var expired = (await service.ListAsync(ct)).Where(f => f.Expiry == ExpiryStatus.Expired).ToList();
        if (expired.Count == 0)
        {
            return HealthCheckResult.Healthy("No expired feature flags.");
        }

        foreach (var f in expired)
        {
            logger.LogWarning("Feature flag '{Name}' ({Kind}) expired at {Expires:u}; remove it or extend its expiry.",
                f.Name, f.Kind, f.ExpiresUtc);
        }

        return new HealthCheckResult(
            service.Options.ExpiredHealthStatus,
            $"{expired.Count} expired feature flag(s): {string.Join(", ", expired.Select(f => f.Name))}");
    }
}

public static class FeatureFlagCoreServiceCollectionExtensions
{
    /// <summary>Registers the admin service. A store must be registered separately by a provider package.</summary>
    public static IServiceCollection AddFeatureFlagAdminCore(
        this IServiceCollection services, Action<FeatureFlagAdminOptions>? configure = null)
    {
        var o = services.AddOptions<FeatureFlagAdminOptions>();
        if (configure is not null)
        {
            o.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFeatureFlagAuditLog, InMemoryFeatureFlagAuditLog>();
        services.TryAddSingleton<FeatureFlagAdminService>();
        return services;
    }

    public static IHealthChecksBuilder AddExpiredFeatureFlagsCheck(this IHealthChecksBuilder builder, string name = "expired-feature-flags") =>
        builder.AddCheck<ExpiredFeatureFlagsHealthCheck>(name);
}
