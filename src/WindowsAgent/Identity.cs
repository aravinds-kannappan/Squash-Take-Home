using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Squash.Contracts;

namespace Squash.Agent;

public sealed class Identity : IDisposable
{
    public ECDsa Key { get; }
    public string MachineId { get; }
    public string PublicKey => Protocol.PublicKey(Key);
    public Identity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The endpoint agent requires Windows.");
        const string name = "Squash.Rmm.Device";
        using var cng = CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)
            ? CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)
            : CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing
            });
        Key = new ECDsaCng(cng);
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var crypto = registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        MachineId = Protocol.Hash(crypto?.GetValue("MachineGuid") as string ?? throw new InvalidOperationException("Missing machine identity."));
    }
    public string Sign(string text) => Protocol.Sign(Key, text);
    public void Dispose() => Key.Dispose();
}

public record AgentConfig(string ServerUrl, string DeviceId, string ServerPublicKey);
public record Bootstrap(string ServerUrl, string Token);
public static class DurableFile
{
    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(file, value, Protocol.Json); file.Flush(true); }
        File.Move(temp, path, true);
    }
}
