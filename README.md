# EVE Contract Tracker

Windows desktop app (.NET 10, WPF + Blazor Hybrid) for monitoring EVE Online contracts. Two views:

- **Profit Scanner** — pulls public contracts from ESI for a chosen region, prices their contents against Jita 4-4 buy/sell, and flags contracts that can be bought and resold at a profit (`BUY` / `THIN` / `LOW VOL` / `SKIP` / `LOWSEC` verdicts). Ships + modules only; scam-pattern, low-volume and expiry risk flags in the detail panel.
- **Our Contracts** — tracks inbound/outbound contracts across 12+ authenticated characters (ESI OAuth PKCE), with status, value, collateral and expiry, plus per-character auth status.

The UI is a pixel-faithful port of the design in [`design_handoff_contract_tracker/`](design_handoff_contract_tracker/README.md).

## Install

Grab `ContractTracker-win-Setup.exe` from the **installer** artifact of any [Actions run](../../actions) (or from a Release once one is tagged) and run it. Installed copies check GitHub Releases on startup and every 6 h; when a new `v*` release exists, an update chip appears in the header — one click downloads (delta when possible) and restarts into the new version.

Releasing a version:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

CI builds, tests, and attaches the installer + update packages + a portable zip to the GitHub Release.

## Build & run from source

```
dotnet build
dotnet run --project src/EveContracts.App
```

Requires the .NET 10 SDK and the WebView2 runtime (preinstalled on Windows 11). Dev and portable-zip runs never self-update — only installed copies do.

## First run

On first startup the app downloads the EVE static data export (item types, groups, packaged volumes, solar systems, stations, stargate graph — ~40 MB from fuzzwork.co.uk) into `%LOCALAPPDATA%\EveContracts\sde`, computes jumps-to-Jita offline via BFS over the stargate graph, then starts the first public-contract scan. Static data is re-downloaded automatically when older than 7 days (game patches change item data).

## Profit Scanner data flow

| What | Source | Cadence |
|---|---|---|
| Public contracts | ESI `/contracts/public/{region}/` (ETag-aware) | 30 min |
| Contract items | ESI `/contracts/public/items/{id}/` — **only for never-seen ids** | on discovery |
| Jita buy/sell | Fuzzwork market aggregates (batched) | hourly |
| Prev-day traded volume | ESI `/markets/10000002/history/` per type in live contracts | ~daily |
| Static data (SDE) | Fuzzwork CSV dump | 7 days |

Everything lands in a local SQLite cache (`%LOCALAPPDATA%\EveContracts\evecontracts.db`), so UI filters never touch ESI. Contracts are purged 3 days after they finished or should have finished.

### Evaluation pipeline (per contract)

1. `item_exchange` only (auctions optional), want-to-buy skipped
2. highsec pickup check (when enabled)
3. every included item above its Jita volume/day floor (per-item override supported)
4. price ≤ max price cap
5. `net = Σ value×qty − price − fees(4.5%) − hauling(m³ × 800 ISK × (0.5 + jumps/10))`
   - liquid items are valued at Jita sell min; **illiquid items (below the volume floor) at Jita buy max** — the sell wall on a dead market is routinely a fake 10–100× order placed by the contract issuer
   - requested (asked-for) items are costed at Jita **sell** — what it costs to acquire them, the gap swap-scams live in
6. Verdict: LOWSEC → **SCAM** → SKIP → LOW VOL → THIN → BUY
   - SCAM: free bait (price ≤ 0 offering value), unpriced requested items, or a >10× return on an illiquid item; SCAM rows sort below everything else. A >10× return on a *liquid* item stays eligible (genuine mispriced snipe) but carries a ⚑ TOO GOOD flag.

## Our Contracts setup

1. Register an application at [developers.eveonline.com](https://developers.eveonline.com) with callback URL `http://localhost:8635/callback/` and scopes `esi-contracts.read_character_contracts.v1 esi-universe.read_structures.v1`.
2. In the app: ⚙ Settings → paste the Client ID → Save → "Add character (ESI login)". Repeat per character.

Refresh tokens are stored DPAPI-encrypted (current user) in the local database. Expired tokens surface in the character rail with a re-auth link.

## Project layout

```
src/EveContracts.Core   domain, EF Core/SQLite cache, ESI client + OAuth, SDE loader, evaluation
src/EveContracts.App    WPF Blazor Hybrid UI (Razor components + design-token CSS)
tests/EveContracts.Tests  evaluation pipeline, ISK formatting, CSV parser
```
