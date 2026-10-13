namespace FeatureManagement.UI;

/// <summary>Provider abstraction: persists flags in the Microsoft.FeatureManagement schema.</summary>
public interface IFeatureFlagStore
{
    Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default);

    Task<FeatureFlag?> GetAsync(string name, CancellationToken ct = default);

    /// <exception cref="FeatureFlagConflictException">A flag with that name exists.</exception>
    Task<FeatureFlag> CreateAsync(FeatureFlag flag, CancellationToken ct = default);

    /// <param name="expectedETag">When not null, must match the stored ETag.</param>
    /// <exception cref="FeatureFlagNotFoundException"/>
    /// <exception cref="FeatureFlagConflictException"/>
    Task<FeatureFlag> UpdateAsync(FeatureFlag flag, string? expectedETag, CancellationToken ct = default);

    /// <exception cref="FeatureFlagNotFoundException"/>
    /// <exception cref="FeatureFlagConflictException"/>
    Task DeleteAsync(string name, string? expectedETag, CancellationToken ct = default);
}

public interface IFeatureFlagAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> ListAsync(string? flagName, int take, CancellationToken ct = default);
}
