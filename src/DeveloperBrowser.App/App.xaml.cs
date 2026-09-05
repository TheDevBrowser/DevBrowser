using System.Windows;
using System.IO;
using DeveloperBrowser.Core.Bookmarks;
using DeveloperBrowser.Core.Collections;
using DeveloperBrowser.Core.History;
using DeveloperBrowser.Core.Updates;
using DeveloperBrowser.Infrastructure;
using DeveloperBrowser.App.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace DeveloperBrowser.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection();
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBrowser");
        services.AddDeveloperBrowserInfrastructure(dataDirectory);
        services.AddSingleton<PageMetadataService>();
        services.AddSingleton<IAppUpdateService, AppInstallerUpdateService>();
        var provider = services.BuildServiceProvider();
        var bookmarks = provider.GetRequiredService<IBookmarkService>();
        await bookmarks.InitializeAsync();
        var window = new MainWindow(bookmarks, provider.GetRequiredService<IBrowsingHistoryService>(), provider.GetRequiredService<PageMetadataService>(), provider.GetRequiredService<ICollectionService>(), provider.GetRequiredService<IEnvironmentService>(), provider.GetRequiredService<IVariableResolver>(), provider.GetRequiredService<ICollectionImportExportService>(), provider.GetRequiredService<IAppUpdateService>());
        MainWindow = window;
        window.Show();
    }
}
