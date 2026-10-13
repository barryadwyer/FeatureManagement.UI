using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureManagement.UI;
using FeatureManagement.UI.Api;
using FeatureManagement.UI.Providers.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureManagement.UI.Tests;

public sealed class FeatureFlagTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"ff-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }

    private static FeatureFlag Sample(string name = "Beta", FeatureKind kind = FeatureKind.ReleaseToggle) => new()
    {
        Name = name,
        Enabled = true,
        Kind = kind,
        Owner = "team-a",
        RequirementType = RequirementType.All,
        Percentage = new PercentageFilter(25),
        TimeWindow = new TimeWindowFilter(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), DateTimeOffset.Parse("2030-02-01T00:00:00Z")),
        Targeting = new TargetingFilter(["alice"], [new GroupRollout("beta", 50)], 10),
    };

    private FeatureFlagAdminService Service(TimeProvider? time = null, Action<FeatureFlagAdminOptions>? configure = null)
    {
        var services = new ServiceCollection()
            .AddFeatureFlagAdminCore(configure)
            .AddJsonFeatureFlagStore(o => o.FilePath = _file);
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        return services.BuildServiceProvider().GetRequiredService<FeatureFlagAdminService>();
    }

    [Fact]
    public void Schema_round_trips_all_filters_and_preserves_unknown_ones()
    {
        var other = JsonSerializer.SerializeToElement(new { name = "Custom.Filter", parameters = new { a = 1 } });
        var flag = Sample() with { OtherFilters = [other] };
        var back = FeatureFlagSchema.FromJson(FeatureFlagSchema.ToJson(flag));

        Assert.Equal(flag.Percentage, back.Percentage);
        Assert.Equal(flag.TimeWindow, back.TimeWindow);
        Assert.Equal(["alice"], back.Targeting!.Users);
        Assert.Equal(new GroupRollout("beta", 50), back.Targeting.Groups.Single());
        Assert.Equal(10, back.Targeting.DefaultRolloutPercentage);
        Assert.Equal(RequirementType.All, back.RequirementType);
        Assert.Equal(FeatureKind.ReleaseToggle, back.Kind);
        Assert.Equal("team-a", back.Owner);
        Assert.Single(back.OtherFilters);
    }

    [Fact]
    public async Task Release_toggle_defaults_to_90_days_and_feature_flag_to_none()
    {
        var svc = Service();
        var toggle = await svc.CreateAsync(Sample("t"), "u");
        var flag = await svc.CreateAsync(Sample("f", FeatureKind.FeatureFlag), "u");

        Assert.Equal(toggle.CreatedUtc!.Value.AddDays(90), toggle.ExpiresUtc);
        Assert.Null(flag.ExpiresUtc);
        Assert.Equal(ExpiryStatus.Active, toggle.Expiry);
    }

    [Fact]
    public async Task Expiry_status_and_health_check_report_expired_flags()
    {
        var svc = Service();
        await svc.CreateAsync(Sample("old") with { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1) }, "u");
        await svc.CreateAsync(Sample("soon") with { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(3) }, "u");

        var all = await svc.ListAsync();
        Assert.Equal(ExpiryStatus.Expired, all.Single(f => f.Name == "old").Expiry);
        Assert.Equal(ExpiryStatus.ExpiringSoon, all.Single(f => f.Name == "soon").Expiry);

        var check = new ExpiredFeatureFlagsHealthCheck(svc, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExpiredFeatureFlagsHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Validation_rejects_bad_values()
    {
        var svc = Service();
        var ex = await Assert.ThrowsAsync<FeatureFlagValidationException>(() =>
            svc.CreateAsync(Sample("bad name!") with { Percentage = new PercentageFilter(150) }, "u"));
        Assert.Equal(2, ex.Errors.Count);
    }

    [Fact]
    public async Task Json_store_enforces_etags_and_preserves_other_settings()
    {
        await File.WriteAllTextAsync(_file, "{ // c\n \"Logging\": { \"Level\": \"Info\" } }");
        var svc = Service();
        var created = await svc.CreateAsync(Sample(), "u");
        await Assert.ThrowsAsync<FeatureFlagConflictException>(() => svc.CreateAsync(Sample(), "u"));

        var updated = await svc.UpdateAsync(created with { Enabled = false }, created.ETag, "u");
        Assert.False(updated.Enabled);
        await Assert.ThrowsAsync<FeatureFlagConflictException>(() => svc.UpdateAsync(created, created.ETag, "u"));
        await Assert.ThrowsAsync<FeatureFlagConflictException>(() => svc.DeleteAsync("Beta", created.ETag, "u"));

        await svc.DeleteAsync("Beta", updated.ETag, "u");
        Assert.Empty(await svc.ListAsync());
        Assert.Equal("Info", JsonNode.Parse(await File.ReadAllTextAsync(_file))!["Logging"]!["Level"]!.GetValue<string>());
        Assert.Equal(3, (await svc.AuditAsync(null, 10)).Count);
    }

    private async Task<HttpClient> Client(bool anonymous)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test").AddCookie("test");
        builder.Services.AddFeatureFlagAdmin(o => o.AllowAnonymous = anonymous);
        builder.Services.AddJsonFeatureFlagStore(o => o.FilePath = _file);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFeatureFlagAdmin();
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Api_requires_authorization_by_default()
    {
        var client = await Client(anonymous: false);
        var response = await client.GetAsync("/api/feature-flags");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Api_supports_crud_with_etag_concurrency()
    {
        var client = await Client(anonymous: true);
        var post = await client.PostAsJsonAsync("/api/feature-flags", Sample(), FeatureFlagJson.Options);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var etag = post.Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/feature-flags", Sample(), FeatureFlagJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/feature-flags", Sample("x y"), FeatureFlagJson.Options)).StatusCode);

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/feature-flags/Beta")
        {
            Content = JsonContent.Create(Sample() with { Enabled = false }, options: FeatureFlagJson.Options),
        };
        put.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put)).StatusCode);

        var stale = new HttpRequestMessage(HttpMethod.Put, "/api/feature-flags/Beta")
        {
            Content = JsonContent.Create(Sample(), options: FeatureFlagJson.Options),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(stale)).StatusCode);

        var list = await client.GetFromJsonAsync<List<FeatureFlag>>("/api/feature-flags", FeatureFlagJson.Options);
        Assert.False(list!.Single().Enabled);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/feature-flags/Beta")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/feature-flags/Beta")).StatusCode);
    }
}
