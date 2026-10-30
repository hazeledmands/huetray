using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HueTray.Hue;

/// HTTPS to a bridge. Bridges use a self-signed certificate, so the normal checks always fail.
/// Instead the certificate seen when pairing is pinned by its SHA-256 fingerprint, and after that
/// only a bridge with the same certificate is trusted.
public static class BridgeHttp
{
    /// <param name="pinned">the fingerprint to require, or null to accept any certificate (discovery and pairing)</param>
    /// <param name="seen">called with the fingerprint of the certificate the bridge presented</param>
    public static HttpClient Create(string ip, string? pinned, Action<string>? seen = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert == null) return false;
                    var fingerprint = Fingerprint(cert);
                    seen?.Invoke(fingerprint);
                    return pinned == null || string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase);
                },
            },
        };
        // No overall timeout: the event stream stays open. Ordinary requests set their own.
        return new HttpClient(handler) { BaseAddress = new Uri($"https://{ip}/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string Fingerprint(X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
}

public class HueException(string message) : Exception(message);
public sealed class NotFoundException(string message) : HueException(message);
public sealed class AmbiguousException(string message) : HueException(message);
