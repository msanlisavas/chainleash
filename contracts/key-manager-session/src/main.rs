#![no_std]
#![no_main]

extern crate alloc;

use casper_contract::contract_api::{account, runtime};
use casper_contract::unwrap_or_revert::UnwrapOrRevert;
use casper_types::account::{ActionType, Weight};
use casper_types::PublicKey;

const ARG_HUMAN_KEY: &str = "human_key";
const ARG_HUMAN_WEIGHT: &str = "human_weight";
const ARG_DEPLOY_THRESHOLD: &str = "deploy_threshold";
const ARG_KEYMGMT_THRESHOLD: &str = "keymgmt_threshold";

/// One-time configuration of the CHAINLEASH treasury account.
///
/// Run by the agent key while key_management threshold is still its default (1),
/// so the agent can self-configure exactly once. After this lands, the agent key
/// (weight 1) is BELOW the key_management threshold and can never again modify
/// keys or raise its own authority — only the human key can.
#[no_mangle]
pub extern "C" fn call() {
    let human: PublicKey = runtime::get_named_arg(ARG_HUMAN_KEY);
    let human_weight: u8 = runtime::get_named_arg(ARG_HUMAN_WEIGHT);
    let deploy_threshold: u8 = runtime::get_named_arg(ARG_DEPLOY_THRESHOLD);
    let keymgmt_threshold: u8 = runtime::get_named_arg(ARG_KEYMGMT_THRESHOLD);

    // Add the human key first so total account weight can satisfy the new,
    // higher key_management threshold set below.
    account::add_associated_key(human.to_account_hash(), Weight::new(human_weight))
        .unwrap_or_revert();

    // Deployment stays low (agent acts routinely); key_management is raised above
    // the agent's weight so only the human can manage keys / raise authority.
    account::set_action_threshold(ActionType::Deployment, Weight::new(deploy_threshold))
        .unwrap_or_revert();
    account::set_action_threshold(ActionType::KeyManagement, Weight::new(keymgmt_threshold))
        .unwrap_or_revert();
}
