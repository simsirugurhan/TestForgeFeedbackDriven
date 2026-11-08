using System.ClientModel;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using TestForge.Core.Llm;

namespace TestForge.Llm.Azure;

public sealed record AzureLlmOptions(string Endpoint, string ApiKey, string Deployment)
{
    public int MaxRetries { get; init; } = 4;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Reads AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT style values.</summary>
    public static AzureLlmOptions FromValues(string? endpoint, string? apiKey, string? deployment)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(endpoint)) missing.Add("AZURE_OPENAI_ENDPOINT");
        if (string.IsNullOrWhiteSpace(apiKey)) missing.Add("AZURE_OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(deployment)) missing.Add("AZURE_OPENAI_DEPLOYMENT");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Azure OpenAI ayarları eksik: {string.Join(", ", missing)}. Ortam değişkeni, testforge.json veya dotnet user-secrets ile verin.");
        return new AzureLlmOptions(endpoint!.Trim(), apiKey!.Trim(), deployment!.Trim());
    }
}

/// <summary>
/// The only place that knows about Azure.AI.OpenAI. Maps our DTOs to SDK types and back,
/// and retries throttling (429) and transient server errors (5xx) with exponential backoff.
/// </summary>
public sealed class AzureLlmClient : ILlmClient
{
    private readonly ChatClient _chat;
    private readonly AzureLlmOptions _options;

    public AzureLlmClient(AzureLlmOptions options)
    {
        _options = options;
        var clientOptions = new AzureOpenAIClientOptions { NetworkTimeout = options.RequestTimeout };
        var client = new AzureOpenAIClient(new Uri(options.Endpoint), new ApiKeyCredential(options.ApiKey), clientOptions);
        _chat = client.GetChatClient(options.Deployment);
    }

    public string ModelName => _options.Deployment;

    public async Task<LlmResponse> GetCompletionAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, CancellationToken ct)
    {
        var chatMessages = messages.Select(ToChatMessage).ToList();
        var chatOptions = new ChatCompletionOptions();
        foreach (var tool in tools)
            chatOptions.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description, BinaryData.FromString(tool.ParametersJsonSchema)));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                ChatCompletion completion = await _chat.CompleteChatAsync(chatMessages, chatOptions, ct);
                var content = string.Concat(completion.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
                var calls = completion.ToolCalls
                    .Select(c => new LlmToolCall(c.Id, c.FunctionName, c.FunctionArguments.ToString()))
                    .ToList();
                var usage = completion.Usage is { } u
                    ? new LlmUsage(u.InputTokenCount, u.OutputTokenCount, u.TotalTokenCount)
                    : LlmUsage.Zero;
                return new LlmResponse(string.IsNullOrEmpty(content) ? null : content, calls, usage);
            }
            catch (ClientResultException ex) when (IsTransient(ex.Status) && attempt < _options.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2, 4, 8, 16 s
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransient(int status) => status is 408 or 429 || status >= 500;

    internal static ChatMessage ToChatMessage(LlmMessage message) => message.Role switch
    {
        LlmRoles.System => new SystemChatMessage(message.Content ?? string.Empty),
        LlmRoles.User => new UserChatMessage(message.Content ?? string.Empty),
        LlmRoles.Tool => new ToolChatMessage(message.ToolCallId ?? throw new InvalidOperationException("Tool mesajında ToolCallId yok."),
            message.Content ?? string.Empty),
        LlmRoles.Assistant => ToAssistantMessage(message),
        _ => throw new InvalidOperationException($"Bilinmeyen rol: {message.Role}"),
    };

    private static AssistantChatMessage ToAssistantMessage(LlmMessage message)
    {
        if (message.ToolCalls is not { Count: > 0 } calls)
            return new AssistantChatMessage(message.Content ?? string.Empty);

        var assistant = new AssistantChatMessage(calls.Select(c =>
            ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(c.ArgumentsJson))));
        if (!string.IsNullOrEmpty(message.Content))
            assistant.Content.Add(ChatMessageContentPart.CreateTextPart(message.Content));
        return assistant;
    }
}
