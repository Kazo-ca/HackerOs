> [Version française](../../../fr/wasm/dev/installable-server.md)

# Installable local server

`TODO` issue #267, [ADR 0191](adr/0191-installable-local-server.md). Series
`todo/base`, work item `todo/14-installable-server`.

## Purpose

The optional HackerOS server (`HackerOs.Server`: HTTP, TCP and UDP network
relay, accounts, devices, synchronization) installs on the user's computer as
a **service** that starts with the machine, with no .NET installed and no
command line to learn. The OS stays usable without it (GitHub Pages, PWA); it
gives the OS the real network and synchronization.

- By default the server only listens **on this computer**:
  `http://localhost:5888`.
- On request (`--lan` / `-Lan`), it also listens **on the local network over
  HTTPS**: `https://<address>:5889`, with a certificate signed by a local
  certificate authority specific to this installation.
- Nothing is exposed to the internet.
- A device links to the server with a **one-time pairing code** (no password
  to type).

## Download

Every published version (`release.yml`, [ADR 0173](adr/0173-automatic-releases-from-the-version-file.md))
carries one archive per system, named after the OS version:

| System | Archive |
|---|---|
| Windows x64 / ARM64 | `hackeros-server-<version>-win-x64.zip` / `win-arm64.zip` |
| macOS Apple Silicon / Intel | `hackeros-server-<version>-osx-arm64.tar.gz` / `osx-x64.tar.gz` |
| Linux x64 / ARM64 | `hackeros-server-<version>-linux-x64.tar.gz` / `linux-arm64.tar.gz` |
| Docker (amd64, arm64) | `ghcr.io/kazo-ca/hackeros-server:<version>` (and `latest`) |

In the OS: **Settings > Server connection > Local server** lists these
downloads for the running version, the Docker image, the releases page and
this guide.

Each archive is a self-contained single-file program that also carries the
published OS: the server serves HackerOS itself
([`server-hosting.md`](server-hosting.md)).

## Install

### Windows

Extract the archive, then in PowerShell **as administrator**:

```powershell
.\install.ps1                      # this computer only
.\install.ps1 -Lan 192.168.1.20    # also the local network over HTTPS
```

- Program in `C:\Program Files\HackerOS Server`, data in
  `C:\ProgramData\HackerOS Server`.
- Windows service `HackerOSServer` (automatic start), account `LocalService`.
- With `-Lan`: inbound firewall rule on the HTTPS port, **Private profile
  only**.
- Options: `-Port`, `-HttpsPort`, `-Account`, `-InstallDir`, `-DataDir`,
  `-Reconfigure`.
- The program is not signed: SmartScreen asks to confirm ("More info", "Run
  anyway").

### Linux

```bash
sudo ./install.sh                       # this computer only
sudo ./install.sh --lan 192.168.1.20    # also the local network over HTTPS
```

- Program in `/opt/hackeros-server`, data in `/var/lib/hackeros-server`.
- systemd unit `hackeros-server` (`Type=notify`), system user `hackeros`
  without a shell.

### macOS

Same command as Linux. Program in `/usr/local/hackeros-server`, data in
`/usr/local/var/hackeros-server`, launchd service `ca.kazo.hackeros-server`.
The script removes the download quarantine and signs the program ad hoc (no
Apple signature).

`install.sh` options: `--lan <names>` (comma-separated IP addresses or names),
`--port`, `--https-port`, `--account`, `--prefix`, `--data`, `--no-service`
(install and configure without a service), `--reconfigure`.

### Docker

```bash
docker run -d --name hackeros-server -p 127.0.0.1:5888:8080 \
  -v hackeros-data:/data ghcr.io/kazo-ca/hackeros-server
docker exec hackeros-server ./HackerOs.Server pair admin
```

The published port is bound to `127.0.0.1`: the server stays on this
computer. The image has no HTTPS; for the local network, put an HTTPS reverse
proxy in front and add its name to `HACKEROS_AllowedHosts` and
`HACKEROS_Cors__AllowedOrigins__*`.

### Upgrade and uninstall

Running the install script of a newer version replaces the program and
**keeps** `hackeros-server.json` and the data (`--reconfigure` /
`-Reconfigure` rewrites the configuration).

`sudo ./uninstall.sh` or `.\uninstall.ps1` removes the service and the
program; `--purge` / `-Purge` also deletes the data.

## Linking a device (pairing)

At install time, the script prints a code such as `H9R4-JYYG` for the `admin`
account (created if missing, with no usable password). In the OS:

1. **Settings > Server connection > Local server**.
2. Type the address (`http://localhost:5888` by default, or
   `https://192.168.1.20:5889` from another device) and press **Test**: the
   OS says whether a HackerOS server answers and its version.
3. Type the code and press **Pair this device**. The device is connected and
   a synchronization starts.

For another code (another device, an expired code), on the server's machine,
in the program folder:

```bash
sudo -u hackeros ./HackerOs.Server pair admin      # Linux
./HackerOs.Server pair admin                        # macOS (the user who installed)
```

```powershell
.\HackerOs.Server.exe pair admin                    # Windows, PowerShell as administrator
```

A code:

- is valid for **10 minutes** and works **once**;
- has 8 characters out of 30 (no `0`, `O`, `1`, `I`, `L`, `U`); case, spaces
  and the dash are ignored;
- is never written in clear: the server keeps one file per code, named by the
  code's SHA-256 hash, in `<data>/pairing`;
- after 10 wrong codes in one minute, every attempt is refused (`429`) until
  the minute ends.

The API: `POST /api/auth/pair` with `PairDeviceRequest(Code, DeviceName,
DeviceFingerprint)` answers the same `LoginResponse` as a password login, or
`401 INVALID_PAIRING_CODE`, or `429`. In the SDK:
`HackServerConnection.PairAsync` and `HackServerConnection.TestAsync`
(capability `server.connection.manage`, [`sdk-reference.md`](../framework/sdk-reference.md)).

## HTTPS on the local network

A browser does not call an `http://` server of the local network from an
`https://` page (mixed content). With `--lan`, the installation:

1. creates a local certificate authority (CA) specific to this machine;
2. signs with it a server certificate for the given names and addresses;
3. **deletes the CA's private key**: it can sign nothing else;
4. serves the CA certificate at
   `https://<address>:5889/api/hackeros/local-ca.crt` (and
   `http://localhost:5888/api/hackeros/local-ca.crt`).

Every other device must trust this certificate once:

| Device | How |
|---|---|
| Windows | Open the `.crt`, "Install Certificate", store **Trusted Root Certification Authorities**. |
| macOS | Open the `.crt` in Keychain Access, then **Always Trust** in its trust settings. |
| Linux | Copy into `/usr/local/share/ca-certificates/` then `sudo update-ca-certificates`; Firefox and Chrome may need an import in their own settings. |
| Android | Settings > Security > Encryption & credentials > Install a certificate > **CA certificate**. |
| iOS / iPadOS | Open the `.crt`, install the profile in Settings, then Settings > General > About > **Certificate Trust Settings**: enable full trust. |

The server's own computer uses `http://localhost:5888` and has nothing to
trust.

## Security

- **Loopback by default**: without `--lan`, only `localhost` listens.
- **`AllowedHosts`**: the installation limits it to `localhost`,
  `127.0.0.1`, `[::1]` and the names given with `--lan`. Any other `Host`
  header gets `400`, which blocks DNS rebinding (an internet page pointing its
  own name at `127.0.0.1`).
- **CORS**: only the GitHub Pages origin `https://kazo-ca.github.io` and the
  server's HTTPS local network addresses may call it from a browser.
- **The OS never probes `localhost` by itself** (ADR 0191 §3):
  `ServerPresenceService` only probes the origin that serves it, and the Local
  server panel calls the address only on **Test** or **Pair**. Otherwise
  Chrome would show its "Local Network Access" prompt to every visitor.
- The service runs under an unprivileged account (`LocalService`,
  `hackeros`); the data folder is `750`.
- Unsigned programs (no code signing certificate): the archive's `README.txt`
  says so.

## Configuration

`hackeros-server.json`, next to the program, is read **after**
`appsettings.json` and **before** the `HACKEROS_` environment variables:

```json
{
  "AllowedHosts": "localhost;127.0.0.1;[::1];192.168.1.20",
  "Kestrel": { "Endpoints": {
    "Local": { "Url": "http://localhost:5888" },
    "Lan": { "Url": "https://*:5889",
             "Certificate": { "Path": ".../certs/server.crt", "KeyPath": ".../certs/server.key" } }
  } },
  "Cors": { "AllowedOrigins": [ "https://kazo-ca.github.io", "https://192.168.1.20:5889" ] },
  "ConnectionStrings": { "HackerOsDb": "Data Source=.../hackeros.db" },
  "BlobStorage": { "Root": ".../blobs" },
  "ServerBackup": { "Root": ".../backups" },
  "HackerOs": {
    "LocalServer": { "CaCertificatePath": ".../certs/hackeros-local-ca.crt" },
    "Pairing": { "Root": ".../pairing" }
  }
}
```

On Windows the certificate is a `.pfx` (`Path` + `Password`). Started as a
Windows service, the server uses the program folder as its content root
(otherwise it would be `C:\Windows\System32`).

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| **Test**: the address does not answer | Service stopped (`systemctl status hackeros-server`, `Get-Service HackerOSServer`, `launchctl print system/ca.kazo.hackeros-server`), wrong port, or firewall. |
| `400 Bad Request` | The typed name is not in `AllowedHosts`: run the installation again with `--lan <that name> --reconfigure`. |
| Certificate error on another device | The local CA is not trusted on that device (see the table above), or the typed name is not in the certificate. |
| "Pairing failed: … 401" | Code expired, already used or mistyped: create another one with `pair`. |
| `429` | Too many wrong codes: wait a minute. |
| Port already in use | `--port` / `--https-port` (and `--reconfigure`). |

## Build and release

- `wasm2/HackerOs/build/publish-server.sh --rid <rid> --version <v> --output <folder>`
  publishes the server (self-contained, single file: `PublishSingleFile` is in
  the `.csproj`), removes the `.pdb`/`.xml` files and the Blazor debug proxy,
  adds the scripts of `Server/HackerOs.Server/packaging/` and produces the
  archive.
- `release.yml`: the `server` job publishes the 6 archives and attaches them
  to the GitHub release; the `server-image` job builds the multi-architecture
  image from the Linux publications and pushes it to `ghcr.io`
  (`:<version>`, and `:latest` outside prereleases).

## Key decisions

- **Codes in files, not in the database**: the `pair` command is a second
  process next to the service; files are enough, with no new table (the
  migration-free SQLite bootstrap could not add one to an existing database).
  Moving the file (`File.Move`) guarantees single use.
- **Download list without a highlighted system**: ADR 0191 §6 planned to
  highlight the detected system, but the public SDK has no user-agent API; the
  six archives are listed.
- No new ADR: these details stay within ADR 0191.

## Tests

- `Tests/HackerOs.Server.Tests/LocalServerTests.cs`: codes (single use,
  expiry, typed form, attempt limit), command line, and end to end: `pair`
  then `POST /api/auth/pair`, unknown `Host` refused, CA served, GitHub Pages
  CORS.
- `Tests/HackerOs.Platform.Core.Tests/Administration/LocalServerGatewayTests.cs`:
  pairing and test gateway, capability required.
- `Tests/HackerOs.Apps.Settings.Tests/LocalServerPanelTests.cs`: the panel
  (downloads, nothing called by itself, Test, Pair, error in French).
- Real run of `install.sh --no-service --lan ...` on Linux. The PowerShell
  scripts, macOS and the Docker image could not be tried in the development
  container.

## Task list

- [x] One-time pairing codes and `POST /api/auth/pair`.
- [x] `HackerOs.Server pair <account>` command.
- [x] Windows and systemd service, `hackeros-server.json`, content root.
- [x] Local CA, HTTPS certificate, `GET /api/hackeros/local-ca.crt`.
- [x] `AllowedHosts` and GitHub Pages CORS origin.
- [x] Install and uninstall scripts (Windows, Linux, macOS), `README.txt`.
- [x] `Dockerfile` and `build/publish-server.sh`.
- [x] `server` and `server-image` jobs in `release.yml`.
- [x] SDK: `HackServerConnection.PairAsync` and `TestAsync`.
- [x] Settings > Local server.
- [x] `index.html` served by the published server (see [`pitfalls.md`](pitfalls.md)).
- [ ] Real run on Windows, macOS and Docker.
