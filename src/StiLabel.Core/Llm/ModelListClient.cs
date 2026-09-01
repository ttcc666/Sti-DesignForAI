using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using StiLabel.Core.Services;

namespace StiLabel.Core.Llm;

/// <summary>
/// 按各官网的列表接口拉模型名。只读，不参与对话。
/// OpenAI：GET /v1/models，Authorization Bearer
/// Anthropic：GET /v1/models，x-api-key + anthropic-version: 2023-06-01
/// Gemini：GET /v1beta/models，x-goog-api-key 或 ?key=
/// Azure OpenAI：GET /openai/v1/models，api-key
/// Ollama：GET /api/tags
/// </summary>
public static class ModelListClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<IReadOnlyList<string>> ListAsync(
        ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        var protocol = ChatProtocols.Normalize(options.Protocol);
        var baseUri = ChatProtocols.BaseUri(protocol, options.Endpoint);
        var url = ListUrl(protocol, baseUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request, protocol, options.ApiKey, url);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Trim().ReplaceLineEndings(" ");
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}：{(detail.Length > 300 ? detail[..300] + "…" : detail)}");
        }

        return Parse(body);
    }

    private static string ListUrl(string protocol, Uri baseUri)
    {
        var origin = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var root = baseUri.AbsoluteUri.TrimEnd('/');
        return protocol switch
        {
            ChatProtocols.Ollama => origin + "/api/tags",
            ChatProtocols.AzureOpenAi or ChatProtocols.AzureResponses => origin + "/openai/v1/models",
            ChatProtocols.Gemini => root + "/models",
            _ => root + "/models"
        };
    }

    private static void Authorize(HttpRequestMessage request, string protocol, string? apiKey, string url)
    {
        switch (protocol)
        {
            case ChatProtocols.Anthropic:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }

                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;

            case ChatProtocols.Gemini:
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return;
                }

                request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                if (!url.Contains("key=", StringComparison.OrdinalIgnoreCase))
                {
                    request.RequestUri = new Uri(url + (url.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(apiKey));
                }

                break;

            case ChatProtocols.AzureOpenAi:
            case ChatProtocols.AzureResponses:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("api-key", apiKey);
                }

                break;

            case ChatProtocols.Ollama:
                break;

            default:
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                break;
        }
    }

    private static IReadOnlyList<string> Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) ? data
            : root.TryGetProperty("models", out var models) ? models
            : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : Read(item, "id") ?? Read(item, "name") ?? Read(item, "model");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            names.Add(name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name[7..] : name);
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Read(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
