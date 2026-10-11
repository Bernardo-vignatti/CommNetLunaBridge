param([switch]$SkipBuild, [string]$KspRoot = $env:KSP_ROOT, [string]$LmpServerRoot = $env:LMP_SERVER_ROOT)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (!$SkipBuild) { & .\build.ps1 -KspRoot $KspRoot -LmpServerRoot $LmpServerRoot }
    $stage = Join-Path $PSScriptRoot ('artifacts\package-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path "$stage\Client\GameData\CommNetLunaBridge\Plugins", "$stage\Server\Plugins", "$stage\MigrationTool", "$stage\Source", "$stage\docs", "$stage\licenses" -Force | Out-Null
    Copy-Item -LiteralPath src\Client\bin\Release\net472\CommNetLunaBridge.Client.dll -Destination "$stage\Client\GameData\CommNetLunaBridge\Plugins"
    Copy-Item -LiteralPath src\Server\bin\Release\net6.0\CommNetLunaBridge.Server.dll -Destination "$stage\Server\Plugins"
    foreach ($name in @('CommNetLunaBridge.Migrate.exe','CommNetLunaBridge.Migrate.dll','CommNetLunaBridge.Migrate.deps.json','CommNetLunaBridge.Migrate.runtimeconfig.json')) { Copy-Item -LiteralPath (Join-Path 'src\Migrate\bin\Release\net6.0' $name) -Destination "$stage\MigrationTool" }
    Copy-Item README.md,LICENSE,CHANGELOG.md -Destination $stage
    Copy-Item docs\* -Destination "$stage\docs"
    Copy-Item licenses\* -Destination "$stage\licenses"
    Copy-Item README.md,LICENSE,CHANGELOG.md,CONTRIBUTING.md,Directory.Build.props,build.ps1,package.ps1 -Destination "$stage\Source"
    foreach ($directory in @('src','tests','docs','licenses')) {
        foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
            $relative = $file.FullName.Substring($PSScriptRoot.Length + 1)
            $destination = Join-Path "$stage\Source" $relative
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    $manifest = Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.FullName.Substring($stage.Length + 1).Replace('\','/') }
    $manifest | Set-Content -LiteralPath "$stage\SHA256SUMS.txt"
    $zip = Join-Path $PSScriptRoot 'artifacts\CommNetLunaBridge-0.1.3-experimental.zip'
    Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
    Write-Output $zip
} finally { Pop-Location }
