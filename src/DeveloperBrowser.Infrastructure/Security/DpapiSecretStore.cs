using System.Security.Cryptography;
using System.Text;
using DeveloperBrowser.Core.Security;
namespace DeveloperBrowser.Infrastructure.Security;
/// <summary>Encrypts values for the current Windows user; intended for API tokens only.</summary>
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    public async Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) { Directory.CreateDirectory(directory); var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser); await File.WriteAllBytesAsync(PathFor(key), bytes, cancellationToken); }
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) { var path = PathFor(key); if (!File.Exists(path)) return null; var bytes = await File.ReadAllBytesAsync(path, cancellationToken); return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)); }
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) { var path = PathFor(key); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".secret");
}
