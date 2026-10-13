using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeatureManagement.UI;

/// <summary>
/// Maps <see cref="FeatureFlag"/> to/from a single item of the Microsoft feature flag schema
/// (<c>feature_management.feature_flags[]</c>). Management metadata lives in <c>telemetry.metadata</c>.
/// </summary>
public static class FeatureFlagSchema
{
    private const string PercentageName = "Microsoft.Percentage";
    private const string TimeWindowName = "Microsoft.TimeWindow";
    private const string TargetingName = "Microsoft.Targeting";

    public static FeatureFlag FromJson(JsonNode node, string? etag = null)
    {
        var obj = node.AsObject();
        var name = obj["id"]?.GetValue<string>() ?? throw new FormatException("Feature flag is missing 'id'.");
        var metadata = obj["telemetry"]?["metadata"] as JsonObject;

        var flag = new FeatureFlag
        {
            Name = name,
            Description = obj["description"]?.GetValue<string>(),
            Enabled = obj["enabled"]?.GetValue<bool>() ?? false,
            Kind = Enum.TryParse<FeatureKind>(Meta(metadata, "Kind"), true, out var k) ? k : FeatureKind.FeatureFlag,
            Owner = Meta(metadata, "Owner"),
            CreatedUtc = ParseDate(Meta(metadata, "CreatedUtc")),
            ExpiresUtc = ParseDate(Meta(metadata, "ExpiresUtc")),
            ETag = etag,
        };

        var conditions = obj["conditions"] as JsonObject;
        if (conditions is null)
        {
            return flag;
        }

        if (Enum.TryParse<RequirementType>(conditions["requirement_type"]?.GetValue<string>(), true, out var rt))
        {
            flag = flag with { RequirementType = rt };
        }

        var other = new List<JsonElement>();
        foreach (var f in conditions["client_filters"]?.AsArray() ?? [])
        {
            var parameters = f?["parameters"] as JsonObject;
            switch (ShortName(f?["name"]?.GetValue<string>()))
            {
                case "Percentage":
                    flag = flag with { Percentage = new PercentageFilter(Num(parameters?["Value"])) };
                    break;
                case "TimeWindow":
                    flag = flag with
                    {
                        TimeWindow = new TimeWindowFilter(
                            ParseDate(parameters?["Start"]?.ToString()),
                            ParseDate(parameters?["End"]?.ToString())),
                    };
                    break;
                case "Targeting":
                    flag = flag with { Targeting = ReadTargeting(parameters?["Audience"] as JsonObject) };
                    break;
                default:
                    if (f is not null)
                    {
                        other.Add(JsonSerializer.SerializeToElement(f));
                    }

                    break;
            }
        }

        return flag with { OtherFilters = other };
    }

    /// <summary>Builds the schema item; unknown properties of <paramref name="existing"/> are preserved.</summary>
    public static JsonObject ToJson(FeatureFlag flag, JsonNode? existing = null)
    {
        var obj = existing?.DeepClone() as JsonObject ?? new JsonObject();
        obj["id"] = flag.Name;
        obj["description"] = flag.Description;
        obj["enabled"] = flag.Enabled;

        var filters = new JsonArray();
        if (flag.Percentage is { } p)
        {
            filters.Add(Filter(PercentageName, new JsonObject { ["Value"] = p.Value }));
        }

        if (flag.TimeWindow is { } t)
        {
            var tw = new JsonObject();
            if (t.Start is { } s)
            {
                tw["Start"] = s.ToString("o", CultureInfo.InvariantCulture);
            }

            if (t.End is { } e)
            {
                tw["End"] = e.ToString("o", CultureInfo.InvariantCulture);
            }

            filters.Add(Filter(TimeWindowName, tw));
        }

        if (flag.Targeting is { } tg)
        {
            var audience = new JsonObject
            {
                ["Users"] = new JsonArray(tg.Users.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()),
                ["Groups"] = new JsonArray(tg.Groups.Select(g => (JsonNode)new JsonObject
                {
                    ["Name"] = g.Name,
                    ["RolloutPercentage"] = g.RolloutPercentage,
                }).ToArray()),
                ["DefaultRolloutPercentage"] = tg.DefaultRolloutPercentage,
            };
            filters.Add(Filter(TargetingName, new JsonObject { ["Audience"] = audience }));
        }

        foreach (var o in flag.OtherFilters)
        {
            filters.Add(JsonNode.Parse(o.GetRawText()));
        }

        var conditions = obj["conditions"] as JsonObject ?? new JsonObject();
        conditions["client_filters"] = filters;
        if (flag.RequirementType == RequirementType.All)
        {
            conditions["requirement_type"] = "All";
        }
        else
        {
            conditions.Remove("requirement_type");
        }

        obj["conditions"] = conditions;

        var telemetry = obj["telemetry"] as JsonObject ?? new JsonObject();
        var metadata = telemetry["metadata"] as JsonObject ?? new JsonObject();
        metadata["Kind"] = flag.Kind.ToString();
        SetOrRemove(metadata, "Owner", flag.Owner);
        SetOrRemove(metadata, "CreatedUtc", flag.CreatedUtc?.ToString("o", CultureInfo.InvariantCulture));
        SetOrRemove(metadata, "ExpiresUtc", flag.ExpiresUtc?.ToString("o", CultureInfo.InvariantCulture));
        telemetry["metadata"] = metadata;
        obj["telemetry"] = telemetry;
        return obj;
    }

    /// <summary>Deterministic content hash usable as an ETag by providers without native versioning.</summary>
    public static string ComputeETag(JsonNode node) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString()))).ToLowerInvariant();

    private static JsonObject Filter(string name, JsonObject parameters) =>
        new() { ["name"] = name, ["parameters"] = parameters };

    private static TargetingFilter ReadTargeting(JsonObject? audience) => new(
        audience?["Users"]?.AsArray().Select(u => u!.GetValue<string>()).ToList() ?? [],
        audience?["Groups"]?.AsArray()
            .Select(g => new GroupRollout(g!["Name"]!.GetValue<string>(), Num(g["RolloutPercentage"]))).ToList() ?? [],
        Num(audience?["DefaultRolloutPercentage"]));

    private static string ShortName(string? name) =>
        name is null ? string.Empty : name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ? name[10..] : name;

    private static double Num(JsonNode? n) =>
        n is null ? 0 : double.Parse(n.ToString(), CultureInfo.InvariantCulture);

    private static string? Meta(JsonObject? metadata, string key) => metadata?[key]?.ToString();

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static void SetOrRemove(JsonObject o, string key, string? value)
    {
        if (value is null)
        {
            o.Remove(key);
        }
        else
        {
            o[key] = value;
        }
    }
}
