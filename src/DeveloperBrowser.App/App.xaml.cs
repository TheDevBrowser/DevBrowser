using System.Windows;
using System.IO;
using DeveloperBrowser.Core.Bookmarks;
using DeveloperBrowser.Core.Collections;
using DeveloperBrowser.Core.History;
using DeveloperBrowser.Infrastructure;
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
        var provider = services.BuildServiceProvider();
        var bookmarks = provider.GetRequiredService<IBookmarkService>();
        await bookmarks.InitializeAsync();
        var window = new MainWindow(bookmarks, provider.GetRequiredService<IBrowsingHistoryService>(), provider.GetRequiredService<PageMetadataService>(), provider.GetRequiredService<ICollectionService>(), provider.GetRequiredService<IEnvironmentService>(), provider.GetRequiredService<IVariableResolver>(), provider.GetRequiredService<ICollectionImportExportService>());
        MainWindow = window;
        window.Show();
    }
}
