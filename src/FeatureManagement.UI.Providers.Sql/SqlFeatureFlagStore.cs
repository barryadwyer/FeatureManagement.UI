using System.Data.Common;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FeatureManagement.UI.Providers.Sql;

public enum SqlDialect
{
    SqlServer,
    Postgres,
}

public sealed partial class SqlFeatureFlagStoreOptions
{
    public required string ConnectionString { get; set; }
    public SqlDialect Dialect { get; set; }

    /// <summary>Table name, optionally schema-qualified. Must be a plain identifier.</summary>
    public string TableName { get; set; } = "FeatureFlags";

    /// <summary>Create the table on first use when it does not exist.</summary>
    public bool AutoCreateTable { get; set; } = true;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$")]
    internal static partial Regex IdentifierRegex();
}

/// <summary>
/// Stores one row per flag: Name, Document (Microsoft schema JSON) and Version (opaque ETag).
/// Works with SQL Server and PostgreSQL.
/// </summary>
public sealed class SqlFeatureFlagStore : IFeatureFlagStore
{
    private readonly SqlFeatureFlagStoreOptions _options;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public SqlFeatureFlagStore(SqlFeatureFlagStoreOptions options)
    {
        if (!SqlFeatureFlagStoreOptions.IdentifierRegex().IsMatch(options.TableName))
        {
            throw new ArgumentException("TableName must be a plain (optionally schema-qualified) identifier.", nameof(options));
        }

        _options = options;
    }

    private string Table => _options.TableName;

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, $"SELECT Document, Version FROM {Table}");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<FeatureFlag>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(FeatureFlagSchema.FromJson(JsonNode.Parse(reader.GetString(0))!, reader.GetString(1)));
        }

        return result;
    }

    public async Task<FeatureFlag?> GetAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var row = await ReadRowAsync(conn, name, ct);
        return row is null ? null : FeatureFlagSchema.FromJson(JsonNode.Parse(row.Value.Document)!, row.Value.Version);
    }

    public async Task<FeatureFlag> CreateAsync(FeatureFlag flag, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        if (await ReadRowAsync(conn, flag.Name, ct) is not null)
        {
            throw Exists(flag.Name);
        }

        var json = FeatureFlagSchema.ToJson(flag);
        var version = NewVersion();
        try
        {
            await using var cmd = Command(conn, $"INSERT INTO {Table} (Name, Document, Version) VALUES (@name, @doc, @ver)",
                ("name", flag.Name), ("doc", json.ToJsonString()), ("ver", version));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException)
        {
            if (await ReadRowAsync(conn, flag.Name, ct) is not null)
            {
                throw Exists(flag.Name);
            }

            throw;
        }

        return FeatureFlagSchema.FromJson(json, version);
    }

    public async Task<FeatureFlag> UpdateAsync(FeatureFlag flag, string? expectedETag, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var row = await ReadRowAsync(conn, flag.Name, ct) ?? throw new FeatureFlagNotFoundException(flag.Name);
        if (expectedETag is not null && expectedETag != row.Version)
        {
            throw Stale(flag.Name);
        }

        var json = FeatureFlagSchema.ToJson(flag, JsonNode.Parse(row.Document));
        var version = NewVersion();
        await using var cmd = Command(conn, $"UPDATE {Table} SET Document = @doc, Version = @ver WHERE Name = @name AND Version = @old",
            ("doc", json.ToJsonString()), ("ver", version), ("name", flag.Name), ("old", row.Version));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            throw Stale(flag.Name);
        }

        return FeatureFlagSchema.FromJson(json, version);
    }

    public async Task DeleteAsync(string name, string? expectedETag, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var row = await ReadRowAsync(conn, name, ct) ?? throw new FeatureFlagNotFoundException(name);
        if (expectedETag is not null && expectedETag != row.Version)
        {
            throw Stale(name);
        }

        await using var cmd = Command(conn, $"DELETE FROM {Table} WHERE Name = @name AND Version = @old",
            ("name", name), ("old", row.Version));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            throw Stale(name);
        }
    }

    private static string NewVersion() => Guid.NewGuid().ToString("N");

    private static FeatureFlagConflictException Exists(string name) => new($"Feature flag '{name}' already exists.");

    private static FeatureFlagConflictException Stale(string name) =>
        new($"Feature flag '{name}' was modified by someone else.");

    private async Task<(string Document, string Version)?> ReadRowAsync(DbConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = Command(conn, $"SELECT Document, Version FROM {Table} WHERE Name = @name", ("name", name));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static DbCommand Command(DbConnection conn, string sql, params (string Name, string Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        DbConnection conn = _options.Dialect == SqlDialect.SqlServer
            ? new SqlConnection(_options.ConnectionString)
            : new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        if (!_initialized && _options.AutoCreateTable)
        {
            await _initGate.WaitAsync(ct);
            try
            {
                if (!_initialized)
                {
                    var ddl = _options.Dialect == SqlDialect.SqlServer
                        ? $"IF OBJECT_ID(N'{Table}', N'U') IS NULL CREATE TABLE {Table} (Name nvarchar(128) NOT NULL PRIMARY KEY, Document nvarchar(max) NOT NULL, Version nvarchar(36) NOT NULL)"
                        : $"CREATE TABLE IF NOT EXISTS {Table} (Name varchar(128) NOT NULL PRIMARY KEY, Document text NOT NULL, Version varchar(36) NOT NULL)";
                    await using var cmd = Command(conn, ddl);
                    await cmd.ExecuteNonQueryAsync(ct);
                    _initialized = true;
                }
            }
            finally
            {
                _initGate.Release();
            }
        }

        return conn;
    }
}

public static class SqlFeatureFlagStoreExtensions
{
    public static IServiceCollection AddSqlServerFeatureFlagStore(
        this IServiceCollection services, string connectionString, Action<SqlFeatureFlagStoreOptions>? configure = null) =>
        Add(services, connectionString, SqlDialect.SqlServer, configure);

    public static IServiceCollection AddPostgresFeatureFlagStore(
        this IServiceCollection services, string connectionString, Action<SqlFeatureFlagStoreOptions>? configure = null) =>
        Add(services, connectionString, SqlDialect.Postgres, configure);

    private static IServiceCollection Add(
        IServiceCollection services, string connectionString, SqlDialect dialect, Action<SqlFeatureFlagStoreOptions>? configure)
    {
        var options = new SqlFeatureFlagStoreOptions { ConnectionString = connectionString, Dialect = dialect };
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlagStore>(new SqlFeatureFlagStore(options)));
        return services;
    }
}
