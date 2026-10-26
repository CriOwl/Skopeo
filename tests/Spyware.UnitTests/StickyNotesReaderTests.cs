using Microsoft.Data.Sqlite;
using Spyware.Collectors.Notes;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Xunit;

namespace Spyware.UnitTests;

/// <summary>
/// Lectura de `plum.sqlite` con esquema descubierto en tiempo de ejecución: las columnas
/// reales se leen con PRAGMA, así que el mismo código sirve para el esquema moderno
/// (`Text`) y para el de Sticky Notes 3.x (`plainText` / `formattedText`).
/// </summary>
public sealed class StickyNotesReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spyware-notes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Plum_EsquemaModerno_TextoFechasYHash()
    {
        DateTime created = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
        DateTime updated = created.AddMinutes(1);

        string database = Create("moderno.sqlite",
            "CREATE TABLE Note (id TEXT PRIMARY KEY, version INTEGER, Text TEXT, createdAt INTEGER," +
            " updatedAt INTEGER, isStickyDeleted INTEGER, Theme TEXT);",
            $"INSERT INTO Note VALUES ('aaaa1111-1111-1111-1111-111111111111', 3," +
            $"'\\id=bbbb2222-2222-2222-2222-222222222222 comprar lechuga\n" +
            $"\\id=cccc3333-3333-3333-3333-333333333333 sin gluten'," +
            $" {created.Ticks}, {updated.Ticks}, 0, 'Yellow');");

        IReadOnlyList<NoteEvent> notes = PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog());

        NoteEvent note = Assert.Single(notes);

        Assert.Equal("sticky", note.Source);
        Assert.Equal("plum.sqlite", note.Store);
        Assert.Equal("aaaa1111-1111-1111-1111-111111111111", note.NoteId);
        Assert.Equal(NoteStatus.Read, note.Status);
        Assert.Equal("comprar lechuga\nsin gluten", note.Text);
        Assert.Equal("text", note.Format);
        Assert.Equal(created, note.CreatedAt);
        Assert.Equal(updated, note.UpdatedAt);
        Assert.False(note.Deleted);
        Assert.Equal(64, note.Hash!.Length);
        Assert.Equal(note.Text!.Length, note.Length);
    }

    [Fact]
    public void Plum_Esquema3x_PrefiereElTextoEnClaroSobreElHtml()
    {
        string database = Create("tres-x.sqlite",
            "CREATE TABLE Note (id TEXT, plainText TEXT, formattedText TEXT, updated INTEGER, markedAsDeleted INTEGER, style TEXT);",
            "INSERT INTO Note VALUES ('id-1', 'texto claro', '<div>html</div>', 0, 0, 'Yellow');");

        NoteEvent note = Assert.Single(PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog()));

        Assert.Equal("texto claro", note.Text);
        Assert.Equal("text", note.Format);
    }

    [Fact]
    public void Plum_SinTextoEnClaro_InformaHtmlEnLugarDeInventar()
    {
        string database = Create("solo-html.sqlite",
            "CREATE TABLE Notes (id TEXT, formattedText TEXT, updatedAt INTEGER);",
            "INSERT INTO Notes VALUES ('id-9', '<p>solo html</p>', 0);");

        NoteEvent note = Assert.Single(PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog()));

        Assert.Equal("<p>solo html</p>", note.Text);
        Assert.Equal("html", note.Format);
        Assert.Equal(NoteStatus.Read, note.Status);
    }

    [Fact]
    public void Plum_TablaSinColumnaDeTexto_EsQuemaNoSoportado()
    {
        string database = Create("raro.sqlite",
            "CREATE TABLE Note (id TEXT PRIMARY KEY, version INTEGER, theme TEXT);",
            "INSERT INTO Note VALUES ('id-1', 1, 'Green');");

        NoteEvent note = Assert.Single(PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog()));

        Assert.Equal(NoteStatus.Unsupported, note.Status);
        Assert.Equal("sin columna de texto", note.FailureReason);
        Assert.Null(note.Text);
    }

    [Fact]
    public void Plum_SinTablaDeNotas_EsQuemaNoSoportado()
    {
        string database = Create("vacio.sqlite", "CREATE TABLE settings (k TEXT PRIMARY KEY, v TEXT);");

        NoteEvent note = Assert.Single(PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog()));

        Assert.Equal(NoteStatus.Unsupported, note.Status);
        Assert.Equal("sin tabla de notas", note.FailureReason);
    }

    [Fact]
    public void Plum_NotaBorrada_SeMarcaYSePuedeOmitir()
    {
        string database = Create("borradas.sqlite",
            "CREATE TABLE Note (id TEXT, Text TEXT, isStickyDeleted INTEGER);",
            "INSERT INTO Note VALUES ('viva', 'nota viva', 0);",
            "INSERT INTO Note VALUES ('muerta', 'nota al reciclaje', 1);");

        var include = PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog());
        Assert.Equal(2, include.Count);
        Assert.Equal(NoteStatus.Deleted, include.Single(n => n.NoteId == "muerta").Status);
        Assert.Equal(NoteStatus.Read, include.Single(n => n.NoteId == "viva").Status);

        // La marca viaja también en la bandera `deleted`, que es la que sale en el lote JSON.
        Assert.True(include.Single(n => n.NoteId == "muerta").Deleted);
        Assert.False(include.Single(n => n.NoteId == "viva").Deleted);

        var skip = PlumDatabaseReader.Read(database, "plum.sqlite",
            new NotesSection { IncludeDeleted = false }, new TestLog());

        Assert.Equal("nota viva", Assert.Single(skip).Text);
    }

    [Fact]
    public void Plum_TextoGuardadoComoBlob_SeDecodificaComoUtf8()
    {
        string database = Create("blob.sqlite",
            "CREATE TABLE Note (id TEXT, Text BLOB);",
            "INSERT INTO Note VALUES ('id-blob', x'3132333435');");

        NoteEvent note = Assert.Single(PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog()));

        Assert.Equal("12345", note.Text);
    }

    [Fact]
    public void Plum_MaxResultsPerRun_CortaLaLectura()
    {
        var statements = new List<string>
        {
            "CREATE TABLE Note (id TEXT, Text TEXT, updatedAt INTEGER);"
        };

        for (int index = 0; index < 10; index++)
            statements.Add($"INSERT INTO Note VALUES ('id-{index}', 'nota {index}', {index});");

        string database = Create("muchas.sqlite", statements.ToArray());

        var notes = PlumDatabaseReader.Read(database, "plum.sqlite",
            new NotesSection { MaxResultsPerRun = 4 }, new TestLog());

        Assert.Equal(4, notes.Count);
    }

    [Fact]
    public void Plum_LecturaNoModificaElAlmacen()
    {
        string database = Create("solo-lectura.sqlite",
            "CREATE TABLE Note (id TEXT, Text TEXT);",
            "INSERT INTO Note VALUES ('id-1', 'no tocar');");

        byte[] before = File.ReadAllBytes(database);
        PlumDatabaseReader.Read(database, "plum.sqlite", new NotesSection(), new TestLog());

        Assert.Equal(before, File.ReadAllBytes(database));
        Assert.False(File.Exists(database + "-wal"));
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("plain_text")]
    [InlineData("text")]
    [InlineData("body")]
    [InlineData("content")]
    public void Esquema_CualquierColumnaDeTextoSirve(string textColumn)
    {
        PlumSchema? schema = PlumSchema.Resolve("Note", ["id", textColumn, "updatedAt"]);

        Assert.NotNull(schema);
        Assert.Equal(textColumn, schema!.Text);
        Assert.False(schema.TextIsHtml);
        Assert.Equal("updatedAt", schema.Updated);
        Assert.Equal("id", schema.Id);
    }

    [Fact]
    public void Esquema_PrefiereTextoEnClaroSobreHtmlYDetectaElBorrado()
    {
        PlumSchema? schema = PlumSchema.Resolve("Note",
            ["id", "plainText", "formattedText", "createdAt", "updatedAt", "isStickyDeleted"]);

        Assert.NotNull(schema);
        Assert.Equal("plainText", schema!.Text);
        Assert.False(schema.TextIsHtml);
        Assert.Equal("isStickyDeleted", schema.Deleted);
        Assert.Equal("createdAt", schema.Created);

        Assert.Null(PlumSchema.Resolve("Note", ["id", "version"]));
        Assert.Null(PlumSchema.Resolve(null, ["Text"]));
        Assert.Null(PlumSchema.Resolve("Note", Array.Empty<string>()));
    }

    [Fact]
    public void NotaTexto_LimpiaMarcasInternasYNormalizaSaltos()
    {
        const string raw = "\\id=11111111-1111-1111-1111-111111111111 primera línea\r\n" +
                           "\\id=22222222-2222-2222-2222-222222222222 segunda línea\0";

        Assert.Equal("primera línea\nsegunda línea", NoteText.Sanitize(raw));
        Assert.Equal(string.Empty, NoteText.Sanitize(null));
        Assert.True(NoteText.IsBlank("   \0  "));
        Assert.False(NoteText.IsBlank("hola"));
    }

    [Fact]
    public void NotaTexto_RecortaMarcandoElCorte()
    {
        string larga = new('a', 40);

        string recortada = NoteText.Truncate(larga, 24, out bool truncated);

        Assert.True(truncated);
        Assert.StartsWith(new string('a', 24), recortada, StringComparison.Ordinal);
        Assert.EndsWith("…[truncado]", recortada, StringComparison.Ordinal);

        Assert.Equal("abc", NoteText.Truncate("abc", 24, out bool intacta));
        Assert.False(intacta);
    }

    [Fact]
    public void NotaTime_ConvierteFileTimeYRechazaValoresAbsurdos()
    {
        var instant = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(instant, NoteTime.FromTicks(instant.Ticks));
        Assert.Equal(instant, NoteTime.FromValue(instant.Ticks));
        Assert.Equal(instant, NoteTime.FromValue(instant.Ticks.ToString()));
        Assert.Equal(instant, NoteTime.FromValue("2026-10-24T12:00:00Z"));

        Assert.Null(NoteTime.FromTicks(null));
        Assert.Null(NoteTime.FromTicks(0));
        Assert.Null(NoteTime.FromTicks(1L << 60));
        Assert.Null(NoteTime.FromValue(DBNull.Value));
        Assert.Null(NoteTime.FromValue("no-es-una-fecha"));
    }

    private string Create(string name, params string[] statements)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False;"))
        {
            connection.Open();

            foreach (string sql in statements)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        return path;
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { /* mejor esfuerzo */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* mejor esfuerzo */ }
    }

    private sealed class TestLog : ILog
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;
        public bool MirrorToConsole { get; set; }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
