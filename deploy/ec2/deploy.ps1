# One-command deploy of the Seat Inventory API from your Windows PC to the EC2 server.
# It: packs the code -> copies it to EC2 -> builds -> starts -> checks /health.
# The server must already run Docker and Identity (Seat Inventory checks tokens against Identity's
# public keys), and the AWS resources (DynamoDB table, queues, topics) must exist.
#
# Usage (PowerShell, from anywhere):
#   .\deploy\ec2\deploy.ps1
#   .\deploy\ec2\deploy.ps1 -HostIp 15.207.51.22      # when the EC2 public IP changed
param(
    [string]$HostIp = "15.207.51.22",
    [string]$KeyPath = "$HOME\Downloads\routego-dev-key.pem",
    [string]$User = "ec2-user"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$archive = Join-Path $env:TEMP "seat-inventory.tar.gz"
$target = "$User@$HostIp"

function Assert-LastExit([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "FAILED at step: $step (exit code $LASTEXITCODE)" }
}

if (-not (Test-Path $KeyPath)) { throw "Key file not found: $KeyPath  (pass -KeyPath <path to .pem>)" }

Write-Host "1/3 Packing code from $repoRoot" -ForegroundColor Cyan
tar.exe -czf $archive --exclude=bin --exclude=obj --exclude=.git --exclude=.vs --exclude=.env -C $repoRoot .
Assert-LastExit "pack code"

Write-Host "2/3 Copying code to $target" -ForegroundColor Cyan
scp -i $KeyPath -o StrictHostKeyChecking=accept-new $archive "${target}:~/seat-inventory.tar.gz"
Assert-LastExit "copy code"

Write-Host "3/3 Unpacking, building and starting on the server" -ForegroundColor Cyan
$remote = "rm -rf ~/seat-inventory && mkdir -p ~/seat-inventory" +
    " && tar -xzf ~/seat-inventory.tar.gz -C ~/seat-inventory" +
    " && sudo bash ~/seat-inventory/deploy/ec2/start.sh"
ssh -i $KeyPath $target $remote
Assert-LastExit "build and start"

Write-Host "DONE. Seat Inventory API is internal only (the portal calls it as http://seat-inventory:8080/)." -ForegroundColor Green
