using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Game.BackendServer;

internal static class CertificateBootstrap
{
    public static X509Certificate2 LoadOrCreate(
        string certificatePath,
        string password,
        IEnumerable<string> hosts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(certificatePath) ?? ".");

        if (!File.Exists(certificatePath))
        {
            using RSA rsa = RSA.Create(3072);
            var request = new CertificateRequest(
                "CN=Backend Server",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var san = new SubjectAlternativeNameBuilder();
            foreach (string raw in hosts ?? Array.Empty<string>())
            {
                string host = raw?.Trim();
                if (string.IsNullOrWhiteSpace(host))
                    continue;

                if (IPAddress.TryParse(host, out IPAddress address))
                    san.AddIpAddress(address);
                else
                    san.AddDnsName(host);
            }
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());

            using X509Certificate2 generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(2));
            File.WriteAllBytes(
                certificatePath,
                generated.Export(X509ContentType.Pfx, password));
        }

        PrivateFilePermissions.RestrictOwnerOnly(certificatePath);
        var certificate = new X509Certificate2(
            certificatePath,
            password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

        TimeSpan remaining = certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Console.Error.WriteLine(
                $"[TLS] Backend certificate expired at {certificate.NotAfter.ToUniversalTime():O}. " +
                "Replace the certificate before exposing the public HTTPS endpoint.");
        }
        else if (remaining <= TimeSpan.FromDays(30))
        {
            Console.Error.WriteLine(
                $"[TLS] Backend certificate expires at {certificate.NotAfter.ToUniversalTime():O} " +
                $"({Math.Max(0, (int)Math.Ceiling(remaining.TotalDays))} day(s) remaining).");
        }

        return certificate;
    }

    public static string Sha256Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));
}
