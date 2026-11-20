using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WinIsland.Services.Usage;

/// <summary>
/// Stores provider API keys encrypted with Windows DPAPI (current user) in
/// <c>%AppData%\WinIsland\usage-secrets.json</c>. Keys never appear in
/// settings.json, logs or the UI.
/// </summary>
public sealed class UsageSecretStore
{
    private const int CryptProtectUiForbidden = 0x1;

    private readonly string _path;
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public UsageSecretStore(string directory)
    {
        _path = Path.Combine(directory, "usage-secrets.json");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            _values = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool Has(string providerId) => _values.ContainsKey(providerId);

    public string? Get(string providerId)
    {
        if (!_values.TryGetValue(providerId, out var encrypted) || string.IsNullOrEmpty(encrypted))
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(encrypted);
            var plain = Unprotect(data);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public void Set(string providerId, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _values.Remove(providerId);
        }
        else
        {
            try
            {
                _values[providerId] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value.Trim())));
            }
            catch
            {
                return;
            }
        }

        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Best effort; a failed save only means the key has to be entered again.
        }
    }

    private static byte[] Protect(byte[] data)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var inputPtr = IntPtr.Zero;
        try
        {
            inputPtr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, inputPtr, data.Length);
            input.cbData = data.Length;
            input.pbData = inputPtr;

            if (!CryptProtectData(ref input, "WinIsland usage key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output))
            {
                throw new InvalidOperationException("DPAPI protect failed.");
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPtr);
            }

            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private static byte[]? Unprotect(byte[] data)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var inputPtr = IntPtr.Zero;
        try
        {
            inputPtr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, inputPtr, data.Length);
            input.cbData = data.Length;
            input.pbData = inputPtr;

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output))
            {
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPtr);
            }

            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
