[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) {
    throw 'Rust cargo was not found. Install Rust stable with rustup first.'
}

& $cargo fmt --all -- --check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $cargo test --workspace --locked
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $cargo clippy --workspace --all-targets --locked -- -D warnings
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build (Join-Path $PSScriptRoot '..\apps\PopGlot.Windows\PopGlot.Windows.csproj') --configuration Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet run --project (Join-Path $PSScriptRoot '..\tests\PopGlot.Windows.LogicTests\PopGlot.Windows.LogicTests.csproj') --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
