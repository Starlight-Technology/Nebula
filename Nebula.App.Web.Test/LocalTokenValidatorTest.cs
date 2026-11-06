using Nebula.App.Web.Auth;

namespace Nebula.App.Web.Test;

public sealed class LocalTokenValidatorTest
{
    [Fact]
    public void AcceptsMatchingToken()
    {
        Assert.True(LocalTokenValidator.IsValid("token-local", "token-local"));
    }

    [Fact]
    public void RejectsDifferentToken()
    {
        Assert.False(LocalTokenValidator.IsValid("token-local", "token-outro"));
    }

    [Fact]
    public void RejectsEmptyValues()
    {
        Assert.False(LocalTokenValidator.IsValid(null, "token-local"));
        Assert.False(LocalTokenValidator.IsValid("token-local", null));
        Assert.False(LocalTokenValidator.IsValid(string.Empty, string.Empty));
    }

    [Fact]
    public void IsCaseSensitive()
    {
        Assert.False(LocalTokenValidator.IsValid("TokenLocal", "tokenlocal"));
    }
}
