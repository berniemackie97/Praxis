using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence;

internal static class SqliteCommandExtensions
{
    internal static SqliteCommand CreateCommand(this SqliteConnection connection, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    /// <summary>Runs a parameterized statement inside <paramref name="transaction"/>; null values bind as NULL.</summary>
    internal static int Run(
        this SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(sql);
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }
}
