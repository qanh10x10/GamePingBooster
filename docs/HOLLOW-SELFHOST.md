# Hollow Ping Booster self-host fork

## Configure

- Relay: `ping.hollow-agent.xyz:51820` (UDP, Cloudflare DNS-only).
- Pre-shared key: the existing VPS key. Never commit it or paste it into reports.
- Licence server: empty. The hostname above is a relay, not an HTTPS licence service.
- Both the UI **and** Windows service must run this source build. The installed vendor 0.5.7 service refuses self-host relays.

Display branding is Hollow Ping Booster. Internal assembly names, service name
`GamePingBooster`, IPC pipe, driver/adapter identities, and ProgramData paths are
retained for compatibility. Original MIT attribution is retained. Update checks
use `qanh10x10/GamePingBooster` and a separate cache; installer assets are named
`HollowPingBooster-Setup-{version}.exe`.

## Source provenance and REA boundary

The public source base is **0.3.9**, commit
`9c4f4a67c4dd93476bc3c798385430cd1be9a0ee`.
The vendor's public `v0.5.7` tag is an unrelated installer-only README tree,
not newer application source. It must not be merged over this source tree.
This fork does **not** claim complete source recovery or parity with 0.5.7.
Hostname support and branding are new source changes, not recovered vendor code.

Read-only analysis of the installed 0.5.7 binaries used REA 6.5.0. The service
was opened with REA's Ghidra provider, but `binary_overview` timed out at 300 s.
The installed REA package's pure `parseNativeAotPe` parser was then invoked
directly on digest-checked file snapshots (not a successful Ghidra decompilation).
It found RTR **10.1** in all three executables and returned `partial` with
`unsupported-layout`: this parser supports RTR 9.1 only. No method tables or
frozen strings were recovered by that parser. Original C# was not recovered.

Installed binary SHA-256 identities:

- `GamePingBooster.exe`: `46965a95772081fd0a72db9d4496fa34c7d02a7aa6ec9b1534d601a68519a48e`
- `gpb-service.exe`: `28c34cd5f0810691a378aa36be0d9aa75dc6ed445daf629797c95c718de0cba6`
- `gpb-updater.exe`: `40605d3fa757116da2d2595c00c5cae12c743323efc60e406dd03f968553b35d`

## Verification boundary

A Windows-side, short-lived protocol probe resolved the configured hostname to
`213.35.117.26`, authenticated the relay's protocol-v3 PSK handshake using the
saved key, verified the response HMAC and nonce, received **5/5** matching pong
replies, and sent Disconnect. Median RTT in that sample was **59.0 ms**.
No route, driver, system DNS policy, service configuration, or stored key was
changed by that probe. This proves DNS/UDP/PSK reachability from this PC, not
that a rebuilt Windows service or game tunnel has been exercised.

A later sample on 2026-10-27 again authenticated successfully, received **5/5**
pongs, and measured **63.1 ms** median RTT. The final source build passed
ProtocolCheck, QualityCheck, PathCheck, and the in-process TunnelCheck scenarios.
TunnelCheck uses a fake adapter and relay; it is not a live game-routing test.

REA's retained managed-inspection records also establish that the installed
service and UI have no CLI directory or CIL available:

- Service: `ev_7b597e55b754309a767a66328658f36fb005bb8f334b5d73b5c3d770a8cfec24`.
- UI: `ev_d55e7f680a518b608ab6ffb5a6212c57c97ee7627d9411824e48e02415f43aec`.

These records support the inspection boundary, not a claim of recovered code.

## Test later; do not replace the running installation

At the owner's request, the local build is staged only: no UI launch, installer,
service replacement/restart, route change, or saved-configuration change is part
of this delivery. The original running installation is left alone.

The UI and service deliberately retain the same service, pipe and single-instance
identities. This is **not** a side-by-side installation: launching the new UI
while another version's service is running would attach to that service and does
not test the rebuilt service. Plan an explicit backed-up switch when ready.
The example profiles are templates, not verified game-server address lists;
full-tunnel routing is not implemented by this change.

## Local build

The targets are `net9.0-windows`. On the development machine with only a .NET 10
runtime, the explicit local compatibility option is:

```powershell
dotnet build client/GamePingBooster.sln -c Debug --nologo -v minimal -p:RollForward=Major
```

Publish the UI and service projects individually for deployment. Do not pass a
solution-level RuntimeIdentifier. Before switching the installed service,
back up the exact service registration and configuration, preserve the saved
PSK and device identity, and retain a rollback to the original executable.
Service replacement requires administrator approval. A newly launched UI
connected to the vendor service does not verify the source service.
