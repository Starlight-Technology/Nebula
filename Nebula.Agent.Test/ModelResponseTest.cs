using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public class ModelResponseTest
{
    [Fact]
    public void parse_must_extract_reasoning_and_response_when_think_block_exists()
    {
        const string raw = "<think>Inspecting options</think>Final answer";

        var parsed = ModelResponse.Parse(raw);

        Assert.Equal("Inspecting options", parsed.Reasoning);
        Assert.Equal("Final answer", parsed.Response);
    }

    [Fact]
    public void parse_must_keep_response_when_no_think_block_exists()
    {
        const string raw = "Direct answer";

        var parsed = ModelResponse.Parse(raw);

        Assert.Equal(string.Empty, parsed.Reasoning);
        Assert.Equal("Direct answer", parsed.Response);
    }
}
