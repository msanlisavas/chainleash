#![no_std]
#![no_main]

extern crate alloc;

use casper_contract::contract_api::account;
use casper_contract::unwrap_or_revert::UnwrapOrRevert;
use casper_types::account::{ActionType, Weight};

/// Spike C — human co-sign proof.
///
/// The human key (weight 3, which meets the key_management threshold of 3)
/// re-asserts key_management = 3. This is a key-management action the agent
/// (weight 1) was just rejected for in Spike B. Non-destructive (sets the
/// threshold to its current value) — it exists purely to prove that the human
/// key CAN authorize key management.
#[no_mangle]
pub extern "C" fn call() {
    account::set_action_threshold(ActionType::KeyManagement, Weight::new(3)).unwrap_or_revert();
}
