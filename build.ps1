param(
    [string]$KspRoot = $env:KSP_ROOT,
    [string]$LmpServerRoot = $env:LMP_SERVER_ROOT,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
if (!$KspRoot -or !(Test-Path -LiteralPath $KspRoot -PathType Container)) { throw 'Informe -KspRoot ou KSP_ROOT com sua instalacao KSP.' }
if (!$LmpServerRoot -or !(Test-Path -LiteralPath $LmpServerRoot -PathType Container)) { throw 'Informe -LmpServerRoot ou LMP_SERVER_ROOT com seu servidor Luna.' }
Push-Location $PSScriptRoot
try {
    $portable = Join-Path $PSScriptRoot 'tools\dotnet\dotnet.exe'
    $compiler = if (Test-Path -LiteralPath $portable) { $portable } else { (Get-Command dotnet -ErrorAction Stop).Source }
    foreach ($project in @('src\Client\Client.csproj','src\Server\Server.csproj','src\Migrate\Migrate.csproj')) {
        & $compiler build $project -c Release --nologo "-p:KspRoot=$KspRoot" "-p:LmpServerRoot=$LmpServerRoot"
        if ($LASTEXITCODE -ne 0) { throw "Compilacao falhou: $project" }
    }
    if (!$SkipTests) {
        & $compiler run --project tests\Tests.csproj -c Release -- (Join-Path $LmpServerRoot 'Universe')
        if ($LASTEXITCODE -ne 0) { throw 'Testes do nucleo falharam.' }
        & $compiler run --project tests\Plugin\Plugin.csproj -c Release "-p:LmpServerRoot=$LmpServerRoot" -- (Join-Path $LmpServerRoot 'Universe')
        if ($LASTEXITCODE -ne 0) { throw 'Testes do plugin falharam.' }
        & $compiler build tests\Abi\Abi.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build do verificador ABI falhou.' }
        & .\tests\Abi\bin\Release\net472\CommNetLunaBridge.AbiCheck.exe $KspRoot
        if ($LASTEXITCODE -ne 0) { throw 'Verificacao ABI falhou.' }
        & $compiler build tests\ClientCodec\ClientCodec.csproj -c Release --nologo "-p:KspRoot=$KspRoot"
        if ($LASTEXITCODE -ne 0) { throw 'Build dos testes JSON falhou.' }
        & .\tests\ClientCodec\bin\Release\net472\ClientCodec.exe
        if ($LASTEXITCODE -ne 0) { throw 'Compatibilidade JSON cliente/servidor falhou.' }
        & $compiler build tests\MonoHost\MonoHost.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build do teste Mono falhou.' }
        & .\tests\MonoHost\bin\Release\net472\MonoHost.exe $KspRoot (Join-Path $PSScriptRoot 'tests\ClientCodec\bin\Release\net472\ClientCodec.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Serializacao no Mono do KSP falhou.' }
    }
} finally { Pop-Location }
