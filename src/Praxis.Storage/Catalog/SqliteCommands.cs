using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

internal static class SqliteCommands
{
    internal static SqliteCommand CreateCommand(this SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }
}
