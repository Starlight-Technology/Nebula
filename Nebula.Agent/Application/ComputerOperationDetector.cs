namespace Nebula.Agent.Application;

internal static class ComputerOperationDetector
{
    private static readonly string[] ActionKeywords =
    [
        "arquivo", "arquivos", "pasta", "pastas", "diretorio", "diretorios",
        "terminal", "comando", "comandos", "shell", "powershell", "bash", "cmd",
        "git", "docker", "script", "scripts", "repositorio", "repo", "rodar",
        "executar", "criar", "listar", "abrir", "instalar", "remover", "deletar",
        "apagar", "mover", "copiar", "renomear", "editar", "salvar", "alterar",
        "altere", "atualizar", "atualize", "mudar", "mude", "run ", "execute",
        "create", "list ", "open ", "install", "remove", "delete", "move ", "copy ",
        "rename", "edit ", "save ", "change", "update", "file", "files", "folder",
        "directory"
    ];

    public static bool IsOperational(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalizedPrompt = prompt.Trim().ToLowerInvariant();
        return ActionKeywords.Any(
            keyword => normalizedPrompt.Contains(keyword, StringComparison.Ordinal));
    }
}
