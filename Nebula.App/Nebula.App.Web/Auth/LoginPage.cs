using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Antiforgery;

namespace Nebula.App.Web.Auth;

public static class LoginPage
{
    public static string Render(
        AntiforgeryTokenSet tokens,
        string? returnUrl = null,
        string? error = null)
    {
        var encoder = HtmlEncoder.Default;
        var safeReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        var encodedReturnUrl = encoder.Encode(safeReturnUrl);
        var encodedFormField = encoder.Encode(tokens.FormFieldName);
        var encodedRequestToken = encoder.Encode(tokens.RequestToken ?? string.Empty);
        var errorMarkup = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : $"""<p class="auth-error" role="alert">{encoder.Encode(error)}</p>""";

        return $$"""
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <meta name="robots" content="noindex,nofollow" />
            <title>Nebula - Acesso local</title>
            <link rel="stylesheet" href="/auth.css" />
        </head>
        <body>
            <main class="auth-shell">
                <section class="auth-card">
                    <p class="auth-kicker">Nebula</p>
                    <h1>Acesso local</h1>
                    <p class="auth-description">
                        Informe o token configurado no servidor para acessar o painel e o agente.
                    </p>
                    {{errorMarkup}}
                    <form method="post" action="/login" class="auth-form" autocomplete="off">
                        <input type="hidden" name="{{encodedFormField}}" value="{{encodedRequestToken}}" />
                        <input type="hidden" name="returnUrl" value="{{encodedReturnUrl}}" />
                        <label for="token">Token local</label>
                        <input id="token"
                               name="token"
                               type="password"
                               autocomplete="current-password"
                               spellcheck="false"
                               autofocus
                               required />
                        <button type="submit">Entrar</button>
                    </form>
                    <p class="auth-hint">
                        O token nunca e enviado na URL e fica em um cookie HttpOnly apos o login.
                    </p>
                </section>
            </main>
        </body>
        </html>
        """;
    }
}
