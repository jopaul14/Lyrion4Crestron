<#
.SYNOPSIS
    Rewrites backslash path separators in a driver .pkg to forward slashes.

.DESCRIPTION
    ManifestUtil.exe writes nested package entries with Windows separators
    ("uidefinitions\UiDefinition.xml"). The ZIP specification (APPNOTE
    4.4.17.1) requires forward slashes, and Crestron Home's CustomAppManager
    logs an errlog warning for every package that uses backslashes (#78):

        <pkg> appears to use backslashes as path separators

    Only packages with nested entries are affected - today that is the
    Helper (programming/, translations/, uidefinitions/); the other three are
    flat and the script is a no-op for them.

    Entry names are the only thing that changes: contents, order and the
    last-write timestamps are copied through unaltered.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Package not found: $Path"
}

# Read the whole package up front: rewriting entry names means rebuilding the
# archive, and a driver .pkg is tens of kilobytes.
$entries = New-Object System.Collections.Generic.List[object]
$needsRewrite = $false

$read = [System.IO.Compression.ZipFile]::OpenRead($Path)
try {
    foreach ($entry in $read.Entries) {
        $name = $entry.FullName
        if ($name.Contains('\')) { $needsRewrite = $true }

        $buffer = New-Object System.IO.MemoryStream
        $stream = $entry.Open()
        try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }

        $entries.Add([pscustomobject]@{
            Name         = $name.Replace('\', '/')
            Bytes        = $buffer.ToArray()
            LastWriteTime = $entry.LastWriteTime
        })
    }
}
finally { $read.Dispose() }

if (-not $needsRewrite) {
    Write-Host "NormalizePkgPaths: $([System.IO.Path]::GetFileName($Path)) already uses forward slashes."
    exit 0
}

$temp = "$Path.normalized"
$write = [System.IO.Compression.ZipFile]::Open($temp, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in $entries) {
        $created = $write.CreateEntry($item.Name, [System.IO.Compression.CompressionLevel]::Optimal)
        $created.LastWriteTime = $item.LastWriteTime
        $stream = $created.Open()
        try { $stream.Write($item.Bytes, 0, $item.Bytes.Length) } finally { $stream.Dispose() }
    }
}
finally { $write.Dispose() }

Move-Item -LiteralPath $temp -Destination $Path -Force
Write-Host "NormalizePkgPaths: rewrote $($entries.Count) entries in $([System.IO.Path]::GetFileName($Path)) to forward slashes."
