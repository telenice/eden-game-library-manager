# Eden Game Library Manager

A desktop companion for the Eden Nintendo Switch emulator. It connects to a self-hosted
[AeroFoil](https://github.com/luketanti/AeroFoil) server over HTTPS, or to the files stored in your
own [TorBox](https://torbox.app) account, shows the games you don't have yet, and downloads the ones
you pick into a local folder. Eden scans that folder on its own; this app never launches or changes
Eden.

- **Platforms:** Windows 10/11 x64, macOS 12+ (Apple silicon and Intel), Linux x64/arm64
- **Stack:** C# / .NET 10, Avalonia UI 11.3 (Fluent, MVVM), SQLite, `HttpClient`
- **Distribution:** one self-contained executable per platform. It needs no .NET install and no
  administrator rights, and doesn't use SMB or mapped drives.

> The app downloads files from a server you or your group runs. It doesn't include or distribute
> games, keys or firmware. You're responsible for having the rights to anything you download.

---

## Features

| Area | What you get |
|---|---|
| **Available Games** | Artwork grid or list, search, sort (title, size, recently added) and Base/Update/DLC filters. Games you already have are hidden; tick *Show downloaded* to see everything. A details panel shows metadata, description, screenshots, and the title's base game, updates and DLC, each with its own download button. |
| **Updates & DLC with the game** | Downloading a base game also queues its latest update and every DLC on the server (Settings → Downloads; on by default). The per-item buttons in the details panel download a single file. |
| **Folder per game** | Files are saved as `<games folder>\<Game name> [TitleID]\…`, with the base game, update and DLC together (Settings → Local games directory; on by default). **Organise existing downloads** moves files this app downloaded earlier into that layout. Files you added yourself are never moved. |
| **Group by game** | One card per title with its updates and DLC. An **Update available** / **New DLC** badge appears when you have the base game but newer content is on the server, and **Download N** fetches everything that's missing. |
| **Server profiles** | Save several AeroFoil servers and TorBox accounts and switch between them from the sidebar. Each profile has its own credentials, offline catalogue cache and download queue. The games folder and storage limit are shared. |
| **TorBox** | Lists the `.nsp`/`.nsz`/`.xci`/`.xcz` files in your finished TorBox downloads and fetches them straight from TorBox, so your home upload speed doesn't matter. See [TorBox profiles](#torbox-profiles). |
| **Game info from titledb** | Names, descriptions, icons and screenshots by title ID from [titledb](https://github.com/blawar/titledb), without a server. Required for TorBox; fills gaps for AeroFoil. See [Game info and artwork](#game-info-and-artwork-titledb). |
| **Downloaded Games** | Every game file in your games folder, nested folders included, with updates and DLC listed under their base game. Shows size, path, download date, total playtime from Eden, pinned status, **Open Folder** and **Delete** (after confirmation). Supports multi-select delete and storage figures. |
| **Downloads** | A persistent queue with progress, speed, ETA and status (Queued, Downloading, Paused, Verifying, Completed, Cancelled, Failed). Pause, resume, cancel and retry each transfer. |
| **Settings** | Server URL and credentials with a connection test, games folder, maximum storage for downloads, concurrency (1–4), speed limit, auto-resume, startup and periodic refresh, and theme. |
| **Reliability** | HTTP Range resume checked against ETag / Last-Modified, bounded retries with exponential backoff, size verification, partial files kept outside the games folder, and a database that rebuilds itself if corrupted. |
| **Storage** | Optional maximum storage for downloads, a cleanup picker when a download won't fit, and pinned games. Nothing is deleted without your confirmation. |
| **Updates** | Checks GitHub for a newer release at startup (Settings → Updates). On Windows it downloads the new version, checks its signature, replaces its own `.exe` and restarts. See [Updates](#updates). |
| **Offline** | Shows the last catalogue it saved, clearly marked. You can still browse, delete and check storage while offline; new downloads are disabled. |

---

## Installing

### Windows
1. Download `EdenGameLibraryManager-<version>-win-x64.zip` and extract it anywhere, e.g.
   `%LOCALAPPDATA%\Programs\EdenGameLibraryManager`.
2. Run `EdenGameLibraryManager.exe`. Windows SmartScreen may warn you about an unsigned app; choose
   *More info → Run anyway*. To remove that warning, sign the executable with your own certificate
   (see [Release builds](#release-builds)).

### macOS
1. Download `EdenGameLibraryManager-<version>-osx-arm64.zip` (Apple silicon) or `-osx-x64.zip`
   (Intel), extract it and move **Eden Game Library Manager.app** to *Applications*.
2. The bundle is only ad-hoc signed, so the first time you open it, right-click it and choose
   **Open**. If macOS says the app is damaged, run this once:

   ```bash
   xattr -dr com.apple.quarantine "/Applications/Eden Game Library Manager.app"
   ```

### Linux
1. Extract `EdenGameLibraryManager-<version>-linux-x64.tar.gz`.
2. Run `./install.sh` to install for your user only (it copies to `~/.local/bin` and adds a menu
   entry), or run `./EdenGameLibraryManager` directly.
3. To remember your password, install the Secret Service CLI, e.g. `sudo apt install libsecret-tools`
   (Debian/Ubuntu) or `sudo dnf install libsecret` (Fedora). Without it, the app asks for your
   password each session. It never stores it in plaintext.

---

## First run

A four-step wizard opens on first launch:

1. **Connect to AeroFoil or TorBox.** For AeroFoil, enter the server URL
   (`https://aerofoil.example.com`), username and password. For TorBox, choose **TorBox account**
   and paste your API key. Then click **Test Connection**. The test confirms that sign-in and
   catalogue access work and tells you how many items it found. You can't continue until the test
   passes.
2. **Choose the games directory**, e.g. `D:\Switch Games` or `~/Games/Switch`. Add the same folder
   to Eden's game directories so new downloads show up automatically. Each game gets its own
   subfolder, so in Eden's game list right-click that directory and enable **Scan Subfolders**.
   Depending on your Eden build, updates and DLC may also need **File → Install Files to NAND**.
   The app downloads them but never installs anything into Eden.
3. **Configure storage.** Set the maximum storage for downloads (0 means unlimited).
4. **Finish.** The app opens on **Available Games**.

You can change all of this later in **Settings**.

### More than one server

In **Settings → Server profiles**, click **Add server**, choose **AeroFoil server** or **TorBox
account**, fill in its details, test it and **Save settings**. The **Server** box at the top of the
sidebar switches the active profile.

- Each profile's password is stored separately in the OS secret store.
- Each server keeps its own cached catalogue, so switching while offline still shows that server's
  last catalogue.
- Downloads belong to the server they were queued from. If you switch profiles mid-transfer, the
  transfer waits ("belongs to another server profile") and resumes when you switch back.
- Games already in your folder count as downloaded on every server: matching is by content, not
  by server.
- Settings from version 1.0.0 (single server) are migrated automatically into a profile called
  *Default*, including the saved password.

### TorBox profiles

A TorBox profile uses the files in your own TorBox account (a paid plan is required) instead of an
AeroFoil server. Get your API key from **torbox.app → Settings**.

- **Catalogue.** The app reads `GET /v1/api/{torrents,usenet,webdl}/mylist` and lists every
  `.nsp`, `.nsz`, `.xci` and `.xcz` file in downloads TorBox has finished storing
  (`download_present`). Files still downloading on TorBox appear once they're done.
- **Identity comes from the file name.** TorBox only knows file names, so the title ID, version and
  type (base, update or DLC) are read from `[0100…]` and `[v…]` tags, e.g.
  `Game [0100ABCD12340000][v0].nsp`. Files without a `[TitleID]` tag are still listed and
  downloadable, but get no artwork or grouping.
- **Downloads go straight to TorBox.** For each attempt the app asks
  `GET /v1/api/{kind}/requestdl` for a fresh direct link and downloads from TorBox's servers
  (`*.tb-cdn.st`). Your own connection only matters for your download speed. TorBox's servers
  honour `Range` with a strong `ETag`, so pause, resume, retries, size verification and the speed
  limit work as with AeroFoil.
- **Pacing.** Download-link requests are spaced at least 1 second apart, across all downloads, so a
  queue of many small files (a game's DLC) doesn't flood TorBox. If TorBox still answers 429 or 400,
  the download waits 10 seconds and retries with a fresh link, up to 6 times before it fails.
- **The API key stays with TorBox.** It's sent as a Bearer header to `mylist` and as the `token`
  query parameter that `requestdl` requires. TorBox's download links also carry the key as
  `?token=`. The app uses whichever host TorBox's API returns (TorBox doesn't publish a fixed list),
  but only over HTTPS, and it refuses redirects to any other host, so the link can't be passed on.
  Links are held in memory for one attempt and are never logged or saved. The key is stored like a
  password (DPAPI, Keychain or Secret Service).
- **Getting files into TorBox is up to you.** TorBox stores files from torrents, Usenet and web
  links, so adding your own files still uses your upload once (for example, seed a torrent you
  created, or add a web download pointing at your own server). After that, every download comes
  from TorBox. Check TorBox's retention rules before deleting your only copy.

This app doesn't use [torbox-tinfoil-server](https://github.com/TorBox-App/torbox-tinfoil-server).
That server streams each file through the machine it runs on, which wouldn't help with a slow home
upload, and it only answers requests that look like they come from a Switch.

### Game info and artwork (titledb)

titledb is a public, regularly updated list of eShop details by title ID. With **Settings → Game
info and artwork** turned on (the default), the app:

- downloads the chosen region file once, e.g. `US.en.json` (about 90 MB of JSON, about 26 MB
  compressed), from `raw.githubusercontent.com/blawar/titledb`, without credentials;
- reads it entry by entry into a local SQLite copy, `titledb.db` (about 55 MB for `US.en`), keeping
  only name, publisher, description, icon, screenshots, release date and category;
- checks for changes weekly with `If-None-Match`, so an unchanged file isn't downloaded again
  (**Update now** forces a check);
- loads icons and screenshots from Nintendo's image server (`img-eshop.cdn.nintendo.net`), the only
  host titledb artwork is accepted from, again without credentials.

TorBox profiles take their names, icons, release dates and categories from titledb: base games and
DLC use their own entries, and updates are shown as "*Game* (Update)". AeroFoil profiles keep
everything their server sends and only gain missing fields; descriptions and screenshots come from
titledb when AeroFoil's `title-info` has none. Some titles in titledb have missing descriptions or
broken image links; those are shown without them.

---

## AeroFoil API: what was verified

Before writing any code, I checked the endpoints against the current AeroFoil source
(`app/app.py`, commit `684e2bd`, 31 Oct 2026) and the
[CyberFoil remote protocol notes](https://cyberfoil.foo/shop.html).

| Purpose | Endpoint used | Notes |
|---|---|---|
| Catalogue | `GET /api/remote/sections`, then `GET /api/shop/sections` | CyberFoil documents `/api/remote/sections` as preferred and falls back to `/api/shop/sections` on 404. **Current AeroFoil only implements `/api/shop/sections`**, so in practice the app gets a 404 and falls back. |
| Metadata | `GET /api/shop/title-info/{title_id}` | Returns `description` and screenshot URLs. A 403 response means the title is restricted for your account. |
| Artwork | the item's `icon_url` (normally `/api/shop/icon/{title_id}`) | Used exactly as given. Fetched only from the configured server and cached on disk. |
| Download | the item's `url` (normally `/api/get_game/{file_id}#{filename}`) | Resolved against the server URL. Nothing is hardcoded. |

Behaviour worth knowing:

- **Authentication failures return HTTP 200.** AeroFoil's `tinfoil_access` decorator answers a
  failed sign-in with `200 {"error": "Incorrect password for user …"}`, including on
  `/api/get_game`. The app recognises these bodies so an error message is never saved as a game
  file. Messages are mapped to invalid credentials, no shop access, frozen account or rate limited.
  Credential failures are never retried, because AeroFoil locks out an IP after repeated failures.
- **Catalogue completeness.** The `all` section contains every owned base game, the latest update
  per title and the latest version of each DLC. For non-CyberFoil clients, AeroFoil caps `all` only
  when the server sets `SHOP_SECTIONS_ALL_ITEMS_CAP`. In that case the response includes
  `"truncated": true` and a `total`, and the app asks again with the `limit` query parameter that
  `shop_sections_api` reads. If completeness still can't be confirmed (truncated, or no `all`
  section), the app shows a warning. Sections overlap, so items are de-duplicated by `file_id`.
- **Updates.** AeroFoil lists only the latest update per title, so older update versions never
  appear.
- **Range / resume.** Files are served with Werkzeug `send_from_directory(conditional=True)`, which
  supports `Range`, `If-Range`, `ETag` and `Last-Modified`. The app resumes only after receiving
  `206` with a matching `Content-Range`, and restarts cleanly otherwise. CyberFoil's on-the-fly
  compressed stream is only offered to CyberFoil user agents. This app identifies itself honestly
  (`User-Agent: EdenGameLibraryManager/<version>`), so it always receives the original file.

### Server-side settings you may need

AeroFoil has two shop settings (**Settings → Shop**) that block third-party clients. The app shows
a clear message in both cases and does **not** pretend to be Tinfoil or CyberFoil:

| AeroFoil setting | Effect on this app | What to do |
|---|---|---|
| **External access: Tinfoil/CyberFoil only** (`external_tinfoil_only`) | Requests from public IPs to `/api/shop/sections` get `403 "External access is restricted to Tinfoil/CyberFoil clients."` LAN/VPN access still works. | Turn it off, or connect over LAN/VPN. |
| **Encrypt shop** (`encrypt`) | The catalogue is returned as Tinfoil-encrypted binary, which only Tinfoil can decrypt. | Turn it off. |

For friends outside your network without exposing your home IP, see
[docs/vps-relay](docs/vps-relay/README.md). It sets up a small cloud VPS that forwards only this
app's endpoints to your home AeroFoil over a single WireGuard link, with no port forward at home.

The shop must also be reachable over HTTPS with a valid certificate, e.g. behind a reverse proxy
with Let's Encrypt. Plain HTTP is accepted only if you tick **Allow insecure HTTP** for a trusted
LAN or VPN address.

---

## How "already downloaded" is decided

A catalogue item is hidden from Available Games only when the app is confident it's the same
content:

1. **Downloaded by this app:** same application ID, content type and version, with the file size
   unchanged since download.
2. **Same file:** identical file name (current or original) **and** identical size.
3. **Added by you manually:** the file name contains `[TitleID]` and `[vVersion]` tokens that match,
   or contains the TitleID and the size matches exactly.

Anything weaker, such as the same TitleID with a different size, is shown as **"Similar file"**
and stays available.

For grouping, the base game is worked out from the application ID using the Switch ID scheme
(update = base `…800`, DLC = base + `0x1000` + n). This stays correct even if a server's `title_id`
field is missing or wrong. `title_id` is used only when there is no application ID. Updates (`…800`) and DLC have different application IDs from their base
game, so owning an update or DLC never hides the base game. Different versions are always treated
as different items.

The filesystem is the source of truth. The folder is rescanned at startup, on refresh, and
whenever a filesystem watcher sees a change (debounced). Files deleted outside the app become
available again; new files are recorded as *Added manually*. If the games folder is missing, for
example on an unplugged drive, the database is left untouched.

---

## Storage

- **Maximum storage for downloads** applies to games this app downloaded, plus space reserved for
  queued, paused and active transfers. Manually added files are shown separately and don't count.
- Before each download, the app checks free disk space in the partial-download folder and, if
  different, in the games folder.
- If a download would go over the limit, a picker opens with the least recently used unpinned games
  preselected. It shows how much space will be recovered and deletes only what you confirm. Nothing
  is ever deleted automatically.
- *Last used* is the later of the download date and the last time you opened the file's folder
  from the app.
- **Total playtime** on Downloaded Games comes from Eden's own play-time file
  (`%APPDATA%\eden\play_time\playtime.bin` on Windows, `~/Library/Application Support/eden/…` on
  macOS, `~/.local/share/eden/…` on Linux), re-read whenever you open the page. It's shown once per
  game, on the base game's row. The file is only read, never changed. If Eden keeps its data
  somewhere else (a portable install), no playtime is shown.
- Deleting only ever removes a game file (`.nsp`, `.nsz`, `.xci`, `.xcz`) inside the games folder.
  Each deletion checks the path and refuses to follow symlinks or junctions. Eden saves, config,
  firmware and keys are never touched.

---

## Updates

With **Settings → Updates → Check for a new version when the app starts** on (the default), the app
asks GitHub's public API for the latest release of `telenice/eden-game-library-manager`. Drafts and
pre-releases are ignored. When a newer version exists, a banner offers **Update now**, **What's new**
and **Later**; **Check now** in Settings does the same on demand.

On Windows, **Update now**:

1. downloads the `-win-x64.zip` (or `-win-arm64.zip`) for this machine and its `.sig` file;
2. checks the signature against the release public key built into the app
   (`src/EdenGameLibraryManager.Infrastructure/Updates/update-signing-public-key.pem`), and stops if
   it doesn't match, so a replaced file on GitHub can't be installed;
3. takes only `EdenGameLibraryManager.exe` from the zip and stages it in the app data folder
   (`Updates\`). The running exe is never changed while it runs: a single-file app reads parts of
   itself from its own file on demand;
4. closes the app the normal way (active downloads pause) and starts the staged exe with
   `--finish-update`. It waits for the old process to exit, renames the old exe to
   `EdenGameLibraryManager.exe.old`, copies itself into place and starts from there. If that fails,
   the old exe is put back and started instead. Nothing else in the folder is touched, so keeping the
   app next to Eden's own files is safe;
5. on its next start the app deletes the `.old` file and the staged copy, and resumes downloads.
   The finishing step writes to `Logs\update.log` in the app data folder.

Settings, passwords, the library database and caches live in the app data folder, so updates keep
them. If any step fails, the current version keeps running unchanged. Files the app downloads itself
don't get Windows' "Mark of the Web", so an updated app starts without a SmartScreen prompt.

The app sends you to the release page instead when it can't update itself: on macOS and Linux, when
its folder isn't writable (e.g. Program Files), when a release has no signature, and in development
builds.

---

## Security

- HTTPS with normal certificate validation. Validation is never disabled; a bad certificate gives
  an explicit error.
- Credentials are attached per request and **only** to the configured origin. Redirects are
  followed manually, only within that origin, so credentials can't leak to another host.
- Catalogue URLs (downloads, icons, screenshots) must resolve to the configured origin. Anything
  else is rejected. Two exceptions: titledb artwork, accepted only over HTTPS from
  `img-eshop.cdn.nintendo.net` and fetched without credentials, and TorBox download links, which
  come from TorBox's API, must be HTTPS (they embed the API key) and follow redirects only within
  the link's own origin.
- Server-supplied file names are sanitised: path separators and traversal are rejected, Windows
  reserved names are neutralised, and only game extensions are allowed. Existing files are never
  overwritten; a ` (1)` suffix is added instead.
- Passwords and TorBox API keys are stored with DPAPI (Windows), Keychain (macOS) or the Secret
  Service via `secret-tool` (Linux), or not at all. They never appear in JSON files or logs.
- Downloaded files are never executed. *Open Folder* only reveals the file in Explorer, Finder or
  your Linux file manager.

---

## Files and folders

| What | Windows | macOS | Linux |
|---|---|---|---|
| App data root | `%LOCALAPPDATA%\EdenGameLibraryManager` | `~/Library/Application Support/EdenGameLibraryManager` | `~/.local/share/EdenGameLibraryManager` |

Inside the root:

- `settings.json`: preferences and server profiles, never passwords (see `docs/example-settings.json`)
- `library.db`: SQLite manifest of local games and unfinished downloads
- `Downloads/*.partial`: in-progress transfers, never inside the games folder
- `Artwork/`: cached icons and screenshots
- `Catalogs/*.json`: last catalogue of each server, used offline
- `titledb.db`: local copy of titledb, rebuilt when it changes
- `Logs/app-YYYYMMDD.log`: logs, kept for 14 days
- `credentials-<profile>.dat`: Windows only, one DPAPI-encrypted password or API key per profile (macOS and
  Linux use the Keychain / Secret Service instead)

If the database becomes corrupted, it's renamed to `library.db.corrupt-<timestamp>` and rebuilt
from a rescan. Game files aren't affected.

---

## Building from source

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build EdenGameLibraryManager.sln
```

```bash
dotnet test EdenGameLibraryManager.sln
```

```bash
dotnet run --project src/EdenGameLibraryManager.App
```

### Tests

The unit tests use mocked HTTP handlers and temporary directories. They don't need a real server.
They cover catalogue parsing, the endpoint fallback, AeroFoil error payloads, client-restriction
and encryption detection, de-duplication, missing metadata, local/remote matching (versions,
updates, DLC, manual files), Range resume, changed remote files, bad `Content-Range`, incomplete
transfers, retries, cancel/pause/restore, storage limits, pinned protection, unsafe URLs and
paths, offline startup, database recovery, reading Eden's play-time file, the TorBox catalogue and download links (including
refusing redirects to another host), streaming titledb parsing and ETag revalidation, and titledb
enrichment.

Optional **integration tests** run against a real server when these variables are set:

```bash
EGL_TEST_SERVER=https://aerofoil.example.com EGL_TEST_USER=alice EGL_TEST_PASSWORD=secret EGL_TEST_DOWNLOAD=1 dotnet test
```

`EGL_TEST_DOWNLOAD=1` additionally downloads the smallest item into a temp folder, interrupting
and resuming it partway, then deletes it.

### Release builds

#### GitHub Actions (all platforms)

`.github/workflows/release.yml` builds every package on native runners: Windows ZIPs, signed macOS
`.dmg`s and Linux AppImages / tarballs, each with a `.sha256` checksum. Windows ZIPs also get a
`.sig` for the in-app updater.

**One-time setup:** add the release signing private key (an ECDSA P-256 key in PEM format) as a
repository secret named `UPDATE_SIGNING_KEY` (**Settings → Secrets and variables → Actions → New
repository secret**). The release job signs each Windows zip with it, checks the signature against
the public key in the repository, and fails if the secret is missing or doesn't match. Keep a copy of
the private key somewhere safe: without it, future releases can't be signed for the updater.

- **Push a version tag** to run the tests, build everything, and publish a GitHub Release with the
  files attached:

  ```bash
  git tag v1.0.0
  ```

  ```bash
  git push origin v1.0.0
  ```

  Tags with a suffix (e.g. `v1.1.0-beta1`) are marked as pre-releases.
- **Run it manually** from the repository's **Actions → Release → Run workflow** to get the
  packages as downloadable workflow artifacts without creating a release.

macOS and Linux packages should come from the workflow (or be built on macOS/Linux). Windows can't
set Unix execute permissions, and Apple silicon only runs signed apps.

#### Windows: `build\publish.ps1` (or double-click `build\publish.cmd`)

The script runs the unit tests, publishes a self-contained single-file `EdenGameLibraryManager.exe`,
optionally signs it, and writes `artifacts\EdenGameLibraryManager-<version>-<rid>.zip` plus a
`.zip.sha256` checksum. It works in Windows PowerShell 5.1 and PowerShell 7.

```bash
build\publish.cmd
```

```bash
powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Runtime win-x64,win-arm64
```

```bash
powershell -ExecutionPolicy Bypass -File build\publish.ps1 -CertificateThumbprint <SHA1-thumbprint>
```

| Option | Meaning |
|---|---|
| `-Runtime` | `win-x64` (default) and/or `win-arm64`, comma-separated |
| `-SkipTests` | Publish without running the tests first |
| `-CertificateThumbprint` | Sign with this certificate from your store using `signtool` (Windows SDK), timestamped via `-TimestampUrl` |
| `-Configuration` | Build configuration (default `Release`) |

#### Any platform: `build/publish.sh`

Publishes a self-contained single-file build and packages it. It runs in bash on Windows (Git
Bash), macOS and Linux.

```bash
build/publish.sh win-x64
```

```bash
build/publish.sh osx-arm64
```

```bash
build/publish.sh linux-x64
```

Other supported targets are `osx-x64` and `linux-arm64`. Packages are written to `artifacts/`.

Without the script, the equivalent command (e.g. in `cmd.exe`) is:

```bash
dotnet publish src/EdenGameLibraryManager.App -c Release -r win-x64 -o publish/win-x64
```

The App project turns on `SelfContained`, `PublishSingleFile` and native-library self-extraction
whenever a runtime identifier is given.

- **macOS:** run the script on a Mac to get the `.icns` icon and an ad-hoc signature. For
  distribution outside your group, sign with a Developer ID and notarize (`xcrun notarytool`).
- **Windows:** to avoid SmartScreen warnings, sign with
  `signtool sign /fd SHA256 /a artifacts\win-x64\publish\EdenGameLibraryManager.exe`.
- The version comes from `<Version>` in `Directory.Build.props`.

Windows builds update themselves from GitHub Releases; see [Updates](#updates).

---

## Project layout

```text
EdenGameLibraryManager.sln
src/
  EdenGameLibraryManager.Core/            Models, interfaces, pure logic (matching, storage planning, URL/path safety)
  EdenGameLibraryManager.Infrastructure/
    AeroFoil/      AeroFoilClient, CatalogParser, SettingsConnectionProvider
    TorBox/        TorBoxClient (mylist catalogue, requestdl links)
    TitleDb/       TitleDatabase (streamed titledb import, SQLite lookups, weekly refresh)
    Library/       ServerLibraryProvider (AeroFoil or TorBox, offline cache, titledb enrichment), GameMetadataService
    Downloads/     HttpDownloader (Range/resume), DownloadManager (queue), DownloadLinkResolver, BandwidthLimiter
    Storage/       LocalLibraryService (scan/watch/delete), CacheManager, AppPaths, VolumeInfo
    Persistence/   ManifestService (SQLite), SettingsService (JSON)
    Security/      DPAPI / Keychain / Secret Service credential stores
    Logging/       File logger
  EdenGameLibraryManager.App/             Avalonia UI: Views (XAML), ViewModels, Services, Assets
tests/EdenGameLibraryManager.Tests/       xUnit tests (+ optional integration tests)
build/publish.ps1, publish.cmd            Windows release script (tests, publish, optional signing, ZIP + SHA-256)
build/publish.sh                          Packaging for Windows / macOS / Linux from bash
docs/example-settings.json
```

Main interfaces (`Core/Interfaces`): `IGameLibraryProvider`, `IAeroFoilClient`,
`IGameMetadataService`, `ITitleDatabase`, `IDownloadLinkResolver`, `ILocalLibraryService`,
`IDownloadManager`, `ICacheManager`, `ISettingsService`, `IManifestService`, `ICredentialStore`.
`ServerLibraryProvider` serves both profile types. An SMB provider is intentionally not included.

Dependencies: Avalonia (+ Fluent theme, Inter font), CommunityToolkit.Mvvm, Microsoft.Data.Sqlite,
Microsoft.Extensions.DependencyInjection / Logging, and System.Security.Cryptography.ProtectedData.
Tests use xUnit.

---

## Status and known limitations

Phases 1, 2 and 3 are implemented: the MVP, advanced download management, and the enhanced
experience. Phase 3 covers details with screenshots, sorting and filtering, base/update/DLC
grouping, parallel downloads, speed limits, offline catalogues, artwork caching, multiple AeroFoil
profiles and cross-platform packaging.

- Verification compares exact byte counts against the server's `Content-Length` / `Content-Range`
  and the catalogue size. AeroFoil doesn't publish file hashes, so no hash check is possible.
- Eden's play-time file records total time only, not when a game was last played, so the
  cleanup picker's *Last used* still reflects download date and *Open Folder*.
- `.nsz`/`.xcz` files are stored as-is and never converted. Whether Eden opens them depends on
  your Eden build.
- macOS and Linux builds compile and package from this repository. The UI and download flow were
  exercised on Windows and in a headless renderer. Test on real macOS/Linux machines before handing
  builds to others.
