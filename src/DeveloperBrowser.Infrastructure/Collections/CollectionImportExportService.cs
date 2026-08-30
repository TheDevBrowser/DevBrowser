using System.Text.Json;
using DeveloperBrowser.Core.Collections;

namespace DeveloperBrowser.Infrastructure.Collections;

public sealed class CollectionImportExportService(ICollectionService collections) : ICollectionImportExportService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] SensitiveNames = ["authorization", "proxy-authorization", "x-api-key", "api-key", "x-auth-token"];
    public async Task<string> ExportAsync(Guid collectionId, bool includeSensitiveValues, CancellationToken ct = default)
    {
        var collection = (await collections.GetCollectionsAsync(ct)).SingleOrDefault(x => x.Id == collectionId) ?? throw new InvalidOperationException("Collection not found.");
        var safe = new RestCollection { Id = collection.Id, Name = collection.Name, Description = collection.Description, SortOrder = collection.SortOrder, CreatedAt = collection.CreatedAt, UpdatedAt = collection.UpdatedAt, Folders = collection.Folders, Requests = collection.Requests.Select(request => new SavedRestRequest { Id = request.Id, CollectionId = request.CollectionId, FolderId = request.FolderId, Name = request.Name, Description = request.Description, Method = request.Method, Url = request.Url, Parameters = request.Parameters, Headers = request.Headers.Select(h => SensitiveNames.Contains(h.Key, StringComparer.OrdinalIgnoreCase) && !includeSensitiveValues ? h with { Value = "{{REDACTED}}" } : h).ToList(), Body = request.Body, ContentType = request.ContentType, AuthType = request.AuthType, AuthToken = includeSensitiveValues ? request.AuthToken : string.IsNullOrWhiteSpace(request.AuthToken) ? null : "{{REDACTED}}", AuthUsername = request.AuthUsername, AuthPassword = includeSensitiveValues ? request.AuthPassword : string.IsNullOrWhiteSpace(request.AuthPassword) ? null : "{{REDACTED}}", SortOrder = request.SortOrder, CreatedAt = request.CreatedAt, UpdatedAt = request.UpdatedAt }).ToList() };
        return JsonSerializer.Serialize(new CollectionExport("devbrowser.collection", 1, safe), Json);
    }
    public async Task<RestCollection> ImportAsync(string json, CancellationToken ct = default)
    {
        CollectionExport? export; try { export = JsonSerializer.Deserialize<CollectionExport>(json, Json); } catch (JsonException) { throw new InvalidOperationException("The collection file is not valid JSON."); }
        if (export is null || export.Format != "devbrowser.collection") throw new InvalidOperationException("This file is not a DevBrowser collection.");
        if (export.Version != 1) throw new InvalidOperationException("This collection format version is not supported.");
        if (export.Collection is null || string.IsNullOrWhiteSpace(export.Collection.Name)) throw new InvalidOperationException("The collection is missing required metadata.");
        if (export.Collection.Requests.Any(x => string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Url))) throw new InvalidOperationException("The collection contains an incomplete request.");
        var existing = await collections.GetCollectionsAsync(ct); var idMap = new Dictionary<Guid, Guid>(); if (existing.Any(x => x.Id == export.Collection.Id)) idMap[export.Collection.Id] = Guid.NewGuid(); foreach (var folder in export.Collection.Folders) if (existing.SelectMany(x => x.Folders).Any(x => x.Id == folder.Id) || idMap.ContainsKey(folder.Id)) idMap[folder.Id] = Guid.NewGuid(); foreach (var request in export.Collection.Requests) if (existing.SelectMany(x => x.Requests).Any(x => x.Id == request.Id) || idMap.ContainsKey(request.Id)) idMap[request.Id] = Guid.NewGuid();
        var imported = await collections.CreateCollectionAsync(export.Collection.Name, export.Collection.Description, ct); var folderMap = new Dictionary<Guid, Guid>(); foreach (var folder in export.Collection.Folders.OrderBy(x => x.SortOrder)) { var created = await collections.CreateFolderAsync(imported.Id, folder.Name, folder.ParentFolderId is { } parent && folderMap.TryGetValue(parent, out var mapped) ? mapped : null, ct); folderMap[folder.Id] = created.Id; }
        foreach (var request in export.Collection.Requests) { request.Id = idMap.TryGetValue(request.Id, out var remapped) ? remapped : request.Id; request.CollectionId = imported.Id; request.FolderId = request.FolderId is { } folder && folderMap.TryGetValue(folder, out var mapped) ? mapped : null; await collections.SaveRequestAsync(request, ct); }
        return (await collections.GetCollectionsAsync(ct)).Single(x => x.Id == imported.Id);
    }
}
