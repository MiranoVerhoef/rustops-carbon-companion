# Changelog

## 0.6.2 — 2026-07-11

- Treats stable proxy/server WebSocket closes as normal reconnects.
- Keeps short-lived unclean closes as real failures so bad proxy routing remains visible.

## 0.6.1 — 2026-07-11

- Detects already-loaded Carbon/uMod-style plugins when the companion starts.
- Keeps plugin loaded state fresh when the dashboard lists plugins.
- Updates the signed release manifest so verified updates match the served source file.

## 0.6.0 — 2026-07-10

- Rejects incompatible or malformed control-plane protocol messages clearly.
- Serializes WebSocket writes to prevent overlapping `SendAsync` failures.
- Adds working remote companion status, automatic-update, and retry operations.
- Restores plugin configuration backups using atomic file replacement.
- Reports observed plugin load state and recent Carbon compilation errors.

## 0.5.5 — 2026-07-10

- Treat short-lived WebSocket sessions as failures so unstable proxies eventually pause retries.
- Throttle repeated successful-connection notices in the game console.

## 0.5.4 — 2026-07-09

- Added smarter WebSocket reconnect backoff.
- Pauses repeated failed connections instead of spamming console logs.
- Added `rustops.retry` to force a reconnect attempt.
- Expanded `rustops.status` with next retry and last error.

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
