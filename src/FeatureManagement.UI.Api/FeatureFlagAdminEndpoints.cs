using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureManagement.UI.Api;

public sealed class FeatureFlagAdminApiOptions
{
    public string RoutePrefix { get; set; } = "/api/feature-flags";

    /// <summary>Authorization policy required for every endpoint; null requires an authenticated user.</summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>Disables authorization. Intended for local development only.</summary>
    public bool AllowAnonymous { get; set; }
}

public sealed record FeatureFlagAdminMetadata(
    int ReleaseToggleDefaultLifetimeDays,
    int? FeatureFlagDefaultLifetimeDays,
    int ExpiringSoonDays);

public static class FeatureFlagJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

public static class FeatureFlagAdminEndpoints
{
    /// <summary>Registers the admin service and API options. Add a provider (e.g. AddJsonFeatureFlagStore) too.</summary>
    public static IServiceCollection AddFeatureFlagAdmin(
        this IServiceCollection services,
        Action<FeatureFlagAdminApiOptions>? configureApi = null,
        Action<FeatureFlagAdminOptions>? configureAdmin = null)
    {
        var api = new FeatureFlagAdminApiOptions();
        configureApi?.Invoke(api);
        services.AddSingleton(api);
        services.AddAuthorization();
        return services.AddFeatureFlagAdminCore(configureAdmin);
    }

    public static RouteGroupBuilder MapFeatureFlagAdmin(this IEndpointRouteBuilder routes)
    {
        var api = routes.ServiceProvider.GetRequiredService<FeatureFlagAdminApiOptions>();
        var group = routes.MapGroup(api.RoutePrefix);
        if (api.AllowAnonymous)
        {
            group.AllowAnonymous();
        }
        else if (api.AuthorizationPolicy is { } policy)
        {
            group.RequireAuthorization(policy);
        }
        else
        {
            group.RequireAuthorization();
        }

        group.AddEndpointFilter(async (ctx, next) =>
        {
            try
            {
                return await next(ctx);
            }
            catch (FeatureFlagValidationException ex)
            {
                return Json(new { errors = ex.Errors }, StatusCodes.Status400BadRequest);
            }
            catch (FeatureFlagNotFoundException ex)
            {
                return Json(new { errors = new[] { ex.Message } }, StatusCodes.Status404NotFound);
            }
            catch (FeatureFlagConflictException ex)
            {
                return Json(new { errors = new[] { ex.Message } }, StatusCodes.Status409Conflict);
            }
        });

        group.MapGet("/", async (FeatureFlagAdminService svc, CancellationToken ct) =>
            Json(await svc.ListAsync(ct)));

        group.MapGet("/meta", (FeatureFlagAdminService svc) => Json(new FeatureFlagAdminMetadata(
            (int)svc.Options.ReleaseToggleDefaultLifetime.TotalDays,
            (int?)svc.Options.FeatureFlagDefaultLifetime?.TotalDays,
            (int)svc.Options.ExpiringSoonWindow.TotalDays)));

        group.MapGet("/audit", async (FeatureFlagAdminService svc, string? name, int? take, CancellationToken ct) =>
            Json(await svc.AuditAsync(name, Math.Clamp(take ?? 50, 1, 500), ct)));

        group.MapGet("/{name}", async (string name, FeatureFlagAdminService svc, HttpContext ctx, CancellationToken ct) =>
            await svc.GetAsync(name, ct) is { } flag ? WithETag(ctx, flag) : Json(new { errors = new[] { $"Feature flag '{name}' was not found." } }, 404));

        group.MapPost("/", async (HttpContext ctx, FeatureFlagAdminService svc, CancellationToken ct) =>
        {
            var flag = await ReadBody(ctx, ct);
            var created = await svc.CreateAsync(flag, User(ctx), ct);
            ctx.Response.Headers.Location = $"{api.RoutePrefix}/{Uri.EscapeDataString(created.Name)}";
            return WithETag(ctx, created, StatusCodes.Status201Created);
        });

        group.MapPut("/{name}", async (string name, HttpContext ctx, FeatureFlagAdminService svc, CancellationToken ct) =>
        {
            var flag = (await ReadBody(ctx, ct)) with { Name = name };
            var updated = await svc.UpdateAsync(flag, IfMatch(ctx), User(ctx), ct);
            return WithETag(ctx, updated);
        });

        group.MapDelete("/{name}", async (string name, HttpContext ctx, FeatureFlagAdminService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(name, IfMatch(ctx), User(ctx), ct);
            return Results.NoContent();
        });

        return group;
    }

    private static IResult Json(object value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, FeatureFlagJson.Options, statusCode: status);

    private static IResult WithETag(HttpContext ctx, FeatureFlag flag, int status = StatusCodes.Status200OK)
    {
        if (flag.ETag is not null)
        {
            ctx.Response.Headers.ETag = $"\"{flag.ETag}\"";
        }

        return Json(flag, status);
    }

    private static async Task<FeatureFlag> ReadBody(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync<FeatureFlag>(FeatureFlagJson.Options, ct)
                ?? throw new FeatureFlagValidationException(["Request body is required."]);
        }
        catch (JsonException ex)
        {
            throw new FeatureFlagValidationException([ex.Message]);
        }
    }

    private static string? IfMatch(HttpContext ctx)
    {
        var value = ctx.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        return value.Length == 0 || value == "*" ? null : value;
    }

    private static string User(HttpContext ctx) => ctx.User.Identity?.Name ?? "anonymous";
}
