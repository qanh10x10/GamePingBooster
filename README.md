# Hollow Ping Booster v0.0.2

Self-host fork of Game Ping Booster. The main window offers **Singapore West**
(`ping.hollow-agent.xyz:51820`) as the built-in Hollow VPS server choice. Its pre-shared key is
entered separately; the optional custom-relay fields stay empty unless somebody runs another relay.
See [self-host setup and verification](docs/HOLLOW-SELFHOST.md).
The public source base is 0.3.9; this fork does not claim source parity with the closed 0.5.7 release.

Self-host mode defaults to **full tunnel**: IPv4 Internet traffic from all games and applications
uses the configured VPS, without game profiles or process detection. Windows DNS uses an IPv4
resolver through that tunnel; outbound IPv6 is blocked while connected because the relay protocol
only carries IPv4. Existing local/LAN routes and the physical route to the relay stay direct.
Downloads and updates also consume VPS bandwidth; a tunnel does not guarantee lower ping.

No injection, no reading game memory, no DLL hooks. A virtual network adapter and Windows routing
carry the packets. The original per-game mode is retained for licensed installations or an explicit
`"fullTunnel": false`; the profile-building sections below describe that legacy mode.

## Layout

```
relay/     Go         -> runs on a Linux VPS
client/    C# .NET 9  -> runs on the player's machine (Avalonia UI + Windows Service)
profiles/  JSON       -> game IP ranges, fetched at runtime
tools/     PowerShell -> build and validate profiles
docs/                 -> design documentation
```

## Where to start

| You want | Read |
|---|---|
| The system as a whole | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| The wire protocol | [docs/PROTOCOL.md](docs/PROTOCOL.md) |
| Deploying and operating the relay | [relay/README.md](relay/README.md) |
| The virtual adapter binary | [client/native/wintun/README.md](client/native/wintun/README.md) |

## How the game's addresses are found

Routing works on destination addresses, so the profile has to say which addresses belong to the
game - and it has to be narrow. AWS and Azure publish the enclosing blocks as `/17`s and `/18`s;
routing one of those would drag thousands of unrelated services through the relay.

`tools/profile-builder/` solves that in two commands:

```powershell
cd tools\profile-builder
.\Capture-GameTraffic.ps1     # leave running, play, Ctrl+C when done
.\Build-PubgProfile.ps1       # no arguments
```

The first watches for the game process, captures while it runs, attributes traffic to the process
by joining captured source ports against the Windows UDP socket table, and appends the servers it
saw. The second cross-checks every address against the official AWS and Azure range files, keeps
each one narrow, sorts it into the right region, writes `profiles/pubg-vn.json` and validates the
result.

Addresses that belong to no published cloud range are never added automatically; they are set
aside for review. In practice they turn out to be voice chat or a CDN.

## Hollow v0.0.2 Windows installer

Build the credential-free, self-contained installer (Python 3, .NET SDK, Inno Setup 6.7+):

```powershell
python installer/build.py --iscc "C:\path\ISCC.exe" --output-dir "C:\path\Builds"
```

The result is `HollowPingBooster-Setup-0.0.2.exe`. It includes the .NET runtime, never includes a PSK,
and does not install or launch itself on the build PC. The old `gpb.ps1 installer` packaging path
is disabled; it must not be used to distribute this fork.

Hollow uses SCM/IPC `HollowPingBooster`, directory `Program Files\Hollow Ping Booster` and data
`ProgramData\HollowPingBooster`; vendor 0.5.7 may remain installed. Close/uninstall only a previous
Hollow installation before installing; live upgrades are intentionally refused. In-app updates open
the release page rather than starting an in-place upgrade. Setup verifies
payload hashes, registers LocalSystem, and reads disconnected full-tunnel IPC before success.
A failed setup shows `ERROR [step]: cause` and its log path. Send `Setup Log*.txt` from the
installing user's `%TEMP%`, never config/PSK/token/device-key files. No claim is made that an
unknown remote installation failure is caused by a version collision or fixed without its log.

## Build

**Relay** (needs Go 1.22+, builds from Windows or Linux):

```bash
cd relay && make build && make test
```

**Client** (needs the .NET 9 SDK). Two builds, for two different jobs:

```bash
./gpb dev        # Debug build, then starts the service and the UI. The everyday loop.
./gpb publish    # Native AOT Release build, installed into %ProgramData%
```

`./gpb dev` is what you want while working. It stops anything already running, builds, and
starts the service through `psexec -s -i` - Wintun refuses to create an adapter for anything
below LocalSystem, and Administrator is not enough.

`./gpb publish` produces the real thing: a Native AOT executable that is machine code, needs no
.NET runtime on the user's PC, and starts immediately. It additionally needs **Visual Studio
Build Tools** with the "Desktop development with C++" workload.

Underneath it is just `dotnet publish`, and you can run that directly:

```bash
cd client && dotnet publish src/GamePingBooster.Service -c Release -r win-x64
cd client && dotnet publish src/GamePingBooster.App -c Release -r win-x64
```

...but `./gpb publish` also does three things that are easy to miss and whose failures name the
wrong cause:

- **Puts `vswhere.exe` on PATH first.** Native AOT needs the MSVC linker and finds it through
  `vswhere`, which is not on PATH by default. Without it the publish dies with
  `'vswhere.exe' is not recognized`, which says nothing about AOT.
- **Stops the service and the UI first.** The service holds `wintun.dll` and the UI holds
  `Core.dll`; a build that cannot overwrite them fails with a locked-file error that does not
  mention either.
- **Copies the result into `%ProgramData%\HollowPingBooster\bin`**, skipping `.pdb` files.

A registered service keeps running the old binary until it is restarted, which needs
Administrator:

```
sc.exe stop HollowPingBooster
sc.exe start HollowPingBooster
```

## Commands

Everything day to day goes through one entry point. `./gpb` is POSIX `sh` and runs on Linux,
macOS and Git Bash; `gpb.ps1` is the same set of verbs for PowerShell. `./gpb` hands the
Windows-only verbs to `gpb.ps1` when you are on Windows, and explains itself rather than failing
strangely when you are not.

Run them from `application/`. `./gpb` with no arguments prints this list.

### Relay - works on any operating system

| Command | What it does |
|---|---|
| `./gpb relay setup` | First-time setup, explained step by step. Start here. |
| `./gpb relay list` | The relays `gpb.conf` declares, how each one authenticates, and the endpoint a client would use. Use it to check what actually got parsed. |
| `./gpb relay build` | Cross-compiles `relayd` for Linux. Static, no cgo, so the VPS never needs Go. |
| `./gpb relay deploy [name]` | Builds, ships and installs in **one** ssh connection - the payload goes over as a tar stream on stdin. Omit the name to use `RELAY_DEFAULT`. |
| `./gpb relay logs [name]` | Follows `journalctl -u relayd -f` on that relay. |
| `./gpb relay test` | Go tests only. |

`./gpb relay` on its own prints that table.

Relays are declared in `gpb.conf` (copy `gpb.conf.example`; it is gitignored and no host in this
repository is real). A name that file does not declare is handed to `ssh` unchanged, so an alias
from your `~/.ssh/config` or a plain `root@203.0.113.10` works too.

#### What a relay block holds

One block per VPS. Only `_HOST` is required; every other field has the default shown.

| Key | Default | What it is |
|---|---|---|
| `RELAY_<NAME>_HOST` | - | Address or DNS name of the VPS. **Required.** |
| `RELAY_<NAME>_USER` | `root` | SSH user. |
| `RELAY_<NAME>_PORT` | `22` | SSH port. |
| `RELAY_<NAME>_KEY` | - | Private key file for SSH. `~/` is expanded, so one line works from PowerShell and from Git Bash. |
| `RELAY_<NAME>_PASSWORD` | `RELAY_PASSWORD` | SSH password, when there is no key. |
| `RELAY_<NAME>_SUDO_PASSWORD` | the SSH password | Only when they differ - key-based login plus a sudo password, usually. |
| `RELAY_<NAME>_LISTEN` | `51820` | UDP port relayd listens on. This is the port a **player** connects to, not the SSH one. |
| `RELAY_<NAME>_MAX` | `0` | How many clients this relay accepts **at once**. 0 means no limit beyond the address pool. |

`gpb.conf` is **parsed, not sourced**, on both sides, so a password may contain a space, `#`, `$`
or a backslash. `./gpb relay list` prints what actually got parsed, including the client cap -
worth a look after editing, because a value that failed to parse reads exactly like one that was
never set.

#### How many clients a relay accepts

`RELAY_<NAME>_MAX` becomes `-max-clients` on the relay, and a client arriving past it is answered
with "the relay is full" and fails over to another relay rather than being left hanging.

A count, rather than a smaller `-subnet`, on purpose. A prefix gives whatever the arithmetic
gives - a `/26` is 61 usable addresses, not 50 - and `-subnet` also has to agree with
`setup-nat.sh` and with the client, which assumes a `/24` when it configures the tunnel adapter.
Leaving the subnet alone and saying the number out loud avoids all of that.

Without it, the ceiling is the address pool: a `/24` leaves **253** clients at once, after the
network address, the relay's own `.1` and the broadcast.

#### Sharing one key between relays

Every relay a client might reach must hold the **same** pre-shared key, or failing over from one
to another is refused by the second. `install.sh` generates a key only when the machine has none,
so a redeploy never disturbs one; to put an existing key on a new relay, hand it a file:

```
sudo ./install.sh --psk-file /path/to/key
```

A file rather than a flag value, because a key on the command line is visible in `ps` to every
user on the box while the script runs. `GPB_PSK` also works, but **only** when `install.sh` runs
as root directly - `sudo` resets the environment, so `GPB_PSK=... sudo ./install.sh` loses it and
quietly generates a new key instead.

An account that is not root is fine: the payload unpacks into `~/.gpb-deploy`, and only the
install step needs privilege. It runs directly when the account is root, under `sudo` when sudo
is passwordless, and otherwise over a second connection that can carry a sudo password - the
first one cannot, because its stdin is the tarball and `sudo -S` reads its password from stdin.

### Client - Windows only

These drive a virtual adapter, the routing table, a Windows service and Npcap. They have no
meaning on another system and say so instead of failing oddly.

| Command | What it does |
|---|---|
| `./gpb dev` | The everyday loop: stop what is running, build, start the service as LocalSystem via `psexec`, start the UI. |
| `./gpb publish` | Native AOT Release build, installed into `%ProgramData%\HollowPingBooster\bin`. See [Build](#build). |
| `./gpb stop` | Stops the service and the UI. |
| `./gpb status` | Asks the running service for its live counters over the named pipe: state, packets, drops, tunnel ping, loss, active routes. |
| `./gpb logs` | Follows the service log in `%ProgramData%\HollowPingBooster\logs`. |
| `./gpb check` | **Run this during a match.** Answers whether the game's traffic is really going through the relay or straight out of the network card. Outside a match it has nothing to look at. |
| `./gpb lag [seconds]` | **Run this while it is lagging.** Pings every segment of the path on the same tick - your router, the ISP's access network, its domestic core, the relay, and a landmark on a different network in the same region - then names the one segment that is at fault. Reads the tunnel's own numbers from the service rather than opening a session of its own, so it is safe to run mid-match. Needs no Administrator. Each run writes a full report - trace, verdict and every raw sample - under `%LOCALAPPDATA%\HollowPingBooster\lag-reports`, plus a one-line history entry that becomes the baseline later runs are judged against. |
| `./gpb capture [udp\|tcp\|all]` | Waits for the game to start, captures its traffic, and appends the server addresses it sees to `observed.txt`. Safe to Ctrl+C. `udp` is the default and the only half that feeds the profile. `tcp` is for a lobby stuck on "Initializing...": it lists every TCP destination the game tried, marks the ones whose SYNs were never answered as **BLOCKED**, and writes that to `tcp-sessions.txt` - never to `observed.txt`. `all` does both. Close downloads while capturing TCP; they fill the capture. |
| `./gpb profile` | Turns what `capture` collected into `profiles/pubg-vn.json`. No arguments. |
| `./gpb diag` | Collects everything needed to diagnose a client-side problem into one text file, with the PSK redacted - including the three newest `./gpb lag` reports, so the bundle can answer whether the network was bad at the time. Attach it to a bug report. |
| `python installer/build.py --iscc <ISCC.exe> --output-dir <Builds>` | Self-contained Hollow Setup; never installs or launches it. Legacy `gpb installer` is disabled. |
| `./gpb version [x.y.z]` | Prints the version everything is stamped with, or sets it. |
| `./gpb reset` | Legacy destructive tooling: **do not use** for this fork. Use Hollow's own uninstaller and a disposable test VM. |

`capture` and `profile` are a pair and are how the profile grows: play, capture, rebuild. A
profile is only as good as the number of matches behind it.

### Version

`VERSION` is the fork's single version source. `client/Directory.Build.props` stamps
assemblies, and `installer/build.py` passes the same version to Inno Setup. Edit `VERSION`
before building, or use `./gpb version x.y.z`. The canonical installer accepts numeric
`x.y.z`; never reuse vendor version numbers to imply source parity.

### Releasing

```
./gpb release 0.1.8
```

That is the whole procedure. It refuses, before changing anything, when: you are not on `main`,
there are uncommitted changes, `main` is behind `origin`, the tag already exists, or the version
is not newer than the latest release. It then shows what it will do and asks once. After the tag
is pushed, `.github/workflows/release.yml` builds the installer and the relay package, and
creates the release with them attached.

**Never create or publish a release on the GitHub web page.** Releases on this repository are
immutable. A published release accepts no new assets, so one published by hand can never receive
the installer - and deleting it to try again makes its version number unusable forever. v0.1.7 was
lost that way. If a push fails part way, run the same `./gpb release` again: it recognises its own
release commit and tag and carries on from where it stopped.

### Testing the installer

Use a disposable Windows 10/11 x64 VM, not an active gaming machine. Test first without
vendor software, then with vendor 0.5.7 installed but its tunnel disconnected. Verify
Hollow service/IPC startup without a PSK, setup error reporting, and uninstall. The shared
Wintun driver and personal Hollow configuration must survive uninstall; vendor files,
service and configuration must remain unchanged.

Do **not** run the inherited `gpb reset` / `tools/Reset-Machine.ps1`: it is legacy tooling,
not an isolation-safe cleanup path for this fork. Use only Hollow's own uninstaller.

### Both halves

| Command | What it does |
|---|---|
| `./gpb test` | Everything: `gofmt`, `go vet`, the Go tests, the lag-verdict rules, then the C# build and the cross-language wire-format check. Skips the C# half with a warning if the .NET SDK is missing. |
| `./gpb release x.y.z` | **The only way to release.** Checks everything first, shows the plan and asks once, then bumps `VERSION`, commits, pushes `main` and the tag; GitHub Actions builds and publishes the release. See [Releasing](#releasing). |
| `./gpb help` | The same list, from the script itself. |

`./gpb test` always passes `-count=1`. Go's test cache is not keyed on
`testdata/protocol-vectors.json`, so without it a tampered vector file is reported as a cached
pass - which is the exact failure that file exists to catch. The whole suite takes about six
seconds from cold.

## Running it

1. To use the built-in **Singapore West** server, keep the custom relay list empty. To run another relay,
   declare it in `gpb.conf` (copy `gpb.conf.example`; it is gitignored), then
   `./gpb relay deploy`. See [relay/README.md](relay/README.md). Keep that endpoint and PSK
   separate from the built-in Hollow VPS choice.
2. Download the signed `wintun.dll` into `client/native/wintun/` - see
   [client/native/wintun/README.md](client/native/wintun/README.md).
3. Start Hollow Ping Booster. In the main window choose **Singapore West** and enter its PSK in
   Settings. If using another relay, leave Singapore West unselected, enter the custom endpoint(s)
   and custom PSK in the separate custom-relay fields. The installer never bundles either key.

   **Two files, and the split matters.** `config.json` is *this machine's settings* - the key,
   which relay to use, the adapter name. The profile is *content* - the game's IP ranges, and
   the relays the vendor offers. Keep your own relay out of the profile: a profile is replaced
   wholesale every time it is fetched from a server, so anything of yours written into it
   disappears silently on the next update.

   The built-in Hollow VPS and custom relays are separate trust domains:

   | Setting | Means |
   |---|---|
   | Main-window server choice | Choose `Singapore West` (the built-in Hollow VPS) or a configured custom relay. |
   | `defaultRelayId` | Use one of the relays the profile lists, by id, in licensed/profile mode. |
   | `relayEndpoints` | Optional custom relay addresses. These never contain the built-in Hollow VPS endpoint and replace the profile's list when selected. |

   `relayEndpoints` is a list, and giving it more than one is worth doing: the client measures
   every relay it knows before connecting and takes the fastest, then falls back to the others
   if that one stops answering. One address turns both of those off.

   `profilePath` is relative to the install directory and should stay that way; an absolute path
   only works on the machine it was written on. It is not in Settings on purpose - it is
   something the installer knows, not something a user should have to.
4. Build and start both halves:
   ```
   ./gpb dev
   ```
   The service has to run **as LocalSystem** - Wintun requires it and Administrator does not
   satisfy it - which is why this goes through `psexec` rather than just launching the exe. By
   hand, if you would rather:
   ```
   psexec -accepteula -s -i <path>\gpb-service.exe --console
   ```
5. Press Connect in `GamePingBooster.exe`.

`./gpb status` reads the tunnel's live counters, `./gpb logs` follows the service log, and
`./gpb check` answers whether the game is actually going through the relay - run that one during
a match.

Things that go wrong first, in order of likelihood:

- `WintunCreateAdapter` returns error 5 - the process is not actually running as LocalSystem.
  Check with `whoami`; it must print `nt authority\system`.
- The handshake never completes - the provider's cloud firewall is blocking the UDP port.
- Packets reach the relay but nothing comes back - `net.ipv4.ip_forward` is off, or `rp_filter`
  is still in strict mode.
- Small packets work but large ones hang - MTU or MSS clamping.

## Historical upstream status (not v0.0.1 acceptance evidence)

The claims below describe upstream testing. For this fork, source/payload checks do not establish
a successful clean-machine installation or live routing. See [verification boundaries](docs/HOLLOW-SELFHOST.md#verification-boundary).

**The tunnel works end to end on real hardware** (verified 2026-08-30). Traffic goes from
Windows, through the Wintun adapter, through the relay on a VPS, out to the internet and back -
confirmed with both ICMP and TCP, the latter proving MSS clamping and stateful NAT on the return
path are correct.

It has since been played on. A three-hour PUBG session over the relay took the in-game ping
from **80 ms to 43 ms** and held it there (2026-09-02), and the installer has been run on a
machine that had never had the software, configured from the settings screen, and used for a
real match (2026-09-03).

Not yet done:

- **The profile is not saturated.** Roughly eight matches in ten land on a covered server. That
  only improves by playing and capturing; see `./gpb capture`.
- **The installer is not code signed.** SmartScreen warns everybody who runs it, and many will
  stop there. This needs a certificate that has to be bought, and it is the last thing between
  the build and somebody who is not the author.
- **The tunnel authenticates but does not encrypt.** See the security section of
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for what that costs and when it has to change.

## License

MIT - see [LICENSE](LICENSE). Hollow Ping Booster and its modifications are Copyright (c) 2026 Hollow. The inherited MIT notice remains in `LICENSE`.
