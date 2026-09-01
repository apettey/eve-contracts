# Handoff: EVE Online Contract Tracker

## Overview
A contract-monitoring tool for EVE Online with two functions:

1. **Profit Scanner** — pull public contracts from ESI, price their contents against Jita buy/sell, and flag contracts that can be bought and resold at a major profit.
2. **Our Contracts** — track inbound and outbound contracts across 12+ authenticated characters (ESI OAuth), with status, value, collateral, and expiry.

## Target Platform — IMPORTANT
This is a **Windows desktop application on the .NET 10 framework**. The bundled HTML file is a design reference only. Recreate the UI in the appropriate .NET 10 UI stack (WPF, WinUI 3, or Blazor Hybrid — pick whichever the codebase already uses; if greenfield, WinUI 3 or Blazor Hybrid are the best fits for this dense, data-grid-heavy UI). The backend (ESI polling, caching, profit evaluation) is .NET 10 service code within the same app.

## About the Design Files
`Contract Tracker.dc.html` is a **design reference created in HTML** — a prototype showing intended look and behavior, not production code to copy. Recreate it in the .NET UI stack using its established patterns. All data in the prototype is mock, but shaped to match ESI responses.

## Fidelity
**High-fidelity.** Colors, typography, spacing, and interactions are final. Recreate pixel-perfectly.

---

# Architecture (backend spec)

## ESI Endpoints
- `GET /contracts/public/{region_id}/` — public contracts per region, paged. ESI cache: ~30 min.
- `GET /contracts/public/items/{contract_id}/` — items in a public contract. Fetch **only for contract IDs not already in the DB** — never re-fetch known contracts; ESI error-rate limits will bite otherwise.
- `GET /characters/{character_id}/contracts/` — own contracts per character (auth: `esi-contracts.read_character_contracts.v1`). ESI cache: ~5 min.
- Market prices: Jita 4-4 buy max / sell min per `type_id`, plus **previous-day traded volume** (from `/markets/{region_id}/history/` for The Forge). Refresh hourly.
- Static data: `type_id` → name, group/category, packaged volume (m³). Use the SDE or `/universe/types/{type_id}/` cached forever.

## Local Database (cache)
All contracts and prices are cached in a local database (SQLite via EF Core recommended) so the UI filters instantly without touching ESI.

Tables (suggested):
- `PublicContracts` — contract_id (PK), region_id, type (item_exchange/auction), title, price, start_location_id, system, security_status, date_issued, date_expired, volume_m3, first_seen, last_seen, evaluation fields (jita_sell_value, net_profit, margin, verdict).
- `ContractItems` — contract_id (FK), type_id, quantity, is_included.
- `Prices` — type_id (PK), jita_sell, jita_buy, prev_day_volume, updated_at.
- `Characters` — character_id, name, refresh_token (DPAPI-encrypted), token_expiry, auth_status.
- `OwnContracts` — contract_id, character_id, direction (issuer/assignee), type, status, price, reward, collateral, date_issued, date_expired, date_completed.
- `ItemSettings` — type_id, min_daily_volume_override, excluded (bool).
- `AppSettings` — global thresholds (min margin %, default min volume, max price, fee %, haul rate ISK/m³, highsec_only).

### Retention rule
Contracts are **purged 3 days after they finished or should have finished**: scheduled job deletes rows where `max(date_completed, date_expired) + 3 days < now`. Applies to both public and own contracts. Keeps the DB cycling and small.

### Update cycle
- Public contracts: poll each enabled region every 30 min; upsert by contract_id; fetch items only for new IDs; re-run evaluation for all live contracts after each price refresh.
- Prices/volume: hourly.
- Own contracts: every 5–10 min per authed character; refresh OAuth tokens as needed; surface expired tokens in UI.

## Profit Evaluation (per public contract)
Scope: **ships and modules only** — exclude minerals, gas, ore, PI, and other categories (filter by SDE category: Ship=6, Module=7; also allow Charge=8 optionally off).

Pipeline, in order:
1. Contract type is item_exchange (auctions optional, off by default). Skip want-to-buy (price ≤ 0 with items requested).
2. Pickup system security ≥ 0.5 (highsec check) when highsec_only is on.
3. Every included item has Jita previous-day volume > its threshold (per-item override from `ItemSettings`, else global default).
4. Contract price ≤ max price cap.
5. Compute:
   - `jita_sell_value = Σ item.jita_sell × qty` (included items only; excluded/requested items subtract at jita_buy)
   - `fees = jita_sell_value × feePct` (default 4.5% — sales tax + broker)
   - `hauling = total_m3 × haulRate × (0.5 + jumps_to_jita / 10)`; 0 if already in Jita (haulRate default 800 ISK/m³)
   - `net_profit = jita_sell_value − price − fees − hauling`
   - `margin = net_profit / price`
6. Verdict: `LOWSEC` (failed 2) → `SKIP` (profit ≤ 0) → `LOW VOL` (failed 3) → `THIN` (margin < min margin %) → `BUY`.

Risk flags (shown in detail panel, don't block): scam patterns in title ("cheap", "quick sale", ★ characters), expires < 24 h, item volume below floor. Consider adding: rigged-hull detection (rigs destroy resale), damaged crystals, mutated/abyssal modules (unpriceable — flag, never auto-value).

## Own Contracts view
- 12+ characters, each with its own OAuth token; character rail shows auth status (green/red dot) and per-character contract counts; expired tokens surfaced with a re-auth prompt.
- Filters: direction (All/Outbound/Inbound — outbound = character is issuer, inbound = character/corp is assignee or acceptor), status (Outstanding, In progress, Finished, Expired; Rejected exists in data).
- Stats: total outstanding value, collateral at risk (courier), completed last 30 d, expiring < 24 h.

---

# UI Specification

## Design Tokens
Fonts: **IBM Plex Sans** (UI), **IBM Plex Mono** (all numbers, ISK values, badges, metadata).

Colors:
- Background: `#1a1817`; panel/header: `#1e1c1a`; hover/inset: `#242220`; selected row: `#26232e`
- Borders: `#2c2926` (strong), `#242220` (row dividers), `#3a3733` (inputs)
- Text: `#e8e4df` (primary), `#b5afa7` (secondary), `#8a847c` (dim/labels)
- Accent (violet): `oklch(0.72 0.16 275)` — active tab underline, selected pills, outstanding badge
- Green: `oklch(0.75 0.13 155)` — profit, BUY, highsec, auth OK
- Red: `oklch(0.68 0.17 25)` — loss, lowsec, expiring, auth expired
- Amber: `oklch(0.78 0.13 85)` — THIN verdict, in-progress, warnings
- Badge backgrounds: the badge color at ~10–12% alpha, e.g. `oklch(0.75 0.13 155 / 0.12)`

Type scale: 22px stat values (mono, 600), 16px app title (700), 13.5px table primary (600), 13px body/inputs, 12.5px secondary cells, 11–11.5px metadata, 10px uppercase column headers / labels (letter-spacing 0.1em).
Badges: mono 10px/600, padding 3px 8px, radius 4px. Pills (filters): radius 99px, padding 5px 13px, 1px border (violet when active). Inputs: `#242220` bg, `#3a3733` border, radius 4px.

## Layout
Full-height column: 60px header, then the active view fills the rest.

**Header**: title "CONTRACT TRACKER" + mono "EVE ONLINE"; two tab buttons (Profit Scanner / Our Contracts) with 2px violet bottom border on active; right side: green dot + "ESI · last scan Xm ago", bordered chip "N/12 characters authed".

**Profit Scanner** = main column + optional 400px right detail panel.
- Filter bar (padding 16px 28px, bottom border): Region select (The Forge, Domain, Sinq Laison, Heimatar, Metropolis), Min profit % slider (0–50), Min Jita volume/day number input, Max contract price slider (50M–2B), Highsec-only checkbox; right-aligned dim note "ships + modules only · minerals/gas excluded · purged 3d after completion/expiry".
- Stats strip: 4 equal cells (Contracts scanned / Passed filters [violet] / Best net profit [green] / Total opportunity), 1px gap grid on `#2c2926`.
- Table: sticky uppercase header; grid columns `minmax(200px,1.6fr) 1fr 90px 90px 90px 90px 70px 80px`, gap 14px, row padding 13px 28px. Columns: Contract (title bold + item summary dim), Location (system + station), Sec/Jumps (sec colored green/red, "· Nj"), Ask price, Jita sell val, Net profit (signed, green/red), Margin %, Verdict badge. Rows sorted by net profit desc; click selects (bg `#26232e`); hover `#242220`.
- Detail panel: contract title + location + expiry, ✕ close; flag badges (✓ PASSES ALL FILTERS, ⚠ ITEM BELOW VOLUME FLOOR, ⚠ LOWSEC PICKUP, ⏱ EXPIRES SOON, ⚑ SCAM PATTERN IN TITLE); "Items vs Jita" table (item + qty + m³, Jita sell, Jita buy, vol/day — vol red when below floor); "Profit breakdown" ledger: sell value − price − fees − hauling ⇒ net profit + margin, plus "Est. time to liquidate" (<1 day / 1–3 days / 3–7 days from worst item volume).

**Our Contracts** = 250px character rail + main column.
- Rail: "All characters" row + one row per character (auth dot, name, count), selected row gets `#26232e` bg + 2px violet left border; dashed-border notice when tokens are expired.
- Filter row: direction pills (All/Outbound/Inbound), divider, status pills (All/Outstanding/In progress/Finished/Expired).
- Stats strip: Outstanding value / Collateral at risk [amber] / Completed 30d [green] / Expiring <24h [red].
- Table: grid `70px minmax(180px,1.5fr) 1fr 1fr 90px 90px 80px 100px`, gap 14px. Columns: Dir badge (OUT violet / IN green), Contract (title + "type · route"), Character, Other party, Value, Collateral ("—" if none), Expires (red if <24 h, "—" if done), Status badge (Outstanding violet / In progress amber / Finished green / Expired dim / Rejected red).

## Interactions & State
- Tab switch swaps views; filters apply instantly against the local DB (no ESI call on filter change).
- Scanner state: region, minMargin, minVolume, maxPrice, highsecOnly, selected contract.
- Own state: character filter (toggle; click again to clear), direction filter, status filter.
- ISK formatting: 1.45B / 385M / 22K, minus sign `−`, profits prefixed `+`.
- No animations required; hover states only.

## Assets
None — no images or icon fonts. The few glyphs (✕ ✓ ⚠ ⏱ ⚑ ★ dots) are unicode text.

## Files
- `Contract Tracker.dc.html` — the full interactive prototype (both views, all states).
