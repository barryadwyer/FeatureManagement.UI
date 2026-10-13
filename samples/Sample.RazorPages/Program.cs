using FeatureManagement.UI.Blazor;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorPages();

// The Fluent UI Blazor admin page is hosted in this Razor Pages app as a Blazor Server component.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFeatureFlagAdminUi(new Uri("https+http://apiservice/api/feature-flags/"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<Sample.RazorPages.Components.App>().AddInteractiveServerRenderMode();
app.MapRazorPages().WithStaticAssets();
app.MapDefaultEndpoints();

app.Run();
