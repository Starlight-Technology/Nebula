using Nebula.Core.Agent;
using Nebula.Services.Projects;

namespace Nebula.Agent.Test.Projects;

public sealed class ProjectTemplateCatalogTest
{
    private static readonly ProjectTemplateCatalog Catalog = new();

    [Fact]
    public void catalog_must_contain_a_template_for_each_supported_stack()
    {
        var templates = Catalog.GetAll();

        Assert.Contains(templates, template => template.Stack == WorkspaceStackKind.DotNet);
        Assert.Contains(templates, template => template.Stack == WorkspaceStackKind.Python);
        Assert.Contains(templates, template => template.Stack == WorkspaceStackKind.Node);
    }

    [Fact]
    public void template_ids_must_be_unique_and_findable()
    {
        var templates = Catalog.GetAll();

        Assert.Equal(
            templates.Count,
            templates.Select(template => template.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var template in templates)
        {
            Assert.Same(template, Catalog.FindById(template.Id));
        }
    }

    [Fact]
    public void every_template_must_have_files_essential_files_and_verification_commands()
    {
        foreach (var template in Catalog.GetAll())
        {
            Assert.NotEmpty(template.Files);
            Assert.NotEmpty(template.EssentialFiles);
            Assert.NotEmpty(template.VerificationCommands);

            foreach (var essential in template.EssentialFiles)
            {
                Assert.Contains(
                    template.Files,
                    file => string.Equals(
                        file.RelativePath.Replace('\\', '/'),
                        essential.Replace('\\', '/'),
                        StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Theory]
    [InlineData("crie um projeto console .net", "dotnet-console")]
    [InlineData("criar uma api rest", "dotnet-api")]
    [InlineData("make a python script", "python-script")]
    [InlineData("python package with pytest", "python-package")]
    [InlineData("node cli tool", "node-cli")]
    public void suggest_must_match_objective_to_template(string objective, string expectedId)
    {
        var template = Catalog.Suggest(objective);

        Assert.NotNull(template);
        Assert.Equal(expectedId, template.Id);
    }

    [Fact]
    public void suggest_must_return_null_for_unknown_objective()
    {
        Assert.Null(Catalog.Suggest("complete the mission report"));
    }

    [Fact]
    public void suggest_must_honor_stack_filter()
    {
        var template = Catalog.Suggest("console app", WorkspaceStackKind.Python);

        Assert.NotNull(template);
        Assert.Equal(WorkspaceStackKind.Python, template.Stack);
    }

    [Fact]
    public void dotnet_templates_must_have_build_and_test_commands()
    {
        foreach (var template in Catalog.GetAll().Where(t => t.Stack == WorkspaceStackKind.DotNet))
        {
            Assert.Contains(template.VerificationCommands, command => command.StartsWith("dotnet build"));
            Assert.Contains(template.VerificationCommands, command => command.StartsWith("dotnet test"));
        }
    }
}
