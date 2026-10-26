namespace Spyware.Core.Paths;

/// <summary>
/// Único directorio raíz de datos: %LOCALAPPDATA%\SpywareLab\SpywareAgent.
/// Todo lo que produce el agente queda aquí y se guarda SIN CIFRAR.
/// </summary>
public sealed class AgentPaths
{
    public const string CompanyFolder = "SpywareLab";
    public const string ProductFolder = "SpywareAgent";

    public string RootDirectory { get; }
    public string StorageDirectory { get; }

    public string ConfigFile => Path.Combine(RootDirectory, "config.json");
    public string StorageDb => Path.Combine(StorageDirectory, "storage.db");
    public string ScreenshotDirectory => Path.Combine(StorageDirectory, "screenshots");
    public string ClipboardDirectory => Path.Combine(StorageDirectory, "clipboard");
    public string CredentialDirectory => Path.Combine(StorageDirectory, "credentials");
    public string KeylogDirectory => Path.Combine(StorageDirectory, "keylog");
    public string NoteDirectory => Path.Combine(StorageDirectory, "notes");
    public string ExportDirectory => Path.Combine(StorageDirectory, "export");
    public string TempDirectory => Path.Combine(RootDirectory, "temp");
    public string LogDirectory => Path.Combine(RootDirectory, "logs");
    public string HealthFile => Path.Combine(RootDirectory, "health.json");

    public AgentPaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        StorageDirectory = Path.Combine(RootDirectory, "storage");
    }

    public static AgentPaths CreateDefault()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AgentPaths(Path.Combine(local, CompanyFolder, ProductFolder));
    }

    public static AgentPaths Create(string? overrideRoot) =>
        string.IsNullOrWhiteSpace(overrideRoot)
            ? CreateDefault()
            : new AgentPaths(Environment.ExpandEnvironmentVariables(overrideRoot.Trim()));

    public void EnsureDirectories()
    {
        foreach (string dir in new[]
                 {
                     RootDirectory, StorageDirectory, ScreenshotDirectory, ClipboardDirectory,
                     CredentialDirectory, KeylogDirectory, NoteDirectory,
                     ExportDirectory, TempDirectory, LogDirectory
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
