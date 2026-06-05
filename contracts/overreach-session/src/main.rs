#![no_std]
#![no_main]

extern crate alloc;

use casper_contract::contract_api::account;
use casper_contract::unwrap_or_revert::UnwrapOrRevert;
use casper_types::account::{ActionType, Weight};

/// Spike B — the "unleash" attempt.
///
/// The agent key (weight 1, BELOW the key_management threshold of 3) tries to
/// lower its own key_management threshold back to 1 — i.e. remove its own leash.
/// Changing a threshold is a key-management action, so the protocol must reject
/// this at the host-function authorization check. We deploy this signed by the
/// agent ONLY and record exactly what the network returns (executed-failed tx
/// vs pre-inclusion reject).
#[no_mangle]
pub extern "C" fn call() {
    account::set_action_threshold(ActionType::KeyManagement, Weight::new(1)).unwrap_or_revert();
}
