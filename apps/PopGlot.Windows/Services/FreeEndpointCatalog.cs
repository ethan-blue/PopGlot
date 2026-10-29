namespace PopGlot.Windows.Services;

/// <summary>
/// 内置免费引擎的端点目录（0.1.35）。
///
/// 端点地址不再以明文躺在二进制里：URL 模板以「UTF-8 字节 XOR 固定掩码 +
/// Base64」的形式存放，运行期才拼装。这样 <c>strings</c> / 资源扫描这类
/// 低成本扒取拿不到完整地址。
///
/// <para><b>诚实边界</b>：这是提高门槛，不是加密。任何在本机运行的程序最终
/// 都必须把真实地址交给操作系统去发请求，专业逆向（调试器、网络抓包、内存
/// dump）仍然能拿到。它挡不住下定决心的逆向者，只挡住「扫一遍二进制就
/// 批量拿走」的低成本滥用——这也是 Pot 一类应用把免配置通道做成公开
/// 无密钥端点后的现实取舍。</para>
/// </summary>
internal static class FreeEndpointCatalog
{
    private const byte ObfuscationMask = 0x5A;

    private const string GtxSingleEncoded =
        "Mi4uKilgdXUuKDs0KTY7Lj90PTU1PTY/OyozKXQ5NTd1Lig7NCk2Oy4/BTt1KTM0PTY/ZTk2Mz80Lmc9LiJ8KTZnISk2J3wuNmchLjYnfD4uZy58Pi5nOD58Pi5nKDd8K2chKyc=";

    private const string DictChromeExEncoded =
        "Mi4uKilgdXU5NjM/NC4pb3Q9NTU9Nj90OTU3dS4oOzQpNjsuPwU7dS5lOTYzPzQuZz4zOS53OTIoNTc/dz8ifCk2ZyEpNid8LjZnIS42J3wrZyErJw==";

    private const string MyMemoryEncoded =
        "Mi4uKilgdXU7KjN0NyM3Pzc1KCN0Lig7NCk2Oy4/PnQ0Py51PT8u";

    /// <summary>
    /// 还原一条 URL 模板。模板里的 {sl} / {tl} / {q} 由调用方替换。
    /// </summary>
    internal static string Reveal(string encoded)
    {
        var masked = Convert.FromBase64String(encoded);
        var bytes = new byte[masked.Length];
        for (var index = 0; index < masked.Length; index++)
        {
            bytes[index] = (byte)(masked[index] ^ ObfuscationMask);
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static string GtxSingleUrl => Reveal(GtxSingleEncoded);

    public static string DictChromeExUrl => Reveal(DictChromeExEncoded);

    public static string MyMemoryUrl => Reveal(MyMemoryEncoded);

    /// <summary>
    /// Test/invariant seam: every template must stay a well-formed https URL
    /// and must name the host the rate-limit buckets key on.
    /// </summary>
    internal static string[] AllUrls => [GtxSingleUrl, DictChromeExUrl, MyMemoryUrl];

    internal static string HostOf(string url) => new Uri(url).Host;
}
