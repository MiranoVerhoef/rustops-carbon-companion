# Changelog

## 0.5.3 — 2026-07-09

- Prevented duplicate WebSocket receive loops after pairing or reconnecting.

## 0.5.2 — 2026-07-09

- Added `rustops.update`.
- Added dashboard-triggered manual update checks.
- Added WebSocket-driven update announcements with SHA-256 verification.

## 0.5.1 — 2026-07-09

- Added WebSocket-pushed update support.

## 0.5.0 — 2026-07-09

- Added custom chat delivery without Rust's `SERVER` sender prefix.

## 0.4.1 — 2026-07-07

- Fixed warning acknowledgement compatibility across Carbon production builds.

## 0.3.0 — 2026-07-06

- Added canonical plugin IDs, lifecycle commands, JSON config management, backups,
  and verified automatic updates.

## 0.1.0 — 2026-07-06

- Initial pairing, plugin lifecycle, and configuration bridge.
