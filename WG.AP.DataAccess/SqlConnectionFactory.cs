using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace WG.AP.DataAccess;

/// <summary>
/// Opens connections to the AP database. One place, so the connection string and command timeout
/// are read consistently and every repository gets the same settings.
/// </summary>
public sealed class SqlConnectionFactory(IOptions<DatabaseOptions> options)
{
    // Every database call in this assembly opens its connection here, so this is the one place that
    // guarantees Dapper knows how to bind DateOnly before any query can run. Without it, the first
    // real invoice fails on a type the compiler was perfectly happy with.
    static SqlConnectionFactory() => DapperTypeHandlers.EnsureRegistered();

    public int CommandTimeoutSeconds => options.Value.CommandTimeoutSeconds;

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            // Otherwise a failure to open leaks the SqlConnection, and with it a pooled slot.
            await connection.DisposeAsync();
            throw;
        }
    }
}
