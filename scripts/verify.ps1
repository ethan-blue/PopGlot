[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) {
    throw 'Rust cargo was not found. Install Rust stable with rustup first.'
}

& $cargo fmt --all -- --check
& $cargo test --workspace --locked
& $cargo clippy --workspace --all-targets --locked -- -D warnings
& dotnet build (Join-Path $PSScriptRoot '..\apps\PopGlot.Windows\PopGlot.Windows.csproj') --configuration Debug
