using FeatureManagement.UI.Blazor;
using Sample.Blazor.Components;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// "apiservice" is resolved by Aspire service discovery.
builder.Services.AddFeatureFlagAdminUi(new Uri("https+http://apiservice/api/feature-flags/"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

app.Run();
