# Reusable Odra build/deploy environment for CHAINLEASH contracts.
# Works around casper-types not host-compiling on Windows; bundles cargo-odra,
# the pinned nightly + wasm target, and binaryen (wasm-opt) so builds optimize.
#
# Build once:  docker build -t chainleash-odra tools/odra-build
# Use:         docker run --rm -v "<contractdir>:/work" -v ... chainleash-odra \
#                bash -c "cargo odra build && cargo run --bin <x>_livenet --features livenet"
# Pinned base (the toolchain that matters is the nightly below; the host rust just bootstraps it).
FROM rust:1.93-bookworm

RUN apt-get update \
 && apt-get install -y --no-install-recommends binaryen \
 && rm -rf /var/lib/apt/lists/*

RUN rustup toolchain install nightly-2026-01-01 \
 && rustup target add wasm32-unknown-unknown --toolchain nightly-2026-01-01

RUN cargo install cargo-odra --version 0.1.7

WORKDIR /work
