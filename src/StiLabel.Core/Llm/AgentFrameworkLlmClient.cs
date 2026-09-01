using StiLabel.Core.Services;

namespace StiLabel.Core.Llm;

public sealed class AgentFrameworkLlmClient : ILlmClient
{
    public async Task<string> TestAsync(ModelOptions options, CancellationToken cancellationToken = default)
    {
        var agent = AgentFactory.Create(options, "只回复 ok，不要解释。");
        var response = await agent.RunAsync("ping", cancellationToken: cancellationToken);
        var text = response.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "已连通" : text;
    }
}
