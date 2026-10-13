using System.Text.Json;

namespace FeatureManagement.UI;

/// <summary>Release toggles are short-lived; feature flags are long-lived.</summary>
public enum FeatureKind
{
    ReleaseToggle,
    FeatureFlag,
}

public enum ExpiryStatus
{
    None,
    Active,
    ExpiringSoon,
    Expired,
}

/// <summary>How multiple filters combine (Microsoft.FeatureManagement <c>requirement_type</c>).</summary>
public enum RequirementType
{
    Any,
    All,
}

/// <summary>Microsoft.Percentage filter.</summary>
public sealed record PercentageFilter(double Value);

/// <summary>Microsoft.TimeWindow filter.</summary>
public sealed record TimeWindowFilter(DateTimeOffset? Start, DateTimeOffset? End);

public sealed record GroupRollout(string Name, double RolloutPercentage);

/// <summary>Microsoft.Targeting filter audience.</summary>
public sealed record TargetingFilter(
    IReadOnlyList<string> Users,
    IReadOnlyList<GroupRollout> Groups,
    double DefaultRolloutPercentage);

public sealed record FeatureFlag
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public FeatureKind Kind { get; init; } = FeatureKind.ReleaseToggle;
    public string? Owner { get; init; }
    public DateTimeOffset? CreatedUtc { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public RequirementType RequirementType { get; init; }
    public PercentageFilter? Percentage { get; init; }
    public TimeWindowFilter? TimeWindow { get; init; }
    public TargetingFilter? Targeting { get; init; }

    /// <summary>Filters this UI does not edit; preserved on round trip.</summary>
    public IReadOnlyList<JsonElement> OtherFilters { get; init; } = [];

    /// <summary>Opaque concurrency token supplied by the provider.</summary>
    public string? ETag { get; init; }

    /// <summary>Computed by the admin service; ignored on write.</summary>
    public ExpiryStatus Expiry { get; init; }
}

public sealed record AuditEntry(
    DateTimeOffset TimestampUtc,
    string User,
    string Action,
    string FlagName,
    FeatureFlag? Before,
    FeatureFlag? After);

public class FeatureFlagStoreException(string message) : Exception(message);

public sealed class FeatureFlagNotFoundException(string name)
    : FeatureFlagStoreException($"Feature flag '{name}' was not found.");

/// <summary>Raised when a flag already exists or the supplied ETag is stale.</summary>
public sealed class FeatureFlagConflictException(string message) : FeatureFlagStoreException(message);

public sealed class FeatureFlagValidationException(IReadOnlyList<string> errors)
    : FeatureFlagStoreException("Feature flag is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
