using OpenAI.Chat;
using TestForge.Core.Llm;
using TestForge.Llm.Azure;

namespace TestForge.Tests;

public class AzureMappingTests
{
    [Fact]
    public void Maps_every_role_to_the_matching_sdk_message()
    {
        Assert.IsType<SystemChatMessage>(AzureLlmClient.ToChatMessage(LlmMessage.System("s")));
        Assert.IsType<UserChatMessage>(AzureLlmClient.ToChatMessage(LlmMessage.User("u")));
        Assert.IsType<ToolChatMessage>(AzureLlmClient.ToChatMessage(LlmMessage.ToolResult("call_1", "out")));
        Assert.IsType<AssistantChatMessage>(AzureLlmClient.ToChatMessage(LlmMessage.Assistant("plain", null)));
    }

    [Fact]
    public void Assistant_tool_calls_and_text_survive_the_mapping()
    {
        var message = LlmMessage.Assistant("thinking", [new LlmToolCall("call_1", "run_tests", "{}")]);
        var mapped = Assert.IsType<AssistantChatMessage>(AzureLlmClient.ToChatMessage(message));
        var call = Assert.Single(mapped.ToolCalls);
        Assert.Equal(("call_1", "run_tests", "{}"), (call.Id, call.FunctionName, call.FunctionArguments.ToString()));
        Assert.Contains(mapped.Content, p => p.Text == "thinking");
    }

    [Fact]
    public void Missing_settings_are_listed_by_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AzureLlmOptions.FromValues("https://x.openai.azure.com", null, ""));
        Assert.Contains("AZURE_OPENAI_API_KEY", ex.Message);
        Assert.Contains("AZURE_OPENAI_DEPLOYMENT", ex.Message);
        Assert.DoesNotContain("AZURE_OPENAI_ENDPOINT", ex.Message);
    }
}
