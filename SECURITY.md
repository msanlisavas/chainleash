# Security Policy

## Reporting a vulnerability

Please report security issues privately rather than opening a public issue:

- Use GitHub's private vulnerability reporting: **Security → Report a vulnerability**, or
- Email **muratsanlisavas@gmail.com**.

Include a description, reproduction steps, and the potential impact. I aim to acknowledge
reports within 72 hours and to address confirmed high-severity issues promptly.

## What's most valuable

CHAINLEASH is a bonded, protocol-governed staking agent on Casper. Its core guarantee is that
the agent can rebalance stake within an on-chain leash but **can never move CSPR out of the
vault — only the owner can**. Reports that demonstrate a way to:

- break that custody guarantee or drain the vault,
- escalate the agent's authority (it sits below the account's key-management threshold),
- forge or replay an "owner co-signed" action, or
- reach the owner-gated endpoints without the owner's wallet signature

are especially valuable.

The contract, backend, and dashboard have been through several rounds of adversarial review
(summarised in the README). `main` is protection-ruled; Dependabot alerts and CodeQL code
scanning run on every push, and the production co-sign flow is wallet-only — the server never
holds the owner key.

## Supported versions

This is an active buildathon project; only the latest `main` is supported.
