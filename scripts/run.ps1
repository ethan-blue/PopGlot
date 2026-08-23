[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot '..\apps\PopGlot.Windows\PopGlot.Windows.csproj')
