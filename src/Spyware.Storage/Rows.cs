namespace Spyware.Storage;

public sealed record ScreenshotRow(
    long Id, DateTime TimestampUtc, string Path, int Width, int Height,
    string? Hash, long Bytes, int DisplayCount, bool DesktopLocked);

public sealed record ClipboardRow(
    long Id, DateTime TimestampUtc, string Format, string? Content,
    string? FilePath, long Length, string? Hash);

public sealed record CredentialRow(
    long Id, DateTime TimestampUtc, string Browser, string Profile, string Url,
    string Username, string? Password, string? EncryptedBlob, string Status,
    DateTime? CreatedAt, DateTime? LastUsedAt);

public sealed record SpoolFile(string Path, DateTime LastWriteUtc, long Length);

public sealed record KeystrokeRow(
    long Id, DateTime TimestampUtc, DateTime? StartedUtc, string WindowTitle, string ProcessName,
    string Text, int KeyCount, int CharCount, bool Truncated, string? Hash, string? FilePath);

public sealed record NoteRow(
    long Id, DateTime TimestampUtc, string Source, string Store, string NoteId, string? Text,
    string Format, string Status, string? FailureReason, int Length, string? Hash,
    bool Deleted, DateTime? CreatedAt, DateTime? UpdatedAt);
