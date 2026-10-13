using FeatureManagement.UI.Blazor;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddControllersWithViews();

// The Fluent UI Blazor admin page is hosted in this MVC app as a Blazor Server component.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFeatureFlagAdminUi(new Uri("https+http://apiservice/api/feature-flags/"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<Sample.Mvc.Components.App>().AddInteractiveServerRenderMode();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();
app.MapDefaultEndpoints();

app.Run();
