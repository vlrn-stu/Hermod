using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Hermod.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace Hermod.Coordinator.Configuration;

/// <summary>
/// YARP <see cref="IForwarderHttpClientFactory"/> override that
/// applies the same internal-CA + client-cert pinning to every
/// translator cluster as the Vault42Api HttpClient. Built once per
/// coordinator pod; the underlying SocketsHttpHandler is reused by
/// YARP across all forwarded requests for a cluster.
/// <para>
/// Three paths in <see cref="SecuritySettings"/> govern the behaviour
/// (InternalCAPath, ClientCertPath, ClientKeyPath). All-empty = dev
/// path, no pinning. All-set + readable = pinned. Anything else
/// throws at construction so a misconfigured prod pod fails fast on
/// boot rather than silently forwarding without verification.
/// </para>
/// </summary>
public sealed class InternalTlsForwarderHttpClientFactory : ForwarderHttpClientFactory
{
    private readonly X509Certificate2? _clientCert;
    private readonly X509Certificate2? _caCert;

    /// <summary>Creates the factory; loads cert material once at construction. Throws on partial / unreadable config.</summary>
    public InternalTlsForwarderHttpClientFactory(IOptions<HermodSettings> options, ILogger<InternalTlsForwarderHttpClientFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var s = options.Value.Security;

        var caSet = !string.IsNullOrWhiteSpace(s.InternalCAPath);
        var certSet = !string.IsNullOrWhiteSpace(s.ClientCertPath);
        var keySet = !string.IsNullOrWhiteSpace(s.ClientKeyPath);

        if (!caSet && !certSet && !keySet)
        {
            logger.LogInformation(
                "InternalTlsForwarderHttpClientFactory: no Hermod:Security TLS paths set — YARP clusters use OS trust store (dev/kind path).");
            return;
        }

        if (!(caSet && certSet && keySet))
        {
            throw new InvalidOperationException(
                "Hermod:Security partial TLS config: set all of InternalCAPath, ClientCertPath, ClientKeyPath, or none. " +
                $"Currently set: InternalCAPath={caSet}, ClientCertPath={certSet}, ClientKeyPath={keySet}. " +
                "A partial config means YARP would forward to translator clusters without server-cert pinning; refusing to start.");
        }

        if (!File.Exists(s.InternalCAPath!))
            throw new InvalidOperationException(
                $"Hermod:Security:InternalCAPath set to '{s.InternalCAPath}' but file does not exist (YARP cluster pinning would be unverifiable).");
        if (!File.Exists(s.ClientCertPath!))
            throw new InvalidOperationException(
                $"Hermod:Security:ClientCertPath set to '{s.ClientCertPath}' but file does not exist.");
        if (!File.Exists(s.ClientKeyPath!))
            throw new InvalidOperationException(
                $"Hermod:Security:ClientKeyPath set to '{s.ClientKeyPath}' but file does not exist.");

        try
        {
            _caCert = X509CertificateLoader.LoadCertificateFromFile(s.InternalCAPath!);
            _clientCert = X509Certificate2.CreateFromPemFile(s.ClientCertPath!, s.ClientKeyPath!);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Hermod:Security cert/key files exist but YARP factory failed to parse them.",
                ex);
        }

        logger.LogInformation(
            "InternalTlsForwarderHttpClientFactory: YARP clusters pinned to internal CA {CASubject}; client cert {ClientSubject}.",
            _caCert.Subject, _clientCert.Subject);
    }

    /// <inheritdoc />
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        base.ConfigureHandler(context, handler);

        if (_caCert is null && _clientCert is null) return;

        handler.SslOptions ??= new SslClientAuthenticationOptions();

        if (_clientCert is not null)
        {
            handler.SslOptions.ClientCertificates ??= new X509CertificateCollection();
            handler.SslOptions.ClientCertificates.Add(_clientCert);
        }

        if (_caCert is not null)
        {
            var pinned = _caCert; // capture so the closure doesn't see future state
            handler.SslOptions.RemoteCertificateValidationCallback =
                (sender, cert, chain, errors) =>
                {
                    if (cert is null) return false;
                    // Same hostname-mismatch guard as the Vault42Api
                    // handler — a compromised pod presenting another
                    // pod's cert would otherwise pass.
                    if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;

                    var x509 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                    var policy = new X509ChainPolicy
                    {
                        RevocationMode = X509RevocationMode.NoCheck,
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                    };
                    policy.CustomTrustStore.Add(pinned);
                    using var pinnedChain = new X509Chain { ChainPolicy = policy };
                    return pinnedChain.Build(x509);
                };
        }
    }
}
