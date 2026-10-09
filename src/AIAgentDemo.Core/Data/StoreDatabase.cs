using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AIAgentDemo.Core.Data;

public sealed class StorageOptions
{
    /// <summary>Folder for the SQLite files (store + run log). Relative paths resolve against the working directory.</summary>
    public string DataDirectory { get; set; } = "App_Data";

    /// <summary>Recreate the demo store on startup so every session starts from the same seed.</summary>
    public bool ResetStoreOnStartup { get; set; } = true;
}

/// <summary>
/// The fictional "Nova Market" store database (SQLite). Agents only ever reach it through tools:
/// read tools use a <c>Mode=ReadOnly</c> connection, write actions go through <see cref="Tools.ActionTools"/>.
/// </summary>
public sealed class StoreDatabase(IOptions<StorageOptions> options, TimeProvider time)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "store.db");

    public string DatabasePath => _path;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Opens a connection that SQLite itself refuses to write through, whatever SQL is sent.</summary>
    public async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        await using (var connection = await OpenAsync(cancellationToken))
        {
            var exists = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'customers'") > 0;
            if (exists && !options.Value.ResetStoreOnStartup)
            {
                return;
            }
        }

        await ResetAsync(cancellationToken);
    }

    /// <summary>Drops and recreates every table with fresh seed data (dates are relative to now).</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(StoreSchema.DropAndCreate, transaction: transaction);
        await StoreSeed.InsertAsync(connection, transaction, time.GetUtcNow());
        await transaction.CommitAsync(cancellationToken);
    }
}
