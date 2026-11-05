# Hollow Ping Booster v0.0.2 self-host fork

## Configure

- Built-in server: **Singapore West**, endpoint `ping.hollow-agent.xyz:51820` (UDP, Cloudflare DNS-only).
- Pre-shared key: enter the existing VPS key in the Hollow VPS credential field. Never commit it or paste it into reports.
- Custom relay addresses are a separate optional setting. Leave them empty when using Hollow VPS;
  another operator can enter their own endpoint and key later.
- Licence server: empty. The hostname above is a selectable relay, not an HTTPS licence service.
- Both the UI **and** Windows service must run this source build. The installed vendor 0.5.7 service refuses self-host relays.

## Full tunnel (self-host default)

No game profile, CIDR list or running-game detection is needed. With an empty licence URL,
`fullTunnel` defaults to `true`. Connecting installs the two IPv4 split-default routes
`0.0.0.0/1` and `128.0.0.0/1` on the virtual adapter, after physically pinning the relay.
Windows' original default routes are not deleted or rewritten. More-specific local/LAN routes
remain local; a pre-existing route from another VPN can take precedence, so do not stack VPNs.

The relay carries IPv4, including TCP and UDP, not native IPv6. Full mode blocks outbound IPv6
while connected instead of silently bypassing the VPS; IPv6-only destinations will not work.
DNS policy sends ordinary Windows name resolution to an IPv4 resolver through the tunnel.
Existing policy owned by other software is not erased; managed NRPT rules may take precedence.
Applications using their own DNS/DoH still follow the IP routing table.

Normal disconnect, connection failure and reconnect teardown remove owned DNS policy and release
the IPv6 block. IPv4 Internet returns to the normal connection while reconnecting: **this is not
a kill switch**. It is a routing feature, not a claim of VPN-grade traffic confidentiality.
All web traffic, downloads and Windows updates use VPS bandwidth while connected. Switching
routes can interrupt existing connections, and latency can be worse on an unfavourable VPS path.

Set `"fullTunnel": false` in the service configuration only to retain legacy profile-based mode.
Existing configurations with a licence server retain their game/profile behaviour and are never
automatically pointed at the self-host endpoint. New configurations still require the VPS PSK;
no key is bundled. Do not edit the currently running installation to test this build.

Hollow Ping Booster is a separate installation: service/pipe `HollowPingBooster`,
mutex `Local\HollowPingBooster.SingleInstance`, data folders `%ProgramData%\HollowPingBooster`
and `%LOCALAPPDATA%\HollowPingBooster`, and adapter `Hollow Ping Booster`.
Assembly/executable filenames remain unchanged, so identify processes by their exact path,
not their image name. The Wintun driver is shared and is not removed by this fork's uninstaller.
Hollow modifications are Copyright (c) 2026 Hollow; the inherited MIT notice remains in `LICENSE`. Update checks
use a separate cache; installer assets are named
`HollowPingBooster-Setup-{version}.exe`.

## Source provenance and REA boundary

The public source base is **0.3.9**, commit
`9c4f4a67c4dd93476bc3c798385430cd1be9a0ee`. The standalone fork version is **v0.0.1**.
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

During full-tunnel development, an independent, authenticated protocol-v3 session also sent a
DNS A query for `example.com` to `1.1.1.1` as an inner IPv4 UDP packet through the VPS and received
a valid DNS answer. A TCP SYN to port 80 of one returned address then received a matching SYN-ACK
through that same session, followed by RST and protocol Disconnect. No adapter, route, DNS policy
or installed service was changed. This confirms real relay forwarding for DNS/UDP and TCP, not
the published Windows service or native full-tunnel lifecycle.

The final full-tunnel source passed a solution build (zero errors; three pre-existing nullable
warnings), ProtocolCheck, QualityCheck, PathCheck and all in-process TunnelCheck scenarios.
The latter includes fake-boundary partial-DNS rollback, WFP descriptor/ABI metadata, captured
session mode and opaque non-game packet transport. These checks do not install WFP filters,
write valid NRPT policy or create a Wintun adapter. A separately compiled, side-effect-free
C++ probe against the installed Windows SDK verified the x64 `FWPM_FILTER0` size (200 bytes)
and its trailing union/field offsets; the C# regression check uses those independent values.
To repeat the focused checks safely:

```powershell
dotnet run --project client/src/GamePingBooster.TunnelCheck -p:RollForward=Major -- full
```

UI and service were published separately as framework-dependent, non-AOT `win-x64` builds
with `RollForward=Major`; this local bundle uses the installed .NET 10 runtime. The original
shared-identity bundle was build-only. After the owner authorized opening a separate app,
the isolated build was installed under `C:\Program Files\Hollow Ping Booster` and started
with its own LocalSystem service and UI. The dedicated pipe reported configured self-host
full tunnel, relay `ping.hollow-agent.xyz:51820`, and state Disconnected. The exact UI window
showed "All games and apps". No tunnel connection was initiated.
A before/after comparison confirmed the vendor service PID/registration, executable/config
hashes, adapter DNS, NRPT and split-default route set were unchanged.

REA's retained managed-inspection records also establish that the installed
service and UI have no CLI directory or CIL available:

- Service: `ev_7b597e55b754309a767a66328658f36fb005bb8f334b5d73b5c3d770a8cfec24`.
- UI: `ev_d55e7f680a518b608ab6ffb5a6212c57c97ee7627d9411824e48e02415f43aec`.

These records support the inspection boundary, not a claim of recovered code.

## Separate installation; only one tunnel connected

The vendor installation is preserved. The fork uses a distinct installer AppId, directory,
service, pipe, UI mutex, config stores, adapter and DNS ownership prefixes. Its startup and
unblock cleanup do not delete vendor NRPT rules. Development stop commands skip unknown paths
and only target fork-owned directories. These identities permit both UIs/services to exist,
not concurrent tunnel activation: routes, DNS policy and the driver remain machine-wide.
Disconnect the old tunnel before connecting the new one.
The example profiles are templates for legacy per-game mode, not verified game-server lists.
They are not read or required by self-host full tunnel. Published startup is verified;
native routing/DNS/IPv6 connection and teardown remain untested on this machine.

## v0.0.1 distributable

Use `python installer/build.py --iscc "C:\path\ISCC.exe" --output-dir "C:\path\Builds"`.
This publishes fresh, self-contained, non-AOT Windows x64 UI/service outputs, includes the
runtime and official signed Wintun DLL, checks overlapping files before merging, and bundles
no PSK/config/device credentials. It never installs or launches on the build PC.

Installer diagnostics preserve the failing stage and error code, verify service registration
readback, and require disconnected full-tunnel IPC before reporting success. Existing Hollow
services are refused; vendor 0.5.7 can remain installed. Automatic in-place update is disabled;
update notifications open the release page. Remove only a previous Hollow installation first.
Clean-machine v0.0.1 install/uninstall and live full-tunnel routing remain **untested**.

## Local build

The targets are `net9.0-windows`. On the development machine with only a .NET 10
runtime, the explicit local compatibility option is:

```powershell
dotnet build client/GamePingBooster.sln -c Debug --nologo -v minimal -p:RollForward=Major
```

Publish the UI and service projects individually for deployment. Do not pass a
solution-level RuntimeIdentifier. Register only `HollowPingBooster` with a quoted executable
path under `C:\Program Files\Hollow Ping Booster`; never repoint the vendor service.
Protect the separate configuration with SYSTEM/Administrators-only ACLs and provision the
VPS PSK locally without copying vendor licence tokens or device identities. Administrator
approval is required. The local validation service uses manual start; no native tunnel is
activated merely by opening the UI.
