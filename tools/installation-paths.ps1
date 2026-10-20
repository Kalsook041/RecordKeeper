# Windows packaged hosts can redirect LocalAppData files while process inventory reports
# the physical path. Compare the actual file, not only its apparent path.
if ($null -eq ('ReplayKeeper.InstallationPaths' -as [type])) {
    Add-Type -Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\InstallationPaths.cs')
}
function Test-ReplayKeeperSameFile([string] $first, [string] $second) {
    return [ReplayKeeper.InstallationPaths]::SameFile($first, $second)
}
