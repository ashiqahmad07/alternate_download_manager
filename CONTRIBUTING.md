# Contributing to ADM

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

- **A bug or an idea:** open an [issue](https://github.com/ashiqahmad07/alternate_download_manager/issues/new/choose).
  For a bug, attach `logs\adm.log` (**Options > General > Open log folder**). It holds no file names,
  cookies or logins, so it is safe to share.
- **A security problem:** don't open an issue; follow [SECURITY.md](SECURITY.md).
- **A translation:** copy `src/ADM.App/Languages/en.json`, change its `_name` and `_code`, translate the
  texts, and open a pull request. Keep each `{0}`-style placeholder as it is.

## Project layout

| Path | What it is |
|---|---|
| `src/ADM.Core` | Download engine (HTTP, FTP, HLS and DASH), queues and scheduler, categories, state storage, IPC pipe |
| `src/ADM.App` | WPF app (`ADM.exe`): download list, dialogs, tray icon, clipboard monitor, wake timer |
| `src/ADM.NativeHost` | Native-messaging host for Chrome, Edge and Firefox (`ADM.NativeHost.exe`) |
| `extension/` | Manifest V3 browser extension: download capture, context menu, media detection popup |
| `scripts/install.ps1` | Builds the app and host into `dist/` and registers the native host |
| `scripts/publish.ps1` | Publishes the app, the host, its manifests and the extension into a folder |
| `scripts/build-installer.ps1`, `installer/ADM.iss` | Build the per-user installer with Inno Setup 6 |
| `scripts/fetch-video-tools.ps1` | Fetches yt-dlp, Deno and ffmpeg for the installer, each checked against its publisher's SHA-256 |
| `scripts/test-installer.ps1` | Installs, upgrades and uninstalls the installer, and checks each step |
| `scripts/pack-extension.ps1` | Packs the extension for Chrome and Edge (`.zip`) and Firefox (`.xpi`) |
| `scripts/make-icons.py` | Makes the app's and the extension's icons from the logo; run by hand when it changes |
| `installer/browser-setup.html` | The page on adding the extension, installed as `Browser setup.html` |
| `.github/workflows/ci.yml` | CI: builds with warnings as errors, runs both test suites, builds and tests the installer, and publishes a release for a `v*` tag |
| `Directory.Build.props` | ADM's version, in one place |
| `assets/` | The logo: `logo-source.png` as drawn, `logo.png` with a transparent background; `social-preview.png`, the repository's link preview |
| `tests/ADM.Core.Tests` | xUnit tests for the engine, using a local Kestrel server for real HTTP |
| `tests/extension` | Node tests that keep the extension's capture rules in step with the engine's |
| `tests/ADM.TestSite` | A local site of links and slow files, for trying the app by hand: `dotnet run --project tests\ADM.TestSite` |

## Building

You need the .NET 8 SDK, either on `PATH` or installed per user by Microsoft's `dotnet-install.ps1`.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

This builds ADM into `dist` and registers it with the browsers, as the installer does. The app and host
are published self-contained, because the browser starts the host without your shell's environment.
Load the extension from the repository's `extension` folder (or for Firefox from
`dist\extension-firefox`), then run `dist\ADM.exe`.

## Tests

```powershell
dotnet build ADM.sln -c Release -warnaserror
dotnet test tests\ADM.Core.Tests
node --test tests/extension/*.test.mjs
```

CI runs the same on every push and pull request, so please make sure they pass first. For a change to
the window or the browser extension, try it by hand too; `tests/ADM.TestSite` serves links and slow files
to try it on.

## Building the installer

Install [Inno Setup 6](https://jrsoftware.org/isdl.php), for example with
`winget install JRSoftware.InnoSetup --scope user`, and run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

It writes `dist\installer\ADM-Setup-<version>.exe`, with the version in `Directory.Build.props`.
`scripts\test-installer.ps1` installs, upgrades and uninstalls it and checks each step, as CI does on
every push; it puts back any browser registration it finds.

## Packing the extension

```powershell
powershell -ExecutionPolicy Bypass -File scripts\pack-extension.ps1 [-Sign]
```

This writes `dist\packages\ADM-chrome-<version>.zip`, for Chrome and Edge, and
`ADM-firefox-<version>-unsigned.xpi`. Release Firefox keeps only add-ons Mozilla has signed; the unsigned
one loads as a temporary add-on, or stays in Firefox Developer Edition or Nightly with
`xpinstall.signatures.required` set to `false` in `about:config`. With `-Sign`, Mozilla signs it as an
unlisted add-on, for handing out yourself, and the script writes `ADM-firefox-<version>.xpi`, which
installs in any Firefox. Signing needs Node.js and a free
[addons.mozilla.org API key](https://addons.mozilla.org/developers/addon/api/key/), in
`WEB_EXT_API_KEY` and `WEB_EXT_API_SECRET`. Mozilla signs each version once, so raise `version` in both
manifests before signing again. CI checks the Firefox package with Mozilla's own linter.

## Releasing

1. Raise `<Version>` in `Directory.Build.props` and commit.
2. Tag that commit `v<version>`, for example `v0.2.0`, and push the tag.

CI builds and tests the installer as on any push, then publishes a GitHub release for the tag with the
installer and the extension packages attached. It stops if the tag and `Directory.Build.props` disagree.

## Pull requests

- Keep a pull request to one change, with tests for engine behaviour.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`,
  `perf:`, `docs:`, `test:`, with a scope where it helps, as in `fix(app): ...`.
- By contributing, you agree that your work is released under the [MIT licence](LICENSE).

## Builds under the old name

If you ran a build from before the rename, when ADM was FluxDM: close it, run `install.ps1`, and reload
the extension in your browser. The first start brings over your list, settings, unfinished downloads and
startup entry. Finished files stay where they are, and `Downloads\FluxDM` stays your download folder
until you change it in Options.
