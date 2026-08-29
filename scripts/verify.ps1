[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) {
    $cargoCmd = Get-Command cargo -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue
    if ($cargoCmd) {
        $cargo = $cargoCmd
    } else {
        throw 'Rust cargo was not found. Install Rust stable with rustup first.'
    }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue
if (-not $dotnet -and (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe")) {
    $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
}
if (-not $dotnet) {
    throw 'dotnet CLI was not found. Install .NET SDK first.'
}

& $cargo fmt --all -- --check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $cargo test --workspace --locked
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $cargo clippy --workspace --all-targets --locked -- -D warnings
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build (Join-Path $PSScriptRoot '..\apps\PopGlot.Windows\PopGlot.Windows.csproj') --configuration Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet run --project (Join-Path $PSScriptRoot '..\tests\PopGlot.Windows.LogicTests\PopGlot.Windows.LogicTests.csproj') --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
