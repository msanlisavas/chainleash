<#
.SYNOPSIS
  CHAINLEASH self-serve onboarding — deploy YOUR OWN governed staking vault and arm it.

.DESCRIPTION
  One command takes you from nothing to a live, armed vault the agent will manage:

    deploy  → a fresh GovernedVault (Odra) under your agent account
    init    → set the agent, owner (you / your multisig) and per-action cap
    arm     → register your validator allowlist on-chain
    fund    → (optional) deposit CSPR for the agent to stake
    bond    → (optional) post the agent's slashable bond
    point   → write the new package hash + allowlist into the agent's appsettings.local.json

  Each step reuses a proven spike command (dotnet run -- <cmd>); see CLAUDE.md / RUNBOOK.

.PREREQUISITES
  - spike/ChainLeash.Spike/Config/settings.local.json with CsprCloudAccessKey + the agent
    key path + ChainName (run `dotnet run -- keygen` and fund the agent at the faucet).
  - The optimized GovernedVault.wasm built in the chainleash-odra container
    (contracts/governed_vault/wasm/GovernedVault.wasm).

.EXAMPLE
  ./scripts/onboard.ps1 -CapCspr 600 -Validators @(
      '0106618e1493f73ee0bc67ffbad4ba4e3863b995d61786d9b9a68ec7676f697981',
      '017d96b9a63abcb61c870a4f55187a0a7ac24096bdb5fc585c12a686a4d892009e') `
    -DepositCspr 1000 -BondCspr 300
#>
[CmdletBinding()]
param(
  [decimal]$CapCspr = 600,
  [string[]]$Validators = @(),
  [decimal]$DepositCspr = 0,
  [decimal]$BondCspr = 0,
  [string]$OwnerPublicKeyHex = '',
  [string]$CsprClickAppId = 'csprclick-template'
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$spikeDir  = Join-Path $root 'spike/ChainLeash.Spike'
$agentCfg  = Join-Path $root 'backend/ChainLeash.Agent/appsettings.local.json'
function Motes([decimal]$cspr) { [long]([math]::Round($cspr * 1000000000)) }

function Run-Spike([string[]]$cmdArgs) {
  Push-Location $spikeDir
  try { return (dotnet run --no-build -- @cmdArgs 2>&1 | Out-String) }
  finally { Pop-Location }
}

Write-Host "==> Building spike harness..." -ForegroundColor Cyan
Push-Location $spikeDir; try { dotnet build -clp:ErrorsOnly | Out-Null } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "spike build failed — fix the build before onboarding (later steps run with --no-build)." }

# 1) Deploy a fresh GovernedVault.
Write-Host "==> [1/6] Deploying GovernedVault..." -ForegroundColor Cyan
$deploy = Run-Spike @('vault-deploy')
Write-Host $deploy
if ($deploy -notmatch 'IsSuccess=True') { throw "deploy did not report success — check gas (~500 CSPR) and the wasm path." }

# 2) Discover the package hash from the agent's named keys.
Write-Host "==> Resolving package hash..." -ForegroundColor Cyan
$find = Run-Spike @('vault-find')
$m = [regex]::Match($find, 'governed_vault_package_hash\s*=\s*(hash-[0-9a-fA-F]{64})')
if (-not $m.Success) { Write-Host $find; throw "could not find governed_vault_package_hash in the agent's named keys." }
$pkg = $m.Groups[1].Value
Write-Host "    package: $pkg" -ForegroundColor Green

# 3) Initialize (agent, owner, per-action cap).
Write-Host "==> [2/6] Initializing (cap = $CapCspr CSPR)..." -ForegroundColor Cyan
Write-Host (Run-Spike @('vault-init', $pkg, "$(Motes $CapCspr)"))

# 4) Arm the validator allowlist.
if ($Validators.Count -eq 0) { Write-Warning "no -Validators given; the agent will have an empty allowlist and stay idle." }
foreach ($v in $Validators) {
  Write-Host "==> [3/6] Allowlisting validator $($v.Substring(0,10))..." -ForegroundColor Cyan
  Write-Host (Run-Spike @('vault-set-validator', $pkg, $v, 'true'))
}

# 5) Optional: fund the vault.
if ($DepositCspr -gt 0) {
  Write-Host "==> [4/6] Depositing $DepositCspr CSPR into the vault..." -ForegroundColor Cyan
  Write-Host (Run-Spike @('vault-deposit', $pkg, "$(Motes $DepositCspr)"))
}

# 6) Optional: post the agent's slashable bond.
if ($BondCspr -gt 0) {
  Write-Host "==> [5/6] Posting $BondCspr CSPR slashable bond..." -ForegroundColor Cyan
  Write-Host (Run-Spike @('vault-bond', $pkg, "$(Motes $BondCspr)"))
}

# 7) Point the agent at the new vault (merge into appsettings.local.json — keeps secrets).
Write-Host "==> [6/6] Pointing the agent at the new vault..." -ForegroundColor Cyan
if (-not $OwnerPublicKeyHex) {
  $ownerFile = Join-Path $spikeDir 'secrets/human/public_key_hex'
  if (Test-Path $ownerFile) { $OwnerPublicKeyHex = (Get-Content $ownerFile -Raw).Trim() }
}

$cfg = @{}
if (Test-Path $agentCfg) { $cfg = Get-Content $agentCfg -Raw | ConvertFrom-Json -AsHashtable }
if (-not $cfg.ContainsKey('Casper'))    { $cfg['Casper'] = @{} }
if (-not $cfg.ContainsKey('Staking'))   { $cfg['Staking'] = @{} }
if (-not $cfg.ContainsKey('Dashboard')) { $cfg['Dashboard'] = @{} }
$cfg['Casper']['GovernedVaultPackageHash'] = $pkg
if ($OwnerPublicKeyHex) { $cfg['Casper']['OwnerPublicKeyHex'] = $OwnerPublicKeyHex }
$cfg['Staking']['Allowlist'] = $Validators
$cfg['Dashboard']['CsprClickAppId'] = $CsprClickAppId
$cfg | ConvertTo-Json -Depth 8 | Set-Content $agentCfg -Encoding utf8

Write-Host ""
Write-Host "✅ Vault live and armed." -ForegroundColor Green
Write-Host "   package : $pkg"
Write-Host "   owner   : $OwnerPublicKeyHex"
Write-Host "   cap     : $CapCspr CSPR/action   allowlist: $($Validators.Count) validator(s)"
Write-Host "   agent   : config written to $agentCfg"
Write-Host ""
Write-Host "Next: cd backend/ChainLeash.Agent && dotnet run   (dashboard at http://localhost:5179, health at /health)"
