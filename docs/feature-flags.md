# Feature flag admin UI

A drop-in admin page for [Microsoft.FeatureManagement](https://learn.microsoft.com/azure/azure-app-configuration/feature-management-dotnet-reference) flags.
One REST API with pluggable storage providers, and two thin UIs built on Fluent UI 2: React/TypeScript and Blazor.

Flags are written in the Microsoft `feature_management:feature_flags` schema, so `AddFeatureManagement()` reads them unchanged.
UI metadata (kind, owner, created, expires) is stored under `telemetry.metadata`.

| Kind | Intended lifetime | Default expiry |
|---|---|---|
| Release toggle | short | 90 days |
| Feature flag | long | none |

Expiry is advisory: the UI shows Expired / Expiring soon badges, and `AddExpiredFeatureFlagsCheck()` adds a health check. Nothing is switched off automatically.

Supported filters: Percentage, TimeWindow, Targeting (users, groups, default rollout %). Other filters (including custom ones) are preserved on save but not editable. Variants are out of scope.

## Server

```csharp
builder.Services.AddAuthentication(/* ... */);
builder.Services.AddAuthorization();
builder.Services.AddFeatureFlagAdmin(
    api => api.AuthorizationPolicy = "FeatureFlagAdmins",   // default: any authenticated user
    admin => admin.ReleaseToggleDefaultLifetime = TimeSpan.FromDays(90));

// choose ONE provider (see each Add*FeatureFlagStore overload for its options)
builder.Services.AddJsonFeatureFlagStore(/* path */);
// AddSqlServerFeatureFlagStore, AddPostgresFeatureFlagStore, AddDynamoDbFeatureFlagStore,
// AddAwsAppConfigFeatureFlagStore, AddAzureAppConfigFeatureFlagStore

builder.Services.AddHealthChecks().AddExpiredFeatureFlagsCheck();

app.UseAuthentication();
app.UseAuthorization();
app.MapFeatureFlagAdmin();   // /api/feature-flags
```

Routes: `GET /`, `GET /meta`, `GET /audit`, `GET|PUT|DELETE /{name}`, `POST /`.
Updates and deletes use optimistic concurrency (`ETag` response header, `If-Match` request header; 409 on conflict).
Every change is recorded in an audit log (in-memory by default; register your own `IFeatureFlagAuditLog` to persist it).

Notes: the JSON provider rewrites the file, so comments are lost. AWS AppConfig stores one hosted document.

## React

```tsx
import { FeatureFlagAdmin } from "@aspireopennimbus/feature-flags-ui";

<FluentProvider theme={webLightTheme}>
  <FeatureFlagAdmin baseUrl="/api/feature-flags" />
</FluentProvider>
```

Pass `headers={() => ({ Authorization: "Bearer ..." })}` for token auth. Requires `@fluentui/react-components` v9.

## Blazor

```csharp
builder.Services.AddFeatureFlagAdminUi(new Uri("https://host/api/feature-flags/"));
```

```razor
<FluentProviders />
<FeatureFlagAdmin />
```
