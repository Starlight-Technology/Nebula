using Nebula.Core.Safety;

namespace Nebula.Agent.Test.Safety;

public sealed class SecretRedactionTest
{
    [Theory]
    [InlineData("api_key=sk-12345678901234567890abcdef", "api_key=***")]
    [InlineData("password: supersecret123", "password: ***")]
    [InlineData("token = \"abc-def-ghi-jkl-mno\"", "token = ***")]
    [InlineData("Authorization: Bearer abcdef1234567890abcdef", "Authorization: Bearer ***")]
    [InlineData("client_secret=xyz789xyz789xyz789", "client_secret=***")]
    public void MasksKeyValuePatterns(string input, string expected)
    {
        Assert.Equal(expected, SecretRedaction.Apply(input));
    }

    [Fact]
    public void MasksApiKeyTokens()
    {
        var input = "use sk-proj-abcdefghijklmnopqrstuvwxyz123456 as key";
        var result = SecretRedaction.Apply(input);

        Assert.DoesNotContain("sk-proj-", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void MasksGithubTokens()
    {
        var input = "git clone https://ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ@github.com/x/y";
        var result = SecretRedaction.Apply(input);

        Assert.DoesNotContain("ghp_", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void MasksAwsKeys()
    {
        var input = "aws_access_key_id=AKIAIOSFODNN7EXAMPLE";
        var result = SecretRedaction.Apply(input);

        Assert.DoesNotContain("AKIA", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void MasksJwtTokens()
    {
        var input =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var result = SecretRedaction.Apply(input);

        Assert.DoesNotContain("eyJ", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void MasksPrivateKeyBlocks()
    {
        var input =
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAABAAKCAQEA\n-----END RSA PRIVATE KEY-----";
        var result = SecretRedaction.Apply(input);

        Assert.DoesNotContain("PRIVATE KEY-----", result);
        Assert.Equal("***", result);
    }

    [Fact]
    public void KeepsPlainTextUnchanged()
    {
        var input = "dotnet build Nebula.slnx --no-restore";
        Assert.Equal(input, SecretRedaction.Apply(input));
    }

    [Fact]
    public void HandlesNullAndEmpty()
    {
        Assert.Null(SecretRedaction.Apply(null));
        Assert.Equal(string.Empty, SecretRedaction.Apply(string.Empty));
    }
}
