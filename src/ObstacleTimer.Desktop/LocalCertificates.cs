using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ObstacleTimer.Desktop;

public sealed class LocalCertificates : IDisposable
{
    public X509Certificate2 Root { get; }
    public X509Certificate2 Server { get; }
    public LocalCertificates(string dataDirectory)
    {
        string directory = Path.Combine(dataDirectory, "certificates"); Directory.CreateDirectory(directory);
        string rootPath = Path.Combine(directory, "local-camera-ca.pfx");
        if (File.Exists(rootPath)) Root = X509CertificateLoader.LoadPkcs12FromFile(rootPath, null, X509KeyStorageFlags.Exportable);
        else
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Obstacle Timer Local Camera CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
            File.WriteAllBytes(rootPath, created.Export(X509ContentType.Pfx));
            Root = X509CertificateLoader.LoadPkcs12FromFile(rootPath, null, X509KeyStorageFlags.Exportable);
        }
        File.WriteAllBytes(Path.Combine(directory, "local-camera-ca.cer"), Root.Export(X509ContentType.Cert));
        using var serverKey = RSA.Create(2048);
        var leaf = new CertificateRequest("CN=Obstacle Timer Camera", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        foreach (string address in TimingHub.Addresses()) names.AddIpAddress(IPAddress.Parse(address));
        leaf.CertificateExtensions.Add(names.Build());
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var signed = leaf.Create(Root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(serverKey);
        // Windows Schannel cannot use every ephemeral CNG key created by CopyWithPrivateKey.
        // Import through the user's key set; the temporary key is cleaned up with the certificate.
        Server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }
    public byte[] AppleProfile()
    {
        string id = new Guid(Root.GetCertHash(HashAlgorithmName.SHA256)[..16]).ToString();
        string profileId = new Guid(Root.GetCertHash(HashAlgorithmName.SHA256)[16..]).ToString();
        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>PayloadContent</key><array><dict>
            <key>PayloadContent</key><data>{Convert.ToBase64String(Root.RawData)}</data>
            <key>PayloadDisplayName</key><string>Obstacle Timer Local Camera CA</string>
            <key>PayloadIdentifier</key><string>cn.obstacletimer.localca.certificate</string>
            <key>PayloadType</key><string>com.apple.security.root</string>
            <key>PayloadUUID</key><string>{id}</string><key>PayloadVersion</key><integer>1</integer></dict></array>
            <key>PayloadDisplayName</key><string>Obstacle Timer Camera HTTPS</string>
            <key>PayloadDescription</key><string>Local finish-camera connection for this computer.</string>
            <key>PayloadIdentifier</key><string>cn.obstacletimer.localca</string>
            <key>PayloadType</key><string>Configuration</string><key>PayloadUUID</key><string>{profileId}</string>
            <key>PayloadVersion</key><integer>1</integer></dict></plist>
            """;
        return Encoding.UTF8.GetBytes(xml);
    }
    public static string SetupHtml(string host, int httpsPort)
    {
        host = IPAddress.TryParse(host, out var address) ? address.ToString() : "localhost";
        return """
        <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>连接终点相机</title><style>body{margin:0;background:#0b141c;color:#eff6f8;font:16px/1.8 system-ui,sans-serif}main{max-width:560px;margin:36px auto;padding:24px}h1{font-size:28px}h2{font-size:19px;margin-top:28px}p{color:#a9bbc9}a{display:inline-block;padding:12px 18px;border-radius:8px;background:#c0f069;color:#122114;text-decoration:none;margin:6px 4px 6px 0}small{color:#e6c788}details{border-top:1px solid #314552;padding:16px 0}</style>
        <main><h1>连接终点相机</h1><p>手机不需要安装 App。首次连接这台电脑，需要信任本机 HTTPS 证书，浏览器才可使用摄像头。</p>
        <h2>1. 安装这台电脑的证书</h2><a href="/camera-ca.mobileconfig">苹果：下载证书描述文件</a><a href="/camera-ca.cer">安卓：下载 CA 证书</a>
        <details open><summary>苹果设置步骤</summary><p>下载后，在「设置 → 通用 → VPN 与设备管理」安装描述文件；再到「设置 → 通用 → 关于本机 → 证书信任设置」，为 Obstacle Timer Local Camera CA 开启完全信任。</p></details>
        <details><summary>安卓设置步骤</summary><p>在系统设置搜索「安装证书」或「CA 证书」，选择刚下载的 camera-ca.cer。不同品牌入口有所不同。安装后用 Chrome 或支持摄像头的现代浏览器打开下方页面。</p></details>
        <h2>2. 打开安全相机页面</h2><a id="open">打开终点相机</a><p id="address"></p>
        <small>只信任自己的计时电脑生成的证书。浏览器模式是训练参考计时，帧率和准确度需要按实际手机验证。</small>
        <script>const address='HTTPS_ADDRESS';document.querySelector('#open').href=address+location.hash;document.querySelector('#address').textContent=address;</script></main></html>
        """.Replace("HTTPS_ADDRESS", $"https://{host}:{httpsPort}/phone/");
    }
    public void Dispose() { Server.Dispose(); Root.Dispose(); }
}
