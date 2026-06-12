using System.Security.Cryptography;

namespace CleverPoint.Migrator.Core.MigrationApi;

/// <summary>
/// AES-256-CBC helpers for the SPO Migration API encryption requirement:
/// every blob in SharePoint-provided containers must be encrypted with the
/// provisioned key and carry its random IV as blob metadata ("IV", base64).
/// Queue messages come back encrypted the same way.
/// </summary>
public static class Aes256Cbc
{
    public static (byte[] Cipher, string IvBase64) Encrypt(byte[] plain, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        return (enc.TransformFinalBlock(plain, 0, plain.Length), Convert.ToBase64String(aes.IV));
    }

    public static byte[] Decrypt(byte[] cipher, byte[] key, string ivBase64)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = Convert.FromBase64String(ivBase64);
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }
}
