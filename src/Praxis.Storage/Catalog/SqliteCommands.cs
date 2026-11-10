using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Catalog;

internal static class SqliteCommands
{
    internal static SqliteCommand CreateCommand(this SqliteConnection connection, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }
}
