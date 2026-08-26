namespace DeveloperBrowser.Core.Security;
public interface ISecretStore { Task SaveAsync(string key, string value, CancellationToken cancellationToken = default); Task<string?> GetAsync(string key, CancellationToken cancellationToken = default); Task RemoveAsync(string key, CancellationToken cancellationToken = default); }
