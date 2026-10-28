using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bridge;

public static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "CPAMP BrimDeck Bridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 无法读取此用户的加密密钥，请重新配置");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); CryptographicOperations.ZeroMemory(bytes); }
    }
    public static string Protect(string text) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), true));
    public static string Read(string text) => text.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(text), false));
    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
public sealed class ConfigStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public BridgeConfig Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "settings.json");
        var config = File.Exists(path) ? JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException("配置内容为空") : new BridgeConfig();
        if (config.ProtectedBridgeKey.Length == 0) { config.ProtectedBridgeKey = SecretStore.Protect(SecretStore.NewKey()); Save(config); }
        config.Validate(); return config;
    }
    public void Save(BridgeConfig config)
    {
        config.Validate(); Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "settings.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, Json.Options), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
