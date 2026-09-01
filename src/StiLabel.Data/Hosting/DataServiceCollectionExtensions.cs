using Microsoft.Extensions.DependencyInjection;
using StiLabel.Core.Services;
using StiLabel.Data.Stores;

namespace StiLabel.Data.Hosting;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddStiLabelData(this IServiceCollection services)
    {
        services.AddSingleton<StiLabelDb>();
        services.AddSingleton<IFieldCatalog, FieldCatalog>();
        services.AddSingleton<IAppStore, AppStore>();
        services.AddSingleton<IModelOptionsStore, ModelOptionsStore>();
        services.AddSingleton<ITemplateWorkspace, TemplateWorkspace>();
        return services;
    }
}
