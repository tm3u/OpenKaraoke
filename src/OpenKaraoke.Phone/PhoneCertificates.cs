using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenKaraoke.Phone;

public sealed class PhoneCertificates : IDisposable
{
    public X509Certificate2 Authority {get;}
    public X509Certificate2 Server {get;}
    public byte[] PublicCertificate=>Authority.RawData;
    public string Fingerprint=>string.Join(":",SHA256.HashData(PublicCertificate).Select(b=>b.ToString("X2")));
    public PhoneCertificates(string directory,IPAddress address)
    {
        directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);
        string identity=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..24];
        using var caKey=OpenKey("OpenKaraoke-CA-"+identity);using var caRsa=new RSACng(caKey);
        string certificate=Path.Combine(directory,"local-ca.cer");
        bool issue=!File.Exists(certificate);
        if(!issue)
        {
            try
            {
                using var saved=X509CertificateLoader.LoadCertificateFromFile(certificate);using var savedKey=saved.GetRSAPublicKey();
                issue=saved.NotAfter.ToUniversalTime()<=DateTime.UtcNow.AddDays(181)||savedKey==null||!savedKey.ExportSubjectPublicKeyInfo().SequenceEqual(caRsa.ExportSubjectPublicKeyInfo());
            }
            catch(CryptographicException){issue=true;}
        }
        if(issue)
        {
            var request=new CertificateRequest("CN=OpenKaraoke Local Microphone CA",caRsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,true,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey,false));
            using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(5));
            File.WriteAllBytes(certificate,generated.Export(X509ContentType.Cert));
        }
        using var publicCa=X509CertificateLoader.LoadCertificateFromFile(certificate);
        Authority=publicCa.CopyWithPrivateKey(caRsa);
        using var serverKey=OpenKey("OpenKaraoke-Server-"+identity);using var serverRsa=new RSACng(serverKey);
        var serverRequest=new CertificateRequest("CN=OpenKaraoke Phone Microphone",serverRsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},false));
        var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(address);san.AddDnsName("localhost");serverRequest.CertificateExtensions.Add(san.Build());
        using var signed=serverRequest.Create(Authority,DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddDays(180),RandomNumberGenerator.GetBytes(16));
        Server=signed.CopyWithPrivateKey(serverRsa);
    }
    private static CngKey OpenKey(string name)
    {
        if(CngKey.Exists(name))return CngKey.Open(name);
        var options=new CngKeyCreationParameters{ExportPolicy=CngExportPolicies.None,KeyUsage=CngKeyUsages.Signing|CngKeyUsages.Decryption};
        options.Parameters.Add(new CngProperty("Length",BitConverter.GetBytes(2048),CngPropertyOptions.None));
        return CngKey.Create(CngAlgorithm.Rsa,name,options);
    }
    public void Dispose(){Server.Dispose();Authority.Dispose();}
}
