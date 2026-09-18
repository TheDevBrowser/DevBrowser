using System.Security.Cryptography;
using System.Text;
using DeveloperBrowser.Core.Security;
namespace DeveloperBrowser.Infrastructure.Security;
/// <summary>Encrypts local secrets for the current Windows user.</summary>
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    public async Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var plaintext = Encoding.UTF8.GetBytes(value);
        byte[] bytes;
        try { bytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var path = PathFor(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var plaintext = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) { var path = PathFor(key); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".secret");
}
