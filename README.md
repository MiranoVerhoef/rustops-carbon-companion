# RustOps Carbon Companion

Open-source Carbon production-channel companion. It opens only an outbound WSS connection and never exposes a new inbound port.

Current version: **0.7.2** · Protocol: **v1**

1. Copy `RustOpsCompanion.cs` to `carbon/plugins`.
2. In dashboard, generate pairing code.
3. Run `rustops.pair CODE wss://your-domain/v1/carbon/connect` once from server console. Private LAN testing may use `ws://`; public hosts require `wss://`.
4. Check `rustops.status`.

Commands:

- `rustops.pair CODE [URL]` — pair or rotate the device credential.
- `rustops.status` — pairing, connection, service, protocol, and capabilities.
- `rustops.retry` — immediately resume and force a connection attempt after a retry pause.
- `rustops.update` — immediately check for and install a newer verified release.
- `rustops.autoupdate true|false` — control automatic verified updates.
- `rustops.version` — installed companion version and build.
- `rustops.changelog` — changes included in installed releases.

Device token is stored in `carbon/configs/RustOpsCompanion.json`. Re-pairing rotates it. Plugin config access is constrained to JSON files inside Carbon configs directory, capped at 2 MiB, written atomically, and retains five backups.

Premium source-file management is constrained to top-level `.cs` files inside `carbon/plugins`, capped at 512 KiB. Replacements and deletions create server-local backups; five versions per plugin are retained for restore.

## Security

Updates are accepted only from the configured RustOps service origin and must match
the SHA-256 value in `release.json`. Pairing and management use an outbound WebSocket;
no inbound game-server port is added.

See [SECURITY.md](SECURITY.md) for private vulnerability reporting.
