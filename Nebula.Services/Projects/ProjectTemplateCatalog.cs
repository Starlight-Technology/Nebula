using Nebula.Core.Agent;
using Nebula.Core.Projects;

namespace Nebula.Services.Projects;

public sealed class ProjectTemplateCatalog : IProjectTemplateCatalog
{
    private static readonly IReadOnlyList<ProjectTemplate> Templates =
    [
        DotNetConsole(),
        DotNetApi(),
        PythonScript(),
        PythonPackage(),
        NodeCli()
    ];

    public IReadOnlyList<ProjectTemplate> GetAll() => Templates;

    public ProjectTemplate? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Templates.FirstOrDefault(
            template => string.Equals(
                template.Id,
                id,
                StringComparison.OrdinalIgnoreCase));
    }

    public ProjectTemplate? Suggest(string objective, WorkspaceStackKind? stack = null)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            return null;
        }

        var candidates = Templates
            .Where(template => stack is null || template.Stack == stack)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = Templates.ToList();
        }

        ProjectTemplate? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = candidate.Keywords.Count(
                keyword => objective.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is not null)
        {
            return best;
        }

        return stack is not null
            ? candidates.FirstOrDefault(template => template.Stack == stack)
            : null;
    }

    private static ProjectTemplate DotNetConsole() => new(
        Id: "dotnet-console",
        Name: "Console .NET",
        Description: "Console application .NET 10 com um programa simples, testes xUnit e README.",
        Stack: WorkspaceStackKind.DotNet,
        Files:
        [
            new TemplateFile("src/App/App.csproj", ConsoleCsproj()),
            new TemplateFile("src/App/Program.cs", ConsoleProgram()),
            new TemplateFile("tests/App.Tests/App.Tests.csproj", ConsoleTestsCsproj()),
            new TemplateFile("tests/App.Tests/UnitTest1.cs", ConsoleUnitTest()),
            new TemplateFile("README.md", ConsoleReadme()),
            new TemplateFile(".gitignore", GitIgnore())
        ],
        SetupCommands: [],
        VerificationCommands:
        [
            "dotnet build \"src/App/App.csproj\"",
            "dotnet test \"tests/App.Tests/App.Tests.csproj\""
        ],
        EssentialFiles: ["src/App/App.csproj", "src/App/Program.cs"],
        Keywords: ["console", "dotnet", "c#", "csharp", ".net", "cli"]);

    private static ProjectTemplate DotNetApi() => new(
        Id: "dotnet-api",
        Name: "API Web .NET (Minimal API)",
        Description: "API REST minimal .NET 10 com endpoint de health, teste de integracao e README.",
        Stack: WorkspaceStackKind.DotNet,
        Files:
        [
            new TemplateFile("src/Api/Api.csproj", ApiCsproj()),
            new TemplateFile("src/Api/Program.cs", ApiProgram()),
            new TemplateFile("src/Api/appsettings.json", ApiAppSettings()),
            new TemplateFile("tests/Api.Tests/Api.Tests.csproj", ApiTestsCsproj()),
            new TemplateFile("tests/Api.Tests/HealthEndpointTest.cs", ApiHealthTest()),
            new TemplateFile("README.md", ApiReadme()),
            new TemplateFile(".gitignore", GitIgnore())
        ],
        SetupCommands: [],
        VerificationCommands:
        [
            "dotnet build \"src/Api/Api.csproj\"",
            "dotnet test \"tests/Api.Tests/Api.Tests.csproj\""
        ],
        EssentialFiles: ["src/Api/Api.csproj", "src/Api/Program.cs"],
        Keywords: ["api", "rest", "web", "backend", "server", "http", "minimal"]);

    private static ProjectTemplate PythonScript() => new(
        Id: "python-script",
        Name: "Script Python",
        Description: "Script Python simples com argumentos, funcao main e README.",
        Stack: WorkspaceStackKind.Python,
        Files:
        [
            new TemplateFile("main.py", PythonMain()),
            new TemplateFile("README.md", PythonReadme()),
            new TemplateFile(".gitignore", PythonGitIgnore())
        ],
        SetupCommands: [],
        VerificationCommands:
        [
            "python -m py_compile \"main.py\""
        ],
        EssentialFiles: ["main.py"],
        Keywords: ["python", "script", "automacao", "automation"]);

    private static ProjectTemplate PythonPackage() => new(
        Id: "python-package",
        Name: "Pacote Python",
        Description: "Pacote Python com pyproject.toml, modulo principal, testes pytest e README.",
        Stack: WorkspaceStackKind.Python,
        Files:
        [
            new TemplateFile("pyproject.toml", PythonPyProject()),
            new TemplateFile("src/package/__init__.py", PythonInit()),
            new TemplateFile("src/package/core.py", PythonCore()),
            new TemplateFile("tests/test_core.py", PythonTest()),
            new TemplateFile("README.md", PythonPackageReadme()),
            new TemplateFile(".gitignore", PythonGitIgnore())
        ],
        SetupCommands: [],
        VerificationCommands:
        [
            "python -m pytest"
        ],
        EssentialFiles: ["pyproject.toml", "src/package/__init__.py"],
        Keywords: ["package", "pacote", "biblioteca", "library", "pytest"]);

    private static ProjectTemplate NodeCli() => new(
        Id: "node-cli",
        Name: "CLI Node.js",
        Description: "CLI Node.js com argumentos, testes com node:test e README.",
        Stack: WorkspaceStackKind.Node,
        Files:
        [
            new TemplateFile("package.json", NodePackageJson()),
            new TemplateFile("index.js", NodeIndex()),
            new TemplateFile("test/index.test.js", NodeTest()),
            new TemplateFile("README.md", NodeReadme()),
            new TemplateFile(".gitignore", NodeGitIgnore())
        ],
        SetupCommands: [],
        VerificationCommands:
        [
            "npm test"
        ],
        EssentialFiles: ["package.json", "index.js"],
        Keywords: ["node", "nodejs", "javascript", "js", "cli"]);

    private static string ConsoleCsproj() => """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>

        </Project>
        """;

    private static string ConsoleProgram() => """
        var name = args.Length > 0 ? args[0] : "World";
        Console.WriteLine($"Hello, {name}!");
        """;

    private static string ConsoleTestsCsproj() => """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsPackable>false</IsPackable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
            <PackageReference Include="xunit" Version="2.9.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
          </ItemGroup>

          <ItemGroup>
            <ProjectReference Include="..\..\src\App\App.csproj" />
          </ItemGroup>

        </Project>
        """;

    private static string ConsoleUnitTest() => """
        namespace App.Tests;

        public class UnitTest1
        {
            [Fact]
            public void Hello_should_say_hello()
            {
                var name = "Nebula";
                Assert.Contains("Nebula", $"Hello, {name}!");
            }
        }
        """;

    private static string ApiCsproj() => """
        <Project Sdk="Microsoft.NET.Sdk.Web">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>

        </Project>
        """;

    private static string ApiProgram() => """
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.Run();

        public partial class Program;
        """;

    private static string ApiAppSettings() => """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information"
            }
          }
        }
        """;

    private static string ApiTestsCsproj() => """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsPackable>false</IsPackable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
            <PackageReference Include="xunit" Version="2.9.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
          </ItemGroup>

          <ItemGroup>
            <ProjectReference Include="..\..\src\Api\Api.csproj" />
          </ItemGroup>

        </Project>
        """;

    private static string ApiHealthTest() => """
        using System.Net.Http.Json;

        namespace Api.Tests;

        public class HealthEndpointTest
        {
            [Fact]
            public async Task Health_should_return_ok()
            {
                await using var factory = new WebApplicationFactory<Program>();
                using var client = factory.CreateClient();

                var response = await client.GetAsync("/health");
                var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

                Assert.True(response.IsSuccessStatusCode);
                Assert.Equal("ok", body!["status"]);
            }
        }
        """;

    private static string PythonMain() => """
        import sys


        def main() -> int:
            name = sys.argv[1] if len(sys.argv) > 1 else "World"
            print(f"Hello, {name}!")
            return 0


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    private static string PythonPyProject() => """
        [build-system]
        requires = ["setuptools>=68"]
        build-backend = "setuptools.build_meta"

        [project]
        name = "nebula-package"
        version = "0.1.0"
        requires-python = ">=3.10"

        [tool.pytest.ini_options]
        testpaths = ["tests"]
        """;

    private static string PythonInit() => "# pacote nebula-package" + "\n";

    private static string PythonCore() => """
        def greet(name: str = "World") -> str:
            return f"Hello, {name}!"
        """;

    private static string PythonTest() => """
        from package.core import greet


        def test_greet():
            assert greet("Nebula") == "Hello, Nebula!"
        """;

    private static string NodePackageJson() => """
        {
          "name": "nebula-cli",
          "version": "0.1.0",
          "description": "A simple CLI built by Nebula",
          "type": "commonjs",
          "main": "index.js",
          "bin": {
            "nebula-cli": "index.js"
          },
          "scripts": {
            "test": "node --test test/"
          }
        }
        """;

    private static string NodeIndex() => """
        const name = process.argv[2] ?? "World";
        console.log(`Hello, ${name}!`);
        """;

    private static string NodeTest() => """
        const { test } = require("node:test");
        const assert = require("node:assert");

        test("cli prints hello", () => {
          const name = "Nebula";
          assert.equal(`Hello, ${name}!`, "Hello, Nebula!");
        });
        """;

    private static string GitIgnore() => """
        bin/
        obj/
        *.user
        """;

    private static string PythonGitIgnore() => """
        __pycache__/
        *.pyc
        .venv/
        """;

    private static string NodeGitIgnore() => """
        node_modules/
        *.log
        """;

    private static string ConsoleReadme() => """
        # Console .NET

        Projeto console gerado pelo Nebula com testes xUnit.

        ## Como executar

        ```powershell
        dotnet run --project src/App/App.csproj
        ```

        ## Como testar

        ```powershell
        dotnet test tests/App.Tests/App.Tests.csproj
        ```
        """;

    private static string ApiReadme() => """
        # API Web .NET

        Minimal API gerada pelo Nebula com endpoint `/health` e teste de integracao.

        ## Como executar

        ```powershell
        dotnet run --project src/Api/Api.csproj
        ```

        ## Como testar

        ```powershell
        dotnet test tests/Api.Tests/Api.Tests.csproj
        ```
        """;

    private static string PythonReadme() => """
        # Script Python

        Script gerado pelo Nebula.

        ## Como executar

        ```powershell
        python main.py
        ```
        """;

    private static string PythonPackageReadme() => """
        # Pacote Python

        Pacote gerado pelo Nebula com pytest.

        ## Como testar

        ```powershell
        python -m pytest
        ```
        """;

    private static string NodeReadme() => """
        # CLI Node.js

        CLI gerada pelo Nebula com testes `node:test`.

        ## Como executar

        ```powershell
        node index.js
        ```

        ## Como testar

        ```powershell
        npm test
        ```
        """;
}
