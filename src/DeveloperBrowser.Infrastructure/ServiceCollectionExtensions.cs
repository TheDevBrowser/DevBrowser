using DeveloperBrowser.Core.Rest;
using DeveloperBrowser.Core.Security;
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
        services.AddDbContext<DeveloperBrowserDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "developer-browser.db")}"));
        services.AddHttpClient<IRestClient, HttpRestClient>();
        services.AddSingleton<ISecretStore>(new DpapiSecretStore(Path.Combine(dataDirectory, "secrets")));
        return services;
    }
}
