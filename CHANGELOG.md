# Changelog

## 0.7.1 — 2026-07-13

- Verifies load, unload, and reload against Carbon lifecycle events before reporting success.
- Stops stale loaded assemblies from changing unloaded plugins back to loaded in the dashboard.
- Reports a clear failure when Carbon does not reach the requested plugin state.

## 0.7.0 — 2026-07-13

- Adds premium plugin source upload, download, and deletion.
- Creates an automatic backup before every replacement or deletion.
- Retains five source versions per plugin and supports dashboard restore.
- Restricts file access to top-level `.cs` files in the canonical Carbon plugin directory.

## 0.6.3 — 2026-07-12

- Adds stable and beta update channels controlled per paired server.
- Reports and persists the selected release channel.
- Fetches channel-specific SHA-256 manifests from the RustOps control plane.

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
