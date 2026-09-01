using System.Runtime.InteropServices;
using System.Text;

namespace StiLabel.Data.Security;

internal static class SecretProtector
{
    private const string Prefix = "dpapi:";

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return null;
        }

        if (plain.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plain;
        }

        return CryptProtectData(Encoding.UTF8.GetBytes(plain), out var sealedBytes)
            ? Prefix + Convert.ToBase64String(sealedBytes)
            : plain;
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            var sealedBytes = Convert.FromBase64String(stored[Prefix.Length..]);
            return CryptUnprotectData(sealedBytes, out var plain)
                ? Encoding.UTF8.GetString(plain)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool CryptProtectData(byte[] plain, out byte[] sealedBytes)
    {
        var input = Blob.Copy(plain);
        var output = default(Blob);
        try
        {
            if (!Native.CryptProtectData(ref input, null, nint.Zero, nint.Zero, nint.Zero, 0, ref output))
            {
                sealedBytes = [];
                return false;
            }

            sealedBytes = output.ToArray();
            return true;
        }
        finally
        {
            input.FreeHeap();
            output.FreeLocal();
        }
    }

    private static bool CryptUnprotectData(byte[] sealedBytes, out byte[] plain)
    {
        var input = Blob.Copy(sealedBytes);
        var output = default(Blob);
        try
        {
            if (!Native.CryptUnprotectData(ref input, nint.Zero, nint.Zero, nint.Zero, nint.Zero, 0, ref output))
            {
                plain = [];
                return false;
            }

            plain = output.ToArray();
            return true;
        }
        finally
        {
            input.FreeHeap();
            output.FreeLocal();
        }
    }

    private struct Blob
    {
        public int Size;
        public nint Data;

        public static Blob Copy(byte[] bytes)
        {
            var data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            return new Blob { Size = bytes.Length, Data = data };
        }

        public readonly byte[] ToArray()
        {
            if (Data == nint.Zero || Size <= 0)
            {
                return [];
            }

            var bytes = new byte[Size];
            Marshal.Copy(Data, bytes, 0, Size);
            return bytes;
        }

        public void FreeHeap()
        {
            if (Data != nint.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = nint.Zero;
            }
        }

        public void FreeLocal()
        {
            if (Data != nint.Zero)
            {
                Native.LocalFree(Data);
                Data = nint.Zero;
            }
        }
    }

    private static class Native
    {
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CryptProtectData(
            ref Blob dataIn,
            string? description,
            nint optionalEntropy,
            nint reserved,
            nint prompt,
            int flags,
            ref Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        public static extern bool CryptUnprotectData(
            ref Blob dataIn,
            nint description,
            nint optionalEntropy,
            nint reserved,
            nint prompt,
            int flags,
            ref Blob dataOut);

        [DllImport("kernel32.dll")]
        public static extern nint LocalFree(nint handle);
    }
}
