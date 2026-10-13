using System.Globalization;

namespace FeatureManagement.UI.Blazor;

/// <summary>Mutable, form-friendly projection of <see cref="FeatureFlag"/>.</summary>
public sealed class FlagEditModel
{
    public bool IsNew { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public string Kind { get; set; } = nameof(FeatureKind.ReleaseToggle);
    public string? Owner { get; set; }
    public DateTime? ExpiresDate { get; set; }
    public string RequirementType { get; set; } = nameof(UI.RequirementType.Any);

    public bool UsePercentage { get; set; }
    public double? PercentageValue { get; set; }

    public bool UseTimeWindow { get; set; }
    public DateTime? WindowStart { get; set; }
    public DateTime? WindowEnd { get; set; }

    public bool UseTargeting { get; set; }
    public string UsersText { get; set; } = string.Empty;
    public string GroupsText { get; set; } = string.Empty;
    public double? DefaultRollout { get; set; } = 0;

    public FeatureFlag? Original { get; private set; }

    public static FlagEditModel ForNew(FeatureKind kind, FeatureFlagAdminMetadata? meta)
    {
        var m = new FlagEditModel { IsNew = true, Kind = kind.ToString() };
        if (kind == FeatureKind.ReleaseToggle && meta is not null)
        {
            m.ExpiresDate = DateTime.UtcNow.Date.AddDays(meta.ReleaseToggleDefaultLifetimeDays);
        }

        return m;
    }

    public static FlagEditModel From(FeatureFlag f) => new()
    {
        Original = f,
        Name = f.Name,
        Description = f.Description,
        Enabled = f.Enabled,
        Kind = f.Kind.ToString(),
        Owner = f.Owner,
        ExpiresDate = f.ExpiresUtc?.UtcDateTime,
        RequirementType = f.RequirementType.ToString(),
        UsePercentage = f.Percentage is not null,
        PercentageValue = f.Percentage?.Value,
        UseTimeWindow = f.TimeWindow is not null,
        WindowStart = f.TimeWindow?.Start?.UtcDateTime,
        WindowEnd = f.TimeWindow?.End?.UtcDateTime,
        UseTargeting = f.Targeting is not null,
        UsersText = string.Join('\n', f.Targeting?.Users ?? []),
        GroupsText = string.Join('\n', (f.Targeting?.Groups ?? [])
            .Select(g => $"{g.Name}:{g.RolloutPercentage.ToString(CultureInfo.InvariantCulture)}")),
        DefaultRollout = f.Targeting?.DefaultRolloutPercentage ?? 0,
    };

    public FeatureFlag ToFlag()
    {
        var users = UsersText.Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var groups = GroupsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseGroup).ToList();

        return (Original ?? new FeatureFlag { Name = Name }) with
        {
            Name = Name.Trim(),
            Description = Description,
            Enabled = Enabled,
            Kind = Enum.Parse<FeatureKind>(Kind),
            Owner = Owner,
            ExpiresUtc = ExpiresDate is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)) : null,
            RequirementType = Enum.Parse<UI.RequirementType>(RequirementType),
            Percentage = UsePercentage ? new PercentageFilter(PercentageValue ?? 0) : null,
            TimeWindow = UseTimeWindow ? new TimeWindowFilter(ToUtc(WindowStart), ToUtc(WindowEnd)) : null,
            Targeting = UseTargeting ? new TargetingFilter(users, groups, DefaultRollout ?? 0) : null,
        };
    }

    private static DateTimeOffset? ToUtc(DateTime? d) =>
        d is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v.Date, DateTimeKind.Utc)) : null;

    private static GroupRollout ParseGroup(string line)
    {
        var i = line.LastIndexOf(':');
        return i > 0 && double.TryParse(line[(i + 1)..], CultureInfo.InvariantCulture, out var pct)
            ? new GroupRollout(line[..i].Trim(), pct)
            : new GroupRollout(line, 100);
    }
}
