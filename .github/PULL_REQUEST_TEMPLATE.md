<!-- Thanks for contributing to CHAINLEASH. Keep this concise. -->

## What & why

<!-- What does this change, and why? Link any issue. -->

## How it preserves the leash

<!-- If this touches the contract, agent authority, or the co-sign flow: how does it keep the
     custody guarantee (agent can rebalance within the leash, only the owner can move CSPR out)? -->

## Testing

- [ ] Contract: `cargo test` (in `contracts/governed_vault` / the `chainleash-odra` container)
- [ ] Backend: `dotnet test backend/ChainLeash.Tests`
- [ ] Dashboard: `npm run test:ci` (and `npx ng build`)

<!-- Note anything verified on testnet, with tx hashes if relevant. -->
