using Microsoft.AspNetCore.Antiforgery;

using Nebula.App.Web.Auth;

namespace Nebula.App.Web.Test;

public sealed class LoginPageTest
{
    [Fact]
    public void RendersAntiforgeryAndReturnUrl()
    {
        var tokens = new AntiforgeryTokenSet(
            "request-token",
            "cookie-token",
            "__RequestVerificationToken",
            "X-CSRF-TOKEN");

        var html = LoginPage.Render(tokens, "/chat?x=<script>");

        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.Contains("value=\"request-token\"", html);
        Assert.Contains("value=\"/chat?x=&lt;script&gt;\"", html);
        Assert.DoesNotContain("auth-error", html);
    }

    [Fact]
    public void EncodesErrorMessage()
    {
        var tokens = new AntiforgeryTokenSet(
            "request-token",
            "cookie-token",
            "__RequestVerificationToken",
            "X-CSRF-TOKEN");

        var html = LoginPage.Render(tokens, "/", "<script>alert(1)</script>");

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }
}
