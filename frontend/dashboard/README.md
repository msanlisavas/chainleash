# CHAINLEASH dashboard

The live operator dashboard for the CHAINLEASH staking agent: the full leash state read
from chain (per-action cap, free/total balance, slashable bond, per-validator cap,
violations, kill-switch), a live audit feed of every agent decision, the validator policy
view, and the owner's **in-wallet co-sign** action for material proposals (via CSPR.click —
the server never holds the owner key).

Angular 20, standalone single component, signals + SignalR. No router, no state library —
the agent is the single source of truth and everything renders from its snapshot + stream.

## Develop

```bash
npm ci
npm start          # ng serve on :4200, talks to the agent at http://localhost:5179
```

Run the agent first (`dotnet run` in `backend/ChainLeash.Agent`) — with no keys it boots
in observer mode, which is enough to develop against.

## Build

```bash
npm run build      # emits straight into ../../backend/ChainLeash.Agent/wwwroot
```

In production the agent serves the built dashboard itself — same origin, no CORS.

## Test

```bash
npm run test:ci    # karma/jasmine, headless — pure view logic only (no network)
```
