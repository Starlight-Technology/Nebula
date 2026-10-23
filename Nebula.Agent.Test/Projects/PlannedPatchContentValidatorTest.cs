using Nebula.Core.Projects;
using Nebula.Services.Projects;

namespace Nebula.Agent.Test.Projects;

public sealed class PlannedPatchContentValidatorTest
{
    [Fact]
    public void validate_must_accept_complete_files()
    {
        var reason = PlannedPatchContentValidator.Validate(
        [
            new PlannedPatchFile(
                "index.html",
                "<!doctype html><html><body><h1>Ok</h1></body></html>"),
            new PlannedPatchFile("styles.css", "body { color: red; }"),
            new PlannedPatchFile("app.js", "function run() { return 1; }"),
            new PlannedPatchFile("data.json", "{\"ok\":true}")
        ]);

        Assert.Null(reason);
    }

    [Fact]
    public void validate_must_reject_html_without_closing_tag()
    {
        var reason = PlannedPatchContentValidator.Validate(
        [
            new PlannedPatchFile(
                "index.html",
                "<!doctype html><html><body><button>1</button>")
        ]);

        Assert.NotNull(reason);
        Assert.Contains("truncado", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index.html", reason);
    }

    [Fact]
    public void validate_must_reject_html_without_closing_body()
    {
        var reason = PlannedPatchContentValidator.ValidateFile(
            new PlannedPatchFile(
                "fragment.html",
                "<body><p>partial</p>"));

        Assert.NotNull(reason);
        Assert.Contains("</body>", reason);
    }

    [Fact]
    public void validate_must_accept_html_fragment_without_html_tag()
    {
        var reason = PlannedPatchContentValidator.ValidateFile(
            new PlannedPatchFile("fragment.html", "<p>complete fragment</p>"));

        Assert.Null(reason);
    }

    [Fact]
    public void validate_must_reject_invalid_json()
    {
        var reason = PlannedPatchContentValidator.ValidateFile(
            new PlannedPatchFile("data.json", "{\"name\": \"nebula\","));

        Assert.NotNull(reason);
        Assert.Contains("JSON", reason);
    }

    [Fact]
    public void validate_must_reject_unbalanced_javascript()
    {
        var reason = PlannedPatchContentValidator.ValidateFile(
            new PlannedPatchFile("app.js", "function run() { return 1;"));

        Assert.NotNull(reason);
        Assert.Contains("truncado", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void validate_must_ignore_braces_inside_strings_and_comments()
    {
        var reason = PlannedPatchContentValidator.ValidateFile(
            new PlannedPatchFile(
                "app.js",
                "// comment with { brace\n" +
                "const text = \"}\";\n" +
                "const template = `{ ${text} }`;\n" +
                "function run() { return text; }"));

        Assert.Null(reason);
    }

    [Fact]
    public void validate_must_reject_empty_content()
    {
        var reason = PlannedPatchContentValidator.ValidateFile(
            new PlannedPatchFile("notes.txt", "   "));

        Assert.NotNull(reason);
        Assert.Contains("vazio", reason, StringComparison.OrdinalIgnoreCase);
    }
}
