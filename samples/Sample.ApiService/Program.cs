using FeatureManagement.UI.Api;
using FeatureManagement.UI.Providers.Json;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.FeatureFilters;
using static FeatureManagement.UI.FeatureFlagCoreServiceCollectionExtensions;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Evaluate flags with Microsoft.FeatureManagement from the same appsettings.json the admin API edits.
builder.Services.AddFeatureManagement()
    .AddFeatureFilter<ContextualTargetingFilter>()
    .AddFeatureFilter<PartnerTierFilter>();

// The sample runs without authentication; real apps should set AuthorizationPolicy instead.
builder.Services.AddFeatureFlagAdmin(api => api.AllowAnonymous = true);
builder.Services.AddJsonFeatureFlagStore(o => o.FilePath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json"));
builder.Services.AddHealthChecks().AddExpiredFeatureFlagsCheck();

// Browser-based samples (React dev server) call the API directly.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("ETag")));

var app = builder.Build();
app.UseCors();
app.MapFeatureFlagAdmin();

// Try the flags as a given user: /api/demo/evaluate?user=alice&groups=beta-testers,staff
app.MapGet("/api/demo/evaluate", async (IFeatureManager features, string? user, string? groups) =>
{
    var context = new TargetingContext
    {
        UserId = user,
        Groups = (groups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    var result = new SortedDictionary<string, bool>();
    await foreach (var name in features.GetFeatureNamesAsync())
    {
        result[name] = await features.IsEnabledAsync(name, context);
    }

    return result;
});

app.MapDefaultEndpoints();
app.Run();

// A custom filter the admin UI does not edit but preserves when saving flags that use it.
[FilterAlias("Contoso.PartnerTier")]
file sealed class PartnerTierFilter : IFeatureFilter
{
    public Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context) =>
        Task.FromResult(context.Parameters["Tier"] == "gold");
}
