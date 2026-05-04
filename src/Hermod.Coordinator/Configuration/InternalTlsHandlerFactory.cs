using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Hermod.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Hermod.Coordinator.Configuration;

/// <summary>
/// Builds an <see cref="HttpClientHandler"/> that presents the internal
/// client certificate and pins server-cert validation to the internal
/// CA. Used by the Vault42Api named HttpClient so the coordinator
/// authenticates to vault42 with mTLS rather than implicit trust.
/// <para>
/// Three modes, decided by the three paths in
/// <see cref="SecuritySettings"/> (InternalCAPath, ClientCertPath,
/// ClientKeyPath):
/// </para>
/// <list type="bullet">
///   <item><description>All empty: returns null, logs an INFO line.
///     Dev compose / kind path — outbound calls go via the OS trust
///     store with no client cert.</description></item>
///   <item><description>All three set + readable: returns a pinned
///     handler. Logs INFO with the cert subject so the audit trail
///     records which identity the coord booted with.</description></item>
///   <item><description>Anything else (1-2 set, or files missing):
///     throws <see cref="InvalidOperationException"/>. A partial
///     config is almost certainly an operator typo; failing loud here
///     beats silently downgrading prod security.</description></item>
/// </list>
/// </summary>
public static class InternalTlsHandlerFactory
{
    /// <summary>
    /// Builds the handler, or returns null when the dev compose / kind
    /// path is in effect (all three TLS paths empty). Throws on
    /// partial config or missing files.
    /// </summary>
    public static HttpClientHandler? TryBuild(SecuritySettings security, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(logger);

        var caSet = !string.IsNullOrWhiteSpace(security.InternalCAPath);
        var certSet = !string.IsNullOrWhiteSpace(security.ClientCertPath);
        var keySet = !string.IsNullOrWhiteSpace(security.ClientKeyPath);

        if (!caSet && !certSet && !keySet)
        {
            logger.LogInformation(
                "InternalTlsHandlerFactory: no Hermod:Security:{{InternalCAPath,ClientCertPath,ClientKeyPath}} set — outbound HTTPS uses OS trust store (dev/kind path).");
            return null;
        }

        if (!(caSet && certSet && keySet))
        {
            throw new InvalidOperationException(
                "Hermod:Security partial TLS config: set all of InternalCAPath, ClientCertPath, ClientKeyPath, or none. " +
                $"Currently set: InternalCAPath={caSet}, ClientCertPath={certSet}, ClientKeyPath={keySet}. " +
                "A partial config almost always means an operator typo; refusing to start rather than silently downgrade.");
        }

        if (!File.Exists(security.InternalCAPath!))
        {
            throw new InvalidOperationException(
                $"Hermod:Security:InternalCAPath set to '{security.InternalCAPath}' but file does not exist. Cannot pin without the CA.");
        }
        if (!File.Exists(security.ClientCertPath!))
        {
            throw new InvalidOperationException(
                $"Hermod:Security:ClientCertPath set to '{security.ClientCertPath}' but file does not exist.");
        }
        if (!File.Exists(security.ClientKeyPath!))
        {
            throw new InvalidOperationException(
                $"Hermod:Security:ClientKeyPath set to '{security.ClientKeyPath}' but file does not exist.");
        }

        X509Certificate2 clientCert;
        X509Certificate2 caCert;
        try
        {
            clientCert = X509Certificate2.CreateFromPemFile(
                security.ClientCertPath!, security.ClientKeyPath!);
            caCert = X509CertificateLoader.LoadCertificateFromFile(security.InternalCAPath!);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Hermod:Security cert/key files exist but failed to parse. " +
                $"InternalCAPath={security.InternalCAPath}, ClientCertPath={security.ClientCertPath}.",
                ex);
        }

        logger.LogInformation(
            "InternalTlsHandlerFactory: pinning outbound HTTPS to internal CA {CASubject}; client cert {ClientSubject}.",
            caCert.Subject, clientCert.Subject);

        var handler = new HttpClientHandler
        {
            // Custom validation: chain must terminate at our internal
            // CA, otherwise reject. The OS trust store doesn't know
            // about our self-signed CA so the default validator would
            // reject every internal call.
            //
            // We REJECT on RemoteCertificateNameMismatch even though
            // we override validation: that flag means the cert's SAN
            // didn't match the URL the HttpClient was calling, which
            // is exactly the cross-pod-cert-reuse case we want to
            // catch (a compromised vault42 pod presenting the postgres
            // cert, etc.). RevocationMode is NoCheck because internal
            // CAs don't typically run CRL/OCSP.
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (cert is null) return false;
                if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;

                var policy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                };
                policy.CustomTrustStore.Add(caCert);

                using var pinnedChain = new X509Chain { ChainPolicy = policy };
                return pinnedChain.Build(cert);
            },
        };
        handler.ClientCertificates.Add(clientCert);
        return handler;
    }
}
