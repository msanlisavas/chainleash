#!/usr/bin/env bash
# Build (with a recent wasm-opt) + deploy GovernedVault to Casper testnet.
# Run in the chainleash-odra container (see tools/odra-build/Dockerfile).
set -euo pipefail

# cargo-odra's wasm-opt step needs a recent binaryen; Debian's apt version is too old
# (lacks --llvm-memory-copy-fill-lowering). Install a fresh one if missing.
if ! wasm-opt --help 2>/dev/null | grep -q -- '--llvm-memory-copy-fill-lowering'; then
  echo "Installing recent binaryen..."
  TAG=$(curl -sSL https://api.github.com/repos/WebAssembly/binaryen/releases/latest | grep -oP '"tag_name": "\K[^"]+')
  echo "binaryen $TAG"
  curl -sSL "https://github.com/WebAssembly/binaryen/releases/download/$TAG/binaryen-$TAG-x86_64-linux.tar.gz" | tar xz -C /opt
  ln -sf "/opt/binaryen-$TAG/bin/wasm-opt" /usr/local/bin/wasm-opt
fi
wasm-opt --version

# cargo-odra also calls wasm-strip (from wabt).
if ! command -v wasm-strip >/dev/null 2>&1; then
  echo "Installing wabt (wasm-strip)..."
  apt-get update -qq && apt-get install -y -qq --no-install-recommends wabt && rm -rf /var/lib/apt/lists/*
fi
wasm-strip --version || true

echo "=== test ==="
cargo test --color never 2>&1 | tail -15
echo "=== build wasm (with wasm-opt) ==="
cargo odra build 2>&1 | tail -8
