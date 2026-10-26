using System.Text;
using Microsoft.Data.Sqlite;
using Spyware.Collectors.Notes;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Storage;
using Spyware.Transport;
using Xunit;

namespace Spyware.UnitTests;

/// <summary>
/// Sticky Notes clásico (archivo CFB/OLE) y el provider de extremo a extremo: copia del
/// almacén, lectura, alta en storage.db, volcado en claro y exportación con `sent`.
/// </summary>
public sealed class StickyNotesSntTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spyware-snt-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CompoundFile_LeSecuenciasPequenias_y_CadenasGrandes()
    {
        string path = Write("mixto.cfb",
            ("Threshold1", "nota corta"),
            ("Threshold10", new string('ñ', 5_000)));   // > 4096 bytes: va por cadena de sectores

        using CompoundFile file = CompoundFile.Open(path);

        Assert.Equal(
            new[] { "Threshold1", "Threshold10" },
            file.EnumerateStreams().Select(stream => stream.Name).OrderBy(name => name).ToArray());

        byte[]? small = file.ReadStream("Threshold1");
        byte[]? large = file.ReadStream("Threshold10");

        Assert.NotNull(small);
        Assert.NotNull(large);

        // Cada secuencia guarda el texto UTF-16 y datos binarios detrás: el lector para en el NUL.
        Assert.Equal("nota corta", SntFileReader.DecodeText(small!));
        Assert.Equal(new string('ñ', 5_000), SntFileReader.DecodeText(large!));
        Assert.True(large!.Length > 4_096, "la secuencia grande debe ir por cadena de sectores, no por mini FAT");
    }

    [Fact]
    public void Snt_DosNotas_EnOrdenNaturalYConEstado()
    {
        string path = Write("StickyNotes.snt",
            ("Threshold10", "la décima"),
            ("Threshold2", "la segunda"));

        IReadOnlyList<NoteEvent> notes = SntFileReader.Read(path, "StickyNotes.snt", new NotesSection(), new TestLog());

        Assert.Equal(2, notes.Count);
        Assert.Equal("Threshold2", notes[0].NoteId);
        Assert.Equal("la segunda", notes[0].Text);
        Assert.Equal("Threshold10", notes[1].NoteId);

        Assert.All(notes, note =>
        {
            Assert.Equal("sticky_legacy", note.Source);
            Assert.Equal(NoteStatus.Read, note.Status);
            Assert.Equal("text", note.Format);
            Assert.Equal(64, note.Hash!.Length);
        });
    }

    [Fact]
    public void Snt_ArchivoQueNoEsCfb_SeñalaEsquemaNoSoportado()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "falso.snt");
        File.WriteAllText(path, "esto no es un compound file");

        NoteEvent note = Assert.Single(SntFileReader.Read(path, "StickyNotes.snt", new NotesSection(), new TestLog()));

        Assert.Equal(NoteStatus.Unsupported, note.Status);
        Assert.Equal("el archivo no es un CFB/OLE", note.FailureReason);
    }

    [Fact]
    public void Snt_SinSecuenciasThreshold_SeñalaEsquemaNoSoportado()
    {
        string path = Write("sin-threshold.cfb", ("OtraCosa", "texto suelto"));

        NoteEvent note = Assert.Single(SntFileReader.Read(path, "StickyNotes.snt", new NotesSection(), new TestLog()));

        Assert.Equal(NoteStatus.Unsupported, note.Status);
        Assert.Equal("sin secuencias Threshold<N>", note.FailureReason);
    }

    [Fact]
    public void Snt_Decodifica_CortaEnElNulYRescataLoLegible()
    {
        byte[] normal = WithTrailingJunk("comprar lechugas");

        Assert.Equal("comprar lechugas", SntFileReader.DecodeText(normal));

        // Variante con prefijo binario: se rescata el segmento imprimible más largo.
        byte[] conPrefijo = [0x01, 0x00, 0xFF, 0xFE, 0x00, 0x00, .. Encoding.Unicode.GetBytes("texto rescatado")];

        Assert.Equal("texto rescatado", SntFileReader.DecodeText(conPrefijo));
        Assert.Equal(string.Empty, SntFileReader.DecodeText(Array.Empty<byte>()));
    }

    [Fact]
    public void Proveedor_LeerModernas_GuardaExportaYNoRepiteLoIgual()
    {
        AgentPaths paths = new(Path.Combine(_root, "agente"));
        paths.EnsureDirectories();

        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var spool = new SpoolManager(paths, log);
        string database = CreatePlum("plum-demo.sqlite");

        var config = new NotesSection
        {
            DatabaseOverride = database,
            LegacySnt = false,
            WriteDumpFile = true
        };

        var provider = new StickyNotesProvider(paths, repository, spool, config, log);

        Assert.Equal(2, provider.CollectAll());
        Assert.Equal(2, repository.GetUnsentNotes(10).Count);
        Assert.NotEmpty(Directory.GetFiles(paths.NoteDirectory, "notes_*.json"));

        // Sticky Notes no avisa de cambios: la segunda pasada no debe duplicar filas.
        Assert.Equal(0, provider.CollectAll());
        Assert.Equal(2, repository.GetUnsentNotes(10).Count);

        IReadOnlyList<IEventExporter> exporters =
            [new LocalFolderExporter(paths, new TransportSection(), log)];

        using var batcher = new EventBatcher("agente-demo", repository, exporters, new TransportSection(), log);

        Assert.Equal(2, batcher.Flush());
        Assert.Empty(repository.GetUnsentNotes(10));

        string batch = Directory.GetFiles(paths.ExportDirectory, "*.json", SearchOption.AllDirectories).Single();
        string json = File.ReadAllText(batch);

        Assert.Contains("\"note\"", json, StringComparison.Ordinal);
        Assert.Contains("comprar lechuga", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Proveedor_ArchivoInexistente_GuardaUnaFilaNotFound()
    {
        AgentPaths paths = new(Path.Combine(_root, "agente-sin-datos"));
        paths.EnsureDirectories();

        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var config = new NotesSection
        {
            DatabaseOverride = Path.Combine(_root, "no-existe", "plum.sqlite"),
            LegacySnt = false
        };

        var provider = new StickyNotesProvider(paths, repository, new SpoolManager(paths, log), config, log);

        Assert.Equal(1, provider.CollectAll());

        NoteRow row = Assert.Single(repository.GetUnsentNotes(10));
        Assert.Equal(NoteStatus.NotFound, row.Status);
        Assert.Null(row.Text);
    }

    [Fact]
    public void Proveedor_NotaEditada_SeRegistraOtraVezConOtroHash()
    {
        AgentPaths paths = new(Path.Combine(_root, "agente-edicion"));
        paths.EnsureDirectories();

        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        string database = CreatePlum("plum-editable.sqlite");
        const string editedId = "cccc3333-3333-3333-3333-333333333333";

        var provider = new StickyNotesProvider(paths, repository, new SpoolManager(paths, log),
            new NotesSection { DatabaseOverride = database, LegacySnt = false }, log);

        Assert.Equal(2, provider.CollectAll());

        string before = repository.GetUnsentNotes(10)
            .Single(note => note.NoteId == editedId).Hash!;

        UpdateNote(database, editedId, "pagar el agua y la luz");

        // Sticky Notes no avisa de los cambios: el hash es lo que separa una nota editada
        // de la misma nota repetida, igual que hace el portapapeles.
        Assert.Equal(1, provider.CollectAll());
        Assert.Equal(3, repository.GetStats()["notes"]);

        NoteRow edited = repository.GetUnsentNotes(10).Single(note => note.Text == "pagar el agua y la luz");

        Assert.Equal(editedId, edited.NoteId);
        Assert.Equal(NoteStatus.Read, edited.Status);
        Assert.NotEqual(before, edited.Hash);
    }

    [Fact]
    public void Proveedor_SaveTextCopy_EscribeUnaCopiaPorNota()
    {
        AgentPaths paths = new(Path.Combine(_root, "agente-copias"));
        paths.EnsureDirectories();

        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        string database = CreatePlum("plum-copias.sqlite");

        var provider = new StickyNotesProvider(paths, repository, new SpoolManager(paths, log),
            new NotesSection { DatabaseOverride = database, LegacySnt = false, SaveTextCopy = true }, log);

        Assert.Equal(2, provider.CollectAll());

        string[] copies = Directory.GetFiles(paths.NoteDirectory, "note_*.txt");

        Assert.Equal(2, copies.Length);
        Assert.Contains(copies, file => File.ReadAllText(file).Contains("comprar lechuga", StringComparison.Ordinal));
        Assert.DoesNotContain(copies, file => Path.GetFileName(file).StartsWith("notes_", StringComparison.Ordinal));
    }

    [Fact]
    public void Proveedor_SinAlmacenes_NoEscribeNada()
    {
        AgentPaths paths = new(Path.Combine(_root, "agente-desactivado"));
        paths.EnsureDirectories();

        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var provider = new StickyNotesProvider(paths, repository, new SpoolManager(paths, log),
            new NotesSection { StickyNotes = false, LegacySnt = false }, log);

        Assert.Equal(0, provider.CollectAll());
        Assert.Equal(0, repository.CountUnsent());
        Assert.Equal(0, repository.GetStats()["notes"]);
    }

    // ---------- utilidades ----------

    private string Write(string name, params (string StreamName, string Text)[] notes)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);

        CompoundFileFixture.Write(path, notes
            .Select(entry => (entry.StreamName, WithTrailingJunk(entry.Text)))
            .ToArray());

        return path;
    }

    /// <summary>Como lo guarda la aplicación: UTF-16LE terminado en NUL y datos binarios detrás.</summary>
    private static byte[] WithTrailingJunk(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text + "\0");
        byte[] junk = [0x01, 0x00, 0x02, 0x00, 0xFF, 0xFF, 0x7F, 0x00];

        return [.. body, .. junk];
    }

    private string CreatePlum(string name)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);

        using var connection = new SqliteConnection($"Data Source={path};Pooling=False;");
        connection.Open();

        using SqliteCommand setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE Note (id TEXT PRIMARY KEY, Text TEXT, createdAt INTEGER, updatedAt INTEGER, isStickyDeleted INTEGER);
            INSERT INTO Note VALUES ('aaaa1111-1111-1111-1111-111111111111',
                '\id=bbbb2222-2222-2222-2222-222222222222 comprar lechuga', 133600000000000000, 133600000600000000, 0);
            INSERT INTO Note VALUES ('cccc3333-3333-3333-3333-333333333333',
                'pagar el agua', 133600000000000000, 133600000900000000, 0);
            """;
        setup.ExecuteNonQuery();

        return path;
    }

    /// <summary>Lo que hace la aplicación al editar una nota: cambiar la fila de la base.</summary>
    private static void UpdateNote(string database, string noteId, string text)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False;");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE Note SET Text = $text WHERE id = $id;";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$id", noteId);
        command.ExecuteNonQuery();
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
