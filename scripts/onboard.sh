#!/usr/bin/env bash
# CHAINLEASH self-serve onboarding (Linux/macOS) — the bash twin of scripts/onboard.ps1.
# Deploy YOUR OWN governed staking vault and arm it, then point the agent at it.
#
# One command: deploy GovernedVault -> init (agent/owner/cap) -> arm the validator
# allowlist on-chain -> optional deposit + bond -> write the agent's appsettings.local.json.
#
# Prerequisites:
#   - .NET 10 SDK (dotnet), bash, awk/grep/sed (standard on Linux/macOS)
#   - spike/ChainLeash.Spike/Config/settings.local.json with CsprCloudAccessKey + the agent
#     key path + ChainName (run `dotnet run -- keygen` in the spike, fund at the faucet)
#   - contracts/governed_vault/wasm/GovernedVault.wasm (committed — no Odra toolchain needed)
#
# Usage:
#   ./scripts/onboard.sh --cap 600 \
#     --validators 0106618e1493f73ee0bc67ffbad4ba4e3863b995d61786d9b9a68ec7676f697981,017d96b9a63abcb61c870a4f55187a0a7ac24096bdb5fc585c12a686a4d892009e \
#     --deposit 1000 --bond 300
set -euo pipefail

CAP=600; VALIDATORS=""; DEPOSIT=0; BOND=0; MAXVAL=0; COOLDOWN=30; OWNER=""; APPID="csprclick-template"
while [ $# -gt 0 ]; do
  case "$1" in
    --cap)               CAP="$2"; shift 2 ;;
    --validators)        VALIDATORS="$2"; shift 2 ;;   # comma-separated hexes
    --deposit)           DEPOSIT="$2"; shift 2 ;;
    --bond)              BOND="$2"; shift 2 ;;
    --max-per-validator) MAXVAL="$2"; shift 2 ;;       # concentration cap (0 = unlimited)
    --cooldown)          COOLDOWN="$2"; shift 2 ;;     # anti-thrash seconds (0 = off)
    --owner)             OWNER="$2"; shift 2 ;;
    --appid)             APPID="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 1 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SPIKE="$ROOT/spike/ChainLeash.Spike"
AGENT_CFG="$ROOT/backend/ChainLeash.Agent/appsettings.local.json"
motes() { awk "BEGIN{printf \"%.0f\", $1 * 1000000000}"; }
gt0()   { awk "BEGIN{exit !($1 > 0)}"; }
run_spike() { ( cd "$SPIKE" && dotnet run --no-build -- "$@" ); }

echo "==> Building spike harness..."
( cd "$SPIKE" && dotnet build -clp:ErrorsOnly >/dev/null ) || { echo "spike build failed" >&2; exit 1; }

echo "==> [1/6] Deploying GovernedVault..."
DEPLOY="$(run_spike vault-deploy)"; echo "$DEPLOY"
echo "$DEPLOY" | grep -q "IsSuccess=True" || { echo "deploy did not report success — check gas (~500 CSPR) and the wasm path." >&2; exit 1; }

echo "==> Resolving package hash..."
FIND="$(run_spike vault-find)"
PKG="$(printf '%s\n' "$FIND" | grep -oE 'hash-[0-9a-fA-F]{64}' | head -1)"
[ -n "$PKG" ] || { echo "could not find governed_vault_package_hash:" >&2; printf '%s\n' "$FIND" >&2; exit 1; }
echo "    package: $PKG"

echo "==> [2/6] Initializing (cap = $CAP CSPR)..."
run_spike vault-init "$PKG" "$(motes "$CAP")"

if [ -n "$VALIDATORS" ]; then
  IFS=',' read -ra VS <<< "$VALIDATORS"
  for v in "${VS[@]}"; do
    echo "==> [3/6] Allowlisting validator ${v:0:10}..."
    run_spike vault-set-validator "$PKG" "$v" true
  done
else
  echo "WARNING: no --validators given; the agent will have an empty allowlist and stay idle." >&2
fi

if gt0 "$DEPOSIT"; then
  echo "==> [4/6] Depositing $DEPOSIT CSPR into the vault..."
  run_spike vault-deposit "$PKG" "$(motes "$DEPOSIT")"
fi
if gt0 "$BOND"; then
  echo "==> [5/6] Posting $BOND CSPR slashable bond..."
  run_spike vault-bond "$PKG" "$(motes "$BOND")"
fi

# Decentralization controls: per-validator concentration cap (opt-in) + anti-thrash cooldown
# (on by default) — so a new vault isn't running the weakest posture.
if gt0 "$MAXVAL"; then
  echo "==> Setting per-validator cap = $MAXVAL CSPR..."
  run_spike vault-set-maxval "$PKG" "$(motes "$MAXVAL")"
fi
if [ "${COOLDOWN:-0}" -gt 0 ] 2>/dev/null; then
  echo "==> Setting action cooldown = $COOLDOWN s..."
  run_spike vault-set-interval "$PKG" "$((COOLDOWN * 1000))"
fi

echo "==> [6/6] Pointing the agent at the new vault..."
if [ -z "$OWNER" ] && [ -f "$SPIKE/secrets/human/public_key_hex" ]; then
  OWNER="$(tr -d '[:space:]' < "$SPIKE/secrets/human/public_key_hex")"
fi
# Reuse the CSPR.cloud key the spike already uses (falls back to the public testnet key).
CSPR_KEY="$(grep -oE '"CsprCloudAccessKey"[[:space:]]*:[[:space:]]*"[^"]*"' "$SPIKE/Config/settings.local.json" 2>/dev/null | sed -E 's/.*"([^"]*)" *$/\1/' | head -1)"
[ -n "$CSPR_KEY" ] || CSPR_KEY="55f79117-fc4d-4d60-9956-65423f39a06a"
# Build the allowlist JSON array.
ALLOW_JSON=""
if [ -n "$VALIDATORS" ]; then
  IFS=',' read -ra VS <<< "$VALIDATORS"
  for v in "${VS[@]}"; do ALLOW_JSON="${ALLOW_JSON}\"$v\","; done
  ALLOW_JSON="${ALLOW_JSON%,}"
fi
cat > "$AGENT_CFG" <<EOF
{
  "Casper": {
    "CsprCloudAccessKey": "$CSPR_KEY",
    "GovernedVaultPackageHash": "$PKG",
    "OwnerPublicKeyHex": "$OWNER",
    "AllowServerKeyCoSign": false
  },
  "Staking": { "Allowlist": [${ALLOW_JSON}], "BondCspr": $BOND },
  "Dashboard": { "CsprClickAppId": "$APPID" }
}
EOF

echo ""
echo "✅ Vault live and armed."
echo "   package : $PKG"
echo "   owner   : $OWNER"
VCOUNT=0; [ -n "$VALIDATORS" ] && VCOUNT="$(printf '%s' "$VALIDATORS" | tr ',' '\n' | grep -c .)"
echo "   cap     : $CAP CSPR/action   allowlist: $VCOUNT validator(s)"
echo "   agent   : config written to $AGENT_CFG"
echo ""
echo "Next: (cd backend/ChainLeash.Agent && dotnet run)  -> dashboard http://localhost:5179, health /health"
