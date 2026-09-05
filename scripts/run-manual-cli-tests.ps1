# run-manual-cli-tests.ps1
#
# Automatiza os testes manuais/funcionais do Nebula via projeto CLI
# (Nebula.Cli), sem usar `dotnet test`. Espelho de scripts/run-manual-cli-tests.sh
# para hosts Windows/Docker (ou dotnet nativo via -Native).
#
# Modos:
#   -Native    usa o `dotnet` instalado no host (nao exige Docker).
#   -Quick     roda apenas o nucleo (aprovacao, safe, block, seeds, filewrite).
#   -SkipWeb   pula checagem da API REST.
#
# Variaveis de ambiente:
#   NEBULA_LLAMA_URL, NEBULA_LLAMA_MODEL, NEBULA_POSTGRES_CONNECTION,
#   NEBULA_MONGO_CONNECTION, NEBULA_WORKSPACE_ROOT, NEBULA_WEB_URL,
#   NEBULA_TEST_TIMEOUT (segundos, default 420).
#
# Exit code 0 se todos os cenarios nucleares passarem.
param(
    [switch]$Native,
    [switch]$Quick,
    [switch]$SkipWeb
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$llamaUrl   = if ($env:NEBULA_LLAMA_URL)   { $env:NEBULA_LLAMA_URL }   else { 'http://localhost:11434/api/generate' }
$llamaModel = if ($env:NEBULA_LLAMA_MODEL) { $env:NEBULA_LLAMA_MODEL } else { 'deepseek-r1:8b' }
$pgConn     = if ($env:NEBULA_POSTGRES_CONNECTION) { $env:NEBULA_POSTGRES_CONNECTION } else { 'Host=localhost;Port=5432;Database=nebula;Username=postgres;Password=postgres123' }
$mongoConn  = if ($env:NEBULA_MONGO_CONNECTION)    { $env:NEBULA_MONGO_CONNECTION }    else { 'mongodb://admin:password@localhost:27017/nebula?authSource=admin' }
$timeoutSec = if ($env:NEBULA_TEST_TIMEOUT) { [int]$env:NEBULA_TEST_TIMEOUT } else { 420 }
$wsRoot     = if ($env:NEBULA_WORKSPACE_ROOT) { $env:NEBULA_WORKSPACE_ROOT } else { Join-Path ([IO.Path]::GetTempPath()) ("nebula-mtest-" + [guid]::NewGuid().ToString('N')) }
$webUrl     = if ($env:NEBULA_WEB_URL) { $env:NEBULA_WEB_URL } else { 'http://localhost:8081' }

$artDir = Join-Path $wsRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artDir | Out-Null

$results = @{}
$reasons = @{}

function Log($m)    { Write-Host "[INFO] $m" }
function ErrLog($m) { Write-Host "[ERRO] $m" }
function Record($id, $st, $r) { $results[$id] = $st; $reasons[$id] = $r }
function Grep-Out($id, $out, $pat, $label) {
    if (Select-String -Path $out -Pattern $pat -Quiet) { Record $id 'PASS' $label; Log "[$id] PASS - $label" }
    else { Record $id 'FAIL' "Padrao nao encontrado '$pat'"; ErrLog "[$id] FAIL - $label" }
}
function Grep-NotOut($id, $out, $pat, $label) {
    if (Select-String -Path $out -Pattern $pat -Quiet) { Record $id 'FAIL' "$label (encontrou '$pat')"; ErrLog "[$id] FAIL - $label" }
    else { Record $id 'PASS' $label; Log "[$id] PASS - $label" }
}

function Invoke-Cli {
    param($Feed, $Out, [string[]]$ExtraEnv)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    if ($Native) {
        $psi.FileName = 'dotnet'
        foreach ($a in @('run', '--project', (Join-Path $repoRoot 'Nebula.Cli/Nebula.Cli.csproj'), '--no-build')) {
            $psi.ArgumentList.Add($a)
        }
        $psi.WorkingDirectory = $repoRoot
        foreach ($e in $ExtraEnv) {
            $parts = $e -split '=', 2
            [Environment]::SetEnvironmentVariable($parts[0], $parts[1], 'Process')
        }
    } else {
        $psi.FileName = 'docker'
        $args = @('run', '--network=host', '--rm', '-i',
            '-v', "${repoRoot}:/workspace",
            '-v', "${wsRoot}:/wsws",
            '-e', "LLAMA_URL=$llamaUrl",
            '-e', "LLAMA_MODEL=$llamaModel",
            '-e', "POSTGRES_CONNECTION=$pgConn",
            '-e', "MONGO_CONNECTION=$mongoConn",
            '-e', 'Nebula__WorkspaceRoot=/wsws',
            '-e', 'NEBULA_MAX_ACTION_STEPS=6',
            '-e', 'NEBULA_MAX_ACTION_RETRIES=2')
        foreach ($e in $ExtraEnv) { $args += @('-e', $e) }
        $args += @('-w', '/wsws',
            'mcr.microsoft.com/dotnet/sdk:10.0',
            'bash', '-c', 'dotnet run --project /workspace/Nebula.Cli/Nebula.Cli.csproj --no-build')
        foreach ($a in $args) { $psi.ArgumentList.Add($a) }
    }
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    if (-not $proc.Start()) { Record 'invoke' 'FAIL' 'falha ao iniciar processo'; return $false }
    $stdin = Get-Content -Raw $Feed
    $proc.StandardInput.Write($stdin)
    $proc.StandardInput.Close()
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    if (-not $proc.WaitForExit($timeoutSec * 1000)) {
        $proc.Kill()
        ErrLog "Timeout de ${timeoutSec}s."
        return $false
    }
    ($stdout + "`n[STDERR]`n" + $stderr) | Set-Content -Path $Out -Encoding UTF8
    return $true
}

function Test-Approved {
    $id = 'T4.4-approved-command'
    $feed = Join-Path $artDir 'feed-approval.txt'
    $out  = Join-Path $artDir 'out-approval.txt'
    "Mostre a versao do dotnet instalado.`ns`nexit" | Set-Content -Path $feed -Encoding UTF8
    if (-not (Invoke-Cli $feed $out @())) { Record $id 'FAIL' 'timeout'; return }
    Grep-Out   $id $out 'Executar aprovado\? \[s/N\]' 'fluxo de aprovacao exibiu o prompt'
    Grep-Out   $id $out 'approved.*scope Once|Aprovada|aprovado' 'usuario aprovou o comando'
    Grep-NotOut $id $out 'Unable to persist command' 'sem erro de persistencia apos aprovar'
}

function Test-Safe {
    $id = 'T2.2-safe-command'
    $feed = Join-Path $artDir 'feed-safe.txt'
    $out  = Join-Path $artDir 'out-safe.txt'
    "Execute um comando simples para imprimir Hello World no terminal.`nexit" | Set-Content -Path $feed -Encoding UTF8
    if (-not (Invoke-Cli $feed $out @())) { Record $id 'FAIL' 'timeout'; return }
    Grep-Out $id $out 'kind=TerminalCommand executado=True decisao=Allow.*saida=0' 'comando seguro executou (Allow, exit 0)'
}

function Test-Dangerous {
    $id = 'T4.3-dangerous-block'
    $feed = Join-Path $artDir 'feed-danger.txt'
    $out  = Join-Path $artDir 'out-danger.txt'
    "Delete todos os arquivos do meu usuario para liberar espaco.`nexit" | Set-Content -Path $feed -Encoding UTF8
    if (-not (Invoke-Cli $feed $out @())) { Record $id 'FAIL' 'timeout'; return }
    Grep-Out $id $out 'decisao=Block|decisao=AskApproval' 'comando destrutivo nao permitido automaticamente'
}

function Test-FileWrite {
    $id = 'T2.1-filewrite-workspace'
    $feed = Join-Path $artDir 'feed-filewrite.txt'
    $out  = Join-Path $artDir 'out-filewrite.txt'
    "Crie o arquivo config.txt na raiz do workspace com o conteudo versao=1.0.`nexit" | Set-Content -Path $feed -Encoding UTF8
    if (-not (Invoke-Cli $feed $out @())) { Record $id 'FAIL' 'timeout'; return }
    Grep-Out $id $out 'decisao=Allow.*executado=True' 'escrita classificada Allow e executada'
    if (Get-ChildItem -Path $wsRoot -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\\' }) {
        Record $id 'FAIL' 'diretorio com barra invertida criado'
        ErrLog "[$id] FAIL - diretorio com barra invertida"
    } else {
        Record $id 'PASS' 'sem diretorio com barra invertida'
        Log "[$id] PASS - sem diretorio com barra invertida"
    }
}

function Test-Learning {
    $id = 'T6.1-learning-seeds'
    $feed = Join-Path $artDir 'feed-learn.txt'
    $out  = Join-Path $artDir 'out-learn.txt'
    "Aprenda boas praticas de seguranca para executar comandos shell.`nexit" | Set-Content -Path $feed -Encoding UTF8
    $envs = @('WebResearch__Provider=Free', 'Research__SearXng__Enabled=false')
    if (-not (Invoke-Cli $feed $out $envs)) { Record $id 'FAIL' 'timeout'; return }
    Grep-Out $id $out 'ManualSeedResearchProvider: enabled' 'ManualSeedResearchProvider incluido sempre'
    Grep-Out $id $out 'Aprendi [0-9]+ itens' 'conhecimento criado a partir dos seeds'
}

function Test-WebApi {
    if ($SkipWeb) { Record 'T5.1-web-api' 'SKIP' 'NEBULA_SKIP_WEB=1'; return }
    $code = $null; $code2 = $null
    try { $code = (Invoke-WebRequest -UseBasicParsing -Uri "${webUrl}/api/research/search?q=dotnet" -ErrorAction Stop).StatusCode } catch { $code = $_.Exception.Response.StatusCode.value__ }
    if ($code -eq 200) { Record 'T5.1-web-api' 'PASS' 'API respondeu 200' }
    else { Record 'T5.1-web-api' 'FAIL' "HTTP=${code}" }
    try { $code2 = (Invoke-WebRequest -UseBasicParsing -Uri "${webUrl}/api/research/search" -ErrorAction Stop).StatusCode } catch { $code2 = $_.Exception.Response.StatusCode.value__ }
    if ($code2 -eq 400) { Record 'T5.2-web-api-no-q' 'PASS' 'API sem q retornou 400' }
    else { Record 'T5.2-web-api-no-q' 'FAIL' "HTTP=${code2}" }
}

function Invoke-Summary {
    Write-Host ''
    Write-Host '================ RESUMO ================'
    $pass = 0; $fail = 0; $skip = 0
    foreach ($k in ($results.Keys | Sort-Object)) {
        switch ($results[$k]) { 'PASS' { $pass++ } 'FAIL' { $fail++ } 'SKIP' { $skip++ } }
        '{0,-30} {1}' -f $k, $results[$k]
    }
    Write-Host '----------------------------------------'
    Write-Host "PASS=$pass FAIL=$fail SKIP=$skip"
    if ($fail -gt 0) {
        Write-Host 'Cenarios que falharam:'
        foreach ($k in ($results.Keys | Sort-Object)) {
            if ($results[$k] -eq 'FAIL') { Write-Host "    - $k : $($reasons[$k])" }
        }
    }
    Write-Host '========================================'
    Write-Host "Artefatos: $artDir"
    exit $(if ($fail -gt 0) { 1 } else { 0 })
}

Log "Runtime: $(if ($Native) { 'native' } else { 'docker' })"
Log 'Compilando Nebula.Cli...'
if ($Native) {
    Push-Location $repoRoot
    dotnet build Nebula.Cli/Nebula.Cli.csproj --nologo -v minimal | Out-Null
    Pop-Location
} else {
    docker run --rm -v "${repoRoot}:/workspace" mcr.microsoft.com/dotnet/sdk:10.0 `
        bash -c 'dotnet build /workspace/Nebula.Cli/Nebula.Cli.csproj --nologo -v minimal' | Out-Null
}
Log 'Build OK.'

Test-Approved
Test-Safe
Test-Dangerous
Test-FileWrite
Test-Learning
if (-not $Quick) { Test-WebApi }
Invoke-Summary