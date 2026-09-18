using System.Text;
using System.Text.Json;
using DeveloperBrowser.Core.Collections;
using DeveloperBrowser.Core.Security;
using DeveloperBrowser.Infrastructure.Collections;
using DeveloperBrowser.Infrastructure.Persistence;
using DeveloperBrowser.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var directory = Path.Combine(Path.GetTempPath(), "DevBrowser-security-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var database = Path.Combine(directory, "requests.db");
    var factory = new TestFactory(new DbContextOptionsBuilder<DeveloperBrowserDbContext>()
        .UseSqlite($"Data Source={database};Pooling=False").Options);
    var store = new FaultStore(new DpapiSecretStore(Path.Combine(directory, "secrets")));
    var service = new LocalCollectionService(factory, store);
    var collection = await service.CreateCollectionAsync("Security checks");
    var token = "TOKEN_SENTINEL_" + Guid.NewGuid();
    var password = "PASSWORD_SENTINEL_" + Guid.NewGuid();
    var request = new SavedRestRequest
    {
        CollectionId = collection.Id, Name = "Saved request", Method = "POST",
        Url = "https://example.test/?key=" + token, AuthType = "Bearer", AuthToken = token,
        AuthUsername = "user", AuthPassword = password, Body = "{\"password\":\"" + password + "\"}",
        Headers = [new(true, "X-Api-Key", token), new(true, "Template", "{{SECRET}}")],
        Parameters = [new(true, "password", password)]
    };
    await service.SaveRequestAsync(request);
    await AssertRoundTrip();
    await using (var db = factory.CreateDbContext())
    {
        var row = await db.SavedRequests.SingleAsync();
        Check(row.SecretStoreKey is not null && row.AuthToken is null && row.AuthPassword is null &&
            row.Url == "" && row.Body == "" && row.HeadersJson == "[]" && row.ParametersJson == "[]", "No sensitive columns persisted");
    }
    foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
    {
        var bytes = await File.ReadAllBytesAsync(file);
        Check(!Encoding.UTF8.GetString(bytes).Contains(token) && !Encoding.UTF8.GetString(bytes).Contains(password), "No plaintext on disk");
    }
    Check((await service.SearchAsync(token)).Single().Id == request.Id, "Encrypted URL search");
    var copy = await service.DuplicateRequestAsync(request.Id);
    Check(copy.AuthToken == token && copy.AuthPassword == password, "Duplicate decrypts credentials");
    await service.DeleteRequestAsync(copy.Id);
    Check(Directory.GetFiles(Path.Combine(directory, "secrets"), "*.secret").Length == 1, "Delete cleans duplicate secret");
    request.AuthToken = null;
    request.AuthPassword = null;
    await service.SaveRequestAsync(request);
    var cleared = (await service.GetCollectionsAsync()).Single().Requests.Single();
    Check(cleared.AuthToken is null && cleared.AuthPassword is null, "Cleared credentials stay cleared");
    request.AuthToken = token;
    request.AuthPassword = password;
    await service.SaveRequestAsync(request);
    Check(Directory.GetFiles(Path.Combine(directory, "secrets"), "*.secret").Length == 1, "Updates retire old secrets");
    await AssertRoundTrip();

    // A failed write must not overwrite the payload referenced by an existing row.
    store.FailWrites = true;
    request.AuthToken = "replacement";
    await MustFail(() => service.SaveRequestAsync(request));
    store.FailWrites = false;
    request.AuthToken = token;
    await AssertRoundTrip();

    // A database failure after encryption leaves the old database reference usable.
    await using (var db = factory.CreateDbContext())
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_save BEFORE UPDATE ON SavedRequests BEGIN SELECT RAISE(ABORT, 'Injected failure'); END;");
    request.AuthToken = "replacement";
    await MustFail(() => service.SaveRequestAsync(request));
    request.AuthToken = token;
    await using (var db = factory.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_save;");
    await AssertRoundTrip();

    // Simulate the schema and data used by the old application.
    var legacyId = Guid.NewGuid();
    await using (var db = factory.CreateDbContext())
    {
        db.SavedRequests.Add(new SavedRequestEntity { Id = legacyId, CollectionId = collection.Id, Name = "Legacy", Method = "GET",
            Url = "https://legacy.test/", AuthToken = token, AuthPassword = password });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE SavedRequests DROP COLUMN SecretStoreKey;");
        // Remove the previously protected row: an old schema could not contain it.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM SavedRequests WHERE Id = {request.Id};");
    }
    store.FailWrites = true;
    await MustFail(() => service.GetCollectionsAsync());
    await using (var db = factory.CreateDbContext())
    {
        var legacy = await db.SavedRequests.SingleAsync();
        Check(legacy.AuthToken == token && legacy.AuthPassword == password && legacy.SecretStoreKey is null, "Failed migration preserves plaintext originals");
    }
    store.FailWrites = false;
    await service.InitializeAsync();
    var migrated = (await service.GetCollectionsAsync()).Single().Requests.Single();
    Check(migrated.AuthToken == token && migrated.AuthPassword == password, "Legacy credentials survive migration");
    await using (var db = factory.CreateDbContext())
    {
        var legacy = await db.SavedRequests.SingleAsync();
        Check(legacy.AuthToken is null && legacy.AuthPassword is null && legacy.SecretStoreKey is not null, "Migration clears plaintext");
    }
    Check((await service.GetCollectionsAsync()).Single().Requests.Single().AuthToken == token, "Migration is repeatable");
    foreach (var file in Directory.GetFiles(directory, "requests.db*"))
    {
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
        Check(!text.Contains(token) && !text.Contains(password), "Migration removes plaintext from active SQLite files");
    }
    var exporter = new CollectionImportExportService(service);
    var redacted = await exporter.ExportAsync(collection.Id, false);
    Check(!redacted.Contains(token) && !redacted.Contains(password), "Default export still redacts auth fields");
    var imported = await exporter.ImportAsync(await exporter.ExportAsync(collection.Id, true));
    Check(imported.Requests.Single().AuthPassword == password, "Explicit full export/import round trip");

    // Missing protected data is an error, never silently substituted with empty credentials.
    store.MissingReads = true;
    await MustFail(() => service.GetCollectionsAsync());
    store.MissingReads = false;

    var folder = await service.CreateFolderAsync(imported.Id, "Parent");
    var child = await service.CreateFolderAsync(imported.Id, "Child", folder.Id);
    await service.MoveRequestAsync(imported.Requests.Single().Id, imported.Id, child.Id);
    await service.DeleteFolderAsync(folder.Id);
    Check((await service.GetCollectionsAsync()).Single(x => x.Id == imported.Id).Requests.Count == 0, "Folder deletion removes protected requests");
    await service.DeleteCollectionAsync(collection.Id);
    Console.WriteLine("PASS: DPAPI storage, disk plaintext checks, round trips, legacy migration, failure recovery, search, exports and deletion");

    async Task AssertRoundTrip()
    {
        var loaded = (await new LocalCollectionService(factory, store).GetCollectionsAsync()).Single().Requests.Single();
        Check(loaded.AuthToken == token && loaded.AuthPassword == password && loaded.Url == request.Url &&
            loaded.Body == request.Body && loaded.Headers.SequenceEqual(request.Headers) && loaded.Parameters.SequenceEqual(request.Parameters), "Restart round trip");
    }
}
finally
{
    // Only this uniquely created test directory is removed.
    Directory.Delete(directory, recursive: true);
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task MustFail(Func<Task> action)
{
    try { await action(); }
    catch (Exception exception) when (exception is IOException or InvalidOperationException or DbUpdateException) { return; }
    throw new Exception("Expected injected failure was not observed");
}
sealed class TestFactory(DbContextOptions<DeveloperBrowserDbContext> options) : IDbContextFactory<DeveloperBrowserDbContext>
{
    public DeveloperBrowserDbContext CreateDbContext() => new(options);
}
sealed class FaultStore(ISecretStore inner) : ISecretStore
{
    public bool FailWrites { get; set; }
    public bool MissingReads { get; set; }
    public Task SaveAsync(string key, string value, CancellationToken ct = default) => FailWrites ? throw new IOException("Injected write failure") : inner.SaveAsync(key, value, ct);
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => MissingReads ? Task.FromResult<string?>(null) : inner.GetAsync(key, ct);
    public Task RemoveAsync(string key, CancellationToken ct = default) => inner.RemoveAsync(key, ct);
}
