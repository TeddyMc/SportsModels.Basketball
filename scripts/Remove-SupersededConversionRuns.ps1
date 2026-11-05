param(
    [string]$ConvertedRoot = 'C:/Data/Basketball/converted',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ConvertedRoot).Path.TrimEnd('\','/')
$prefix = $root + [IO.Path]::DirectorySeparatorChar
$catalogPath = Join-Path $root 'catalog.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if (@($catalog.Seasons).Count -eq 0) { throw 'Catalog has no selected seasons.' }
$selected = @($catalog.Seasons | ForEach-Object {
    $path = (Resolve-Path -LiteralPath $_.RunDirectory).Path.TrimEnd('\','/')
    if (!$path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Selected run outside converted root: $path" }
    if (!(Test-Path -LiteralPath (Join-Path $path 'manifest.json'))) { throw "Selected run incomplete: $path" }
    $path
})
# Never remove a directory while a converter may still be writing to it.
$writers = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {
    $_.CommandLine -match 'NbaOfficial.Host.dll.*\sconvert\s'
})
if ($writers.Count -gt 0) { throw 'A converter is running; retry cleanup after publication.' }
$directories = @(Get-ChildItem -LiteralPath $root -Directory -Recurse)
if (@($directories | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
    throw 'Reparse points under converted root require review before cleanup.'
}
$obsolete = @($directories | Where-Object {
    $_.Name -match '^\d{8}T\d{9}Z-[a-f0-9]{8}$' -and
    $selected -notcontains $_.FullName -and
    ((Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json')) -or
     (Test-Path -LiteralPath (Join-Path $_.FullName 'started.json')))
})
foreach ($directory in $obsolete) {
    $target = (Resolve-Path -LiteralPath $directory.FullName).Path
    if (!$target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup target: $target" }
    if (@($selected | Where-Object { $_.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        throw "Cleanup target contains a selected run: $target"
    }
    if ($Apply) { Remove-Item -LiteralPath $target -Recurse -Force }
    [pscustomobject]@{ RunDirectory = $target; Deleted = [bool]$Apply }
}
if ($Apply) {
    $catalog.PSObject.Properties.Remove('OneSeasonComparisonBaseline')
    $catalog.SelectionPolicy = 'Use the selected run for each season. After successful replacement publication, remove superseded conversion outputs; retain raw source data and the identity registry.'
    [IO.File]::WriteAllText("$catalogPath.tmp", ($catalog | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath "$catalogPath.tmp" -Destination $catalogPath -Force
}
