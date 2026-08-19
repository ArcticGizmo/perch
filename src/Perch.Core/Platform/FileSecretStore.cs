using System.Text;
using System.Text.Json;
using Perch.Data;

namespace Perch.Platform;

/// <summary>
/// The portable <see cref="ISecretStore"/>: a single JSON file (<c>secrets.json</c> in Perch's per-profile
/// app-data folder) mapping key → base64 of the <see cref="ISecretProtector"/>-protected value bytes.
/// Windows subclasses this with a DPAPI protector (<c>WindowsSecretStore</c>); a Linux head can use it with
/// the <see cref="IdentitySecretProtector"/> directly. macOS uses its own Keychain store instead.
///
/// Never throws — a missing/locked/garbage file reads as "no secrets", and a failed write is swallowed
/// (worst case: the user signs in again). The file is per-profile (see <see cref="AppProfile"/>), so a dev
/// instance never reads the installed Perch's secrets.
/// </summary>
public class FileSecretStore : ISecretStore
{
    private readonly ISecretProtector _protector;
    private readonly string _filePath;
    private readonly object _gate = new();

    public FileSecretStore(ISecretProtector? protector = null, string? filePath = null)
    {
        _protector = protector ?? new IdentitySecretProtector();
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppProfile.DataFolderName, "secrets.json");
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var map = Read();
            try
            {
                map[key] = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(value)));
                Write(map);
            }
            catch { /* best-effort: a secret we can't persist just means re-auth later */ }
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            if (!Read().TryGetValue(key, out var stored) || string.IsNullOrEmpty(stored))
                return null;
            try
            {
                return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(stored)));
            }
            catch
            {
                return null;   // wrong user/machine (DPAPI), or corrupt — treat as absent
            }
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var map = Read();
            if (map.Remove(key))
            {
                try { Write(map); } catch { /* best-effort */ }
            }
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
                       ?? new Dictionary<string, string>();
            }
        }
        catch { /* fall through to empty */ }
        return new Dictionary<string, string>();
    }

    private void Write(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }
}
