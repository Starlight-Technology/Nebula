using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Nebula.App.Web.Auth;

namespace Nebula.App.Web.Test;

public sealed class WebAuthOptionsTest
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ProductionWithoutTokenThrows()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WebAuthOptions.FromConfiguration(
                Configuration(),
                Environment(Environments.Production)));

        Assert.Contains(WebAuthOptions.EnvironmentVariable, exception.Message);
    }

    [Fact]
    public void DevelopmentWithoutTokenDisablesAuth()
    {
        var options = WebAuthOptions.FromConfiguration(
            Configuration(),
            Environment(Environments.Development));

        Assert.False(options.Enabled);
        Assert.Empty(options.Token);
    }

    [Fact]
    public void ShortTokenThrows()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WebAuthOptions.FromConfiguration(
                Configuration((WebAuthOptions.EnvironmentVariable, "curto")),
                Environment(Environments.Development)));

        Assert.Contains(WebAuthOptions.MinimumTokenLength.ToString(), exception.Message);
    }

    [Fact]
    public void ReadsEnvironmentVariableToken()
    {
        var options = WebAuthOptions.FromConfiguration(
            Configuration((WebAuthOptions.EnvironmentVariable, Token)),
            Environment(Environments.Production));

        Assert.True(options.Enabled);
        Assert.Equal(Token, options.Token);
    }

    [Fact]
    public void ReadsConfigurationToken()
    {
        var options = WebAuthOptions.FromConfiguration(
            Configuration((WebAuthOptions.ConfigurationKey, Token)),
            Environment(Environments.Production));

        Assert.True(options.Enabled);
        Assert.Equal(Token, options.Token);
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    private static IHostEnvironment Environment(string environmentName)
    {
        return new FakeHostEnvironment { EnvironmentName = environmentName };
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Nebula.App.Web.Test";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
