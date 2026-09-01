using Microsoft.Extensions.DependencyInjection;
using StiLabel.Core.Drafting;
using StiLabel.Core.Llm;
using StiLabel.Core.Services;

namespace StiLabel.Core.Hosting;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddStiLabelCore(this IServiceCollection services)
    {
        services.AddSingleton<IDraftBuilder, DraftBuilder>();
        services.AddSingleton<ILlmClient, AgentFrameworkLlmClient>();
        services.AddSingleton<IWorkbenchAgent, LlmToolAgent>();
        return services;
    }
}
