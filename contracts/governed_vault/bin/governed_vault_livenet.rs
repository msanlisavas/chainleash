//! Deploy GovernedVault to Casper testnet via the Odra livenet backend.
//! Run inside the Linux build container:
//!   cargo run --bin governed_vault_livenet --features livenet
//! Requires .env with ODRA_CASPER_LIVENET_* vars + CSPR_CLOUD_AUTH_TOKEN.

use std::str::FromStr;

use odra::casper_types::U512;
use odra::host::Deployer;
use odra::prelude::{Address, Addressable};

use governed_vault::governed_vault::{GovernedVault, GovernedVaultInitArgs};

fn main() {
    let env = odra_casper_livenet_env::env();

    // agent = the funded weighted-key treasury account (from Spikes A-C).
    let agent = Address::from_str(
        "hash-11b5fdcc0b9653c5d67891c675d1548193779b7ff0a9c942c03f7e6752b52aeb",
    )
    .unwrap();
    // owner = the human's standalone account (distinct from the agent so the
    // contract can require the human for material approvals + cap raises).
    let owner = Address::from_str(
        "hash-5bc1cf012c678676ff14c3cd3d2d72ac19d17819d448de4795f7bf1618bfd232",
    )
    .unwrap();

    // 2 CSPR per-action cap.
    let init_args = GovernedVaultInitArgs {
        agent,
        owner,
        value_cap: U512::from(2_000_000_000u64),
    };

    env.set_gas(1_000_000_000_000u64);
    let vault = GovernedVault::deploy(&env, init_args);
    println!("GovernedVault deployed at: {}", vault.address().to_string());
}
