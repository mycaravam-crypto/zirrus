using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Zirrus.Infrastructure.Identity;

/// <summary>
/// 256-bit key that encrypts the Data Protection key ring at rest (security.md, W2). Supplied as the
/// Docker secret <c>DataProtection__Key</c> (base64). The key ring itself lives in PostgreSQL, so a
/// database backup alone cannot decrypt sessions or antiforgery tokens.
/// </summary>
public sealed class KeyRingEncryptionKey
{
    /// <summary>Creates the key holder.</summary>
    /// <param name="base64Key">Base64-encoded 32-byte key.</param>
    /// <exception cref="ArgumentException">The value is not a base64-encoded 32-byte key.</exception>
    public KeyRingEncryptionKey(string base64Key)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key.Trim());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("DataProtection:Key must be base64.", nameof(base64Key), ex);
        }

        if (key.Length != 32)
        {
            throw new ArgumentException("DataProtection:Key must decode to 32 bytes.", nameof(base64Key));
        }

        Key = key;
    }

    /// <summary>Raw key bytes.</summary>
    internal byte[] Key { get; }
}

/// <summary>Encrypts Data Protection key XML with AES-256-GCM.</summary>
/// <param name="key">Key-ring encryption key.</param>
internal sealed class AesGcmXmlEncryptor(KeyRingEncryptionKey key) : IXmlEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc />
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key.Key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var element = new XElement("encryptedKey",
            new XComment(" Encrypted with AES-256-GCM using the DataProtection__Key secret. "),
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("value", Convert.ToBase64String(ciphertext)));
        return new EncryptedXmlInfo(element, typeof(AesGcmXmlDecryptor));
    }
}

/// <summary>Decrypts key XML written by <see cref="AesGcmXmlEncryptor"/>.</summary>
/// <param name="services">Service provider; Data Protection activates decryptors with it.</param>
internal sealed class AesGcmXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    /// <inheritdoc />
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var key = services.GetRequiredService<KeyRingEncryptionKey>();
        var nonce = Convert.FromBase64String(encryptedElement.Element("nonce")!.Value);
        var tag = Convert.FromBase64String(encryptedElement.Element("tag")!.Value);
        var ciphertext = Convert.FromBase64String(encryptedElement.Element("value")!.Value);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key.Key, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
    }
}
