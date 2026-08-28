using DeveloperBrowser.Core.Rest;
using DeveloperBrowser.Core.Security;
using DeveloperBrowser.Core.Bookmarks;
using DeveloperBrowser.Infrastructure.Bookmarks;
using DeveloperBrowser.Infrastructure.Persistence;
using DeveloperBrowser.Infrastructure.Rest;
using DeveloperBrowser.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace DeveloperBrowser.Infrastructure;
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeveloperBrowserInfrastructure(this IServiceCollection services, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var connectionString = $"Data Source={Path.Combine(dataDirectory, "developer-browser.db")}";
        services.AddDbContext<DeveloperBrowserDbContext>(options => options.UseSqlite(connectionString));
        services.AddDbContextFactory<DeveloperBrowserDbContext>(options => options.UseSqlite(connectionString));
        services.AddHttpClient<IRestClient, HttpRestClient>();
        services.AddSingleton<ISecretStore>(new DpapiSecretStore(Path.Combine(dataDirectory, "secrets")));
        services.AddSingleton<IBookmarkRepository, SqliteBookmarkRepository>();
        services.AddSingleton<IBookmarkService, BookmarkService>();
        return services;
    }
}
