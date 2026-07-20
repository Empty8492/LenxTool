using System.Security.Cryptography;
using System.Text.Json;

return args.Length == 0 ? ShowUsage() : args[0].ToLowerInvariant() switch
{
    "keygen" when args.Length == 3 => GenerateKeys(args[1], args[2]),
    "sign-manifest" when args.Length == 4 => SignManifest(args[1], args[2], args[3]),
    "sign-package" when args.Length == 4 => SignPackage(args[1], args[2], args[3]),
    "verify-manifest" when args.Length == 3 => VerifyManifest(args[1], args[2]),
    "verify-package" when args.Length == 4 => VerifyPackage(args[1], args[2], args[3]),
    _ => ShowUsage()
};

static int GenerateKeys(string privatePath, string publicPath)
{
    EnsureParent(privatePath);
    EnsureParent(publicPath);
    using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privatePath, key.ExportECPrivateKeyPem());
    File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"Public key: {Path.GetFullPath(publicPath)}");
    Console.WriteLine("Private key created outside the application repository. Keep it offline.");
    return 0;
}

static int SignManifest(string payloadPath, string privatePath, string outputPath)
{
    byte[] payload = File.ReadAllBytes(payloadPath);
    using ECDsa key = LoadPrivateKey(privatePath);
    byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256);
    var envelope = new
    {
        PayloadBase64 = Convert.ToBase64String(payload),
        SignatureBase64 = Convert.ToBase64String(signature)
    };
    EnsureParent(outputPath);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static int SignPackage(string packagePath, string privatePath, string outputPath)
{
    byte[] hash = SHA256.HashData(File.ReadAllBytes(packagePath));
    using ECDsa key = LoadPrivateKey(privatePath);
    string signature = Convert.ToBase64String(key.SignHash(hash));
    EnsureParent(outputPath);
    File.WriteAllText(outputPath, signature);
    Console.WriteLine(Convert.ToHexString(hash).ToLowerInvariant());
    return 0;
}

static int VerifyManifest(string envelopePath, string publicPath)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(envelopePath));
    byte[] payload = Convert.FromBase64String(document.RootElement.GetProperty("PayloadBase64").GetString()!);
    byte[] signature = Convert.FromBase64String(document.RootElement.GetProperty("SignatureBase64").GetString()!);
    using ECDsa key = LoadPublicKey(publicPath);
    if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return 1;
    using JsonDocument payloadDocument = JsonDocument.Parse(payload);
    return payloadDocument.RootElement.GetProperty("SchemaVersion").GetInt32() == 1 ? 0 : 1;
}

static int VerifyPackage(string packagePath, string signaturePath, string publicPath)
{
    byte[] hash = SHA256.HashData(File.ReadAllBytes(packagePath));
    byte[] signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
    using ECDsa key = LoadPublicKey(publicPath);
    return key.VerifyHash(hash, signature) ? 0 : 1;
}

static ECDsa LoadPrivateKey(string path)
{
    ECDsa key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(path));
    return key;
}

static ECDsa LoadPublicKey(string path)
{
    ECDsa key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(path));
    return key;
}

static void EnsureParent(string path)
{
    string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
    if (parent is not null) Directory.CreateDirectory(parent);
}

static int ShowUsage()
{
    Console.Error.WriteLine("LenxTool.ReleaseTool keygen <private.pem> <public.pem>");
    Console.Error.WriteLine("LenxTool.ReleaseTool sign-manifest <payload.json> <private.pem> <envelope.json>");
    Console.Error.WriteLine("LenxTool.ReleaseTool sign-package <package> <private.pem> <signature.txt>");
    Console.Error.WriteLine("LenxTool.ReleaseTool verify-manifest <envelope.json> <public.pem>");
    Console.Error.WriteLine("LenxTool.ReleaseTool verify-package <package> <signature.txt> <public.pem>");
    return 2;
}
