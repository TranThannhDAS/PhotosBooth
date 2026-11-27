# Cài mẫu cưới đi kèm vào thư viện template của máy hiện tại.
# Chỉ thêm template chưa tồn tại, không ghi đè mẫu custom hoặc library.json.
[CmdletBinding()]
param(
    [string] $LibraryRoot = (Join-Path $env:LOCALAPPDATA 'Photobooth\Templates')
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\src\Photobooth.Desktop\Templates'
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Không tìm thấy thư mục mẫu đi kèm: $source"
}
New-Item -ItemType Directory -Force -Path $LibraryRoot | Out-Null
$installed = 0
$skipped = 0
Get-ChildItem -LiteralPath $source -Directory -Filter 'wedding-*' | ForEach-Object {
    $templateFile = Join-Path $_.FullName 'template.json'
    if (-not (Test-Path -LiteralPath $templateFile -PathType Leaf)) { return }
    $target = Join-Path $LibraryRoot $_.Name
    if (Test-Path -LiteralPath $target) {
        Write-Host "Bỏ qua (đã tồn tại): $($_.Name)"
        $skipped++
        return
    }
    Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse
    Write-Host "Đã cài: $($_.Name)"
    $installed++
}
Write-Host "Hoàn thành: $installed mẫu mới; $skipped mẫu bỏ qua. Vào Admin > Template Manager > Tải lại."
