using StiLabel.Core.Llm;
using StiLabel.Core.Services;
using StiLabel.Data.Security;

namespace StiLabel.Data.Stores;

public sealed class ModelOptionsStore : IModelOptionsStore
{
    private readonly IAppStore _store;

    public ModelOptionsStore(IAppStore store) => _store = store;

    public async Task<ModelOptions> LoadAsync(CancellationToken cancellationToken = default) =>
        new()
        {
            Enabled = string.Equals(await _store.GetAsync("ModelEnabled", cancellationToken), "1", StringComparison.Ordinal),
            Endpoint = await _store.GetAsync("ModelEndpoint", cancellationToken) ?? "",
            Model = await _store.GetAsync("ModelName", cancellationToken) ?? "",
            ApiKey = SecretProtector.Unprotect(await _store.GetAsync("ModelKey", cancellationToken)),
            Provider = await _store.GetAsync("ModelProvider", cancellationToken) ?? "",
            Protocol = await _store.GetAsync("ModelProtocol", cancellationToken) ?? "",
            ContextTokens = CompactModes.ParseContextTokens(await _store.GetAsync("ModelContextTokens", cancellationToken)),
            CompactMode = CompactModes.Normalize(await _store.GetAsync("ModelCompactMode", cancellationToken)),
            CompactTurns = CompactModes.ParseTurns(await _store.GetAsync("ModelCompactTurns", cancellationToken))
        };

    public async Task SaveAsync(ModelOptions options, CancellationToken cancellationToken = default)
    {
        await _store.SetAsync("ModelEnabled", options.Enabled ? "1" : "0", cancellationToken);
        await _store.SetAsync("ModelEndpoint", options.Endpoint.Trim(), cancellationToken);
        await _store.SetAsync("ModelName", options.Model.Trim(), cancellationToken);
        await _store.SetAsync("ModelKey", SecretProtector.Protect(options.ApiKey), cancellationToken);
        await _store.SetAsync("ModelProvider", options.Provider.Trim(), cancellationToken);
        await _store.SetAsync("ModelProtocol", options.Protocol.Trim(), cancellationToken);
        await _store.SetAsync("ModelContextTokens", CompactModes.ClampContextTokens(options.ContextTokens).ToString(), cancellationToken);
        await _store.SetAsync("ModelCompactMode", CompactModes.Normalize(options.CompactMode), cancellationToken);
        await _store.SetAsync("ModelCompactTurns", CompactModes.ClampTurns(options.CompactTurns).ToString(), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> LoadModelNamesAsync(CancellationToken cancellationToken = default)
    {
        var raw = await _store.GetAsync("ModelList", cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task SaveModelNamesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default) =>
        _store.SetAsync(
            "ModelList",
            string.Join('\n', names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)),
            cancellationToken);
}
