# FeatureManagement.UI

Drop-in admin UI for Microsoft.FeatureManagement flags (Fluent UI), with a provider-based API.

- `src/FeatureManagement.UI.Core` / `.Api`: schema, admin service and minimal API (`MapFeatureFlagAdmin`).
- `src/FeatureManagement.UI.Providers.*`: Json (appsettings), Sql (SQL Server/Postgres), DynamoDb, AwsAppConfig, AzureAppConfig.
- `src/FeatureManagement.UI.Blazor`: `<FeatureFlagAdmin />` component (Fluent UI Blazor).
- `src/FeatureManagement.UI.React`: `@featuremanagement/ui-react` (Fluent UI React v9).
- `samples/`: Aspire sample (ApiService backed by `appsettings.json` with 16 sample flags, plus Blazor, MVC, Razor Pages and React hosts).

## Run the samples

```
cd samples/Sample.React && npm install
dotnet run --project samples/Sample.AppHost
```

Open the Aspire dashboard and launch each app. The sample API allows anonymous access; secure it in real apps.

## Hosting in Blazor

Layout: add the Fluent bundle CSS link (`_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.bundle.scp.css`) and `<FluentProviders />`. Register with `AddFeatureFlagAdminUi(new Uri(".../api/feature-flags/"))`.
MVC / Razor Pages: `AddServerSideBlazor()`, `MapBlazorHub()`, load `_framework/blazor.server.js`, and render `<component type="typeof(FeatureManagement.UI.Blazor.FeatureFlagAdmin)" render-mode="Server" />`.

See `docs/feature-flags.md` for provider and filter details.
