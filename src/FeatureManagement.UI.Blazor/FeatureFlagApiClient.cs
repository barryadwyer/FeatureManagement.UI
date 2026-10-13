using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureManagement.UI.Blazor;

public sealed record FeatureFlagAdminMetadata(
    int ReleaseToggleDefaultLifetimeDays,
    int? FeatureFlagDefaultLifetimeDays,
    int ExpiringSoonDays);

public sealed class FeatureFlagApiException(HttpStatusCode status, IReadOnlyList<string> errors)
    : Exception(errors.Count > 0 ? string.Join(" ", errors) : $"Request failed ({(int)status}).")
{
    public HttpStatusCode Status { get; } = status;
    public IReadOnlyList<string> Errors { get; } = errors;
    public bool IsConflict => Status == HttpStatusCode.Conflict;
}

/// <summary>Typed client for the feature flag admin REST API.</summary>
public sealed class FeatureFlagApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record ErrorBody(List<string>? Errors);

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default) =>
        await Send<List<FeatureFlag>>(HttpMethod.Get, "", null, null, ct) ?? [];

    public Task<FeatureFlagAdminMetadata?> GetMetadataAsync(CancellationToken ct = default) =>
        Send<FeatureFlagAdminMetadata>(HttpMethod.Get, "meta", null, null, ct);

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string? name = null, CancellationToken ct = default) =>
        await Send<List<AuditEntry>>(HttpMethod.Get, name is null ? "audit" : $"audit?name={Uri.EscapeDataString(name)}", null, null, ct) ?? [];

    public async Task<FeatureFlag> CreateAsync(FeatureFlag flag, CancellationToken ct = default) =>
        (await Send<FeatureFlag>(HttpMethod.Post, "", flag, null, ct))!;

    public async Task<FeatureFlag> UpdateAsync(FeatureFlag flag, CancellationToken ct = default) =>
        (await Send<FeatureFlag>(HttpMethod.Put, Uri.EscapeDataString(flag.Name), flag, flag.ETag, ct))!;

    public Task DeleteAsync(FeatureFlag flag, CancellationToken ct = default) =>
        Send<object>(HttpMethod.Delete, Uri.EscapeDataString(flag.Name), null, flag.ETag, ct);

    private async Task<T?> Send<T>(HttpMethod method, string path, object? body, string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            ErrorBody? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            }
            catch (JsonException)
            {
            }

            throw new FeatureFlagApiException(response.StatusCode, error?.Errors ?? []);
        }

        return response.StatusCode == HttpStatusCode.NoContent
            ? default
            : await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}

public static class FeatureFlagAdminUiExtensions
{
    /// <summary>
    /// Registers the API client (base address must end with the admin route, e.g. https://host/api/feature-flags/)
    /// and the Fluent UI services.
    /// </summary>
    public static IServiceCollection AddFeatureFlagAdminUi(
        this IServiceCollection services, Uri apiBaseAddress, Action<HttpClient>? configureClient = null)
    {
        var baseAddress = apiBaseAddress.AbsoluteUri.EndsWith('/') ? apiBaseAddress : new Uri(apiBaseAddress + "/");
        services.AddHttpClient<FeatureFlagApiClient>(c =>
        {
            c.BaseAddress = baseAddress;
            configureClient?.Invoke(c);
        });
        Microsoft.FluentUI.AspNetCore.Components.ServiceCollectionExtensions.AddFluentUIComponents(services);
        return services;
    }
}
