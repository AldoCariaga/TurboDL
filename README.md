# TurboDL

A fast, multi-connection download manager for Windows, with a Chrome and Edge extension.

TurboDL splits each download across several connections, each writing its own part of the file. When a connection finishes, it takes over half of the slowest part still downloading. Downloads survive slow, flaky or rate-limited servers, and you can pause and resume them, even after closing the app.

## Features

- **Segmented downloads.** Up to the number of connections you choose per file, written straight into place with no merge step. Each download's row shows every connection filling in its part.
- **Copes with bad servers.** Takes over stalled parts, retries with backoff, honours `Retry-After`, steps connections back when a host limits them, and detects a file that changed on the server instead of corrupting it.
- **Queue and schedule.** Limit how many downloads run at once, start and stop the queue on a schedule, and sleep or shut down the PC when it's done.
- **Speed limits.** A global limit and a per-download limit, both changeable while downloading.
- **Browser integration.** The extension hands Chrome and Edge downloads to TurboDL, and adds a *Download video with TurboDL* menu item.
- **Video downloads** with [yt-dlp](https://github.com/yt-dlp/yt-dlp). mp4 prefers H.264 + AAC so the file plays everywhere.
- **Batches.** Paste a block of text and every link in it becomes one batch. Split archives (`.part01.rar`, `.7z.001`, ...) are recognised and extracted with 7-Zip once every part is in.
- **File hosts.** When a link leads to a host's page instead of the file, a visible browser works through the page (countdowns, buttons) and hands TurboDL the real link. If the page shows a CAPTCHA, the browser comes on screen so you can solve it. Site configs teach it specific hosts (see [Site configs](#site-configs)).
- **Clipboard watching.** Copy a link to a watched file type and TurboDL offers to download it.
- Category folders, tray icon, completion notifications, and light/dark themes that follow Windows.

## Install

1. Download `TurboDL-Setup-<version>.exe` from [Releases](../../releases) and run it. It installs TurboDL for all users into `C:\Program Files\TurboDL` and registers it with Chrome and Edge. The .NET runtime is built in, so there's nothing else to install. Upgrades keep your downloads and settings.
2. Add the browser extension. It isn't in the Chrome Web Store, so load it unpacked:
   1. Get the `extension` folder from this repository: clone it, or download the source code zip from the release.
   2. Open `chrome://extensions` (or `edge://extensions`) and turn on **Developer mode**.
   3. Click **Load unpacked** and choose the `extension` folder.

   The extension's ID is fixed (`bmffkkpdabfjihjbmhmammbckmeaoblf`), and only that ID can talk to TurboDL.

To uninstall, use **Settings > Apps > Installed apps**. Your downloads list and settings are kept unless you tick the option to remove them.

Requires Windows 10 or 11, x64.

## Where things are kept

| What | Where |
|---|---|
| Downloads list | `%LOCALAPPDATA%\TurboDL\turbodl.db` |
| Settings | `%LOCALAPPDATA%\TurboDL\settings.json` |
| Your site configs | `%LOCALAPPDATA%\TurboDL\sites` |
| yt-dlp, ffmpeg, Deno, 7-Zip, Playwright | `%LOCALAPPDATA%\TurboDL\tools` (downloaded on first use and checked against SHA-256 hashes: published ones for yt-dlp, FFmpeg and Deno, pinned ones for 7-Zip and Playwright) |
| Downloaded files | `Downloads\TurboDL\<category>` by default |

## Site configs

A site config is a JSON file that tells the browser how to get through one file host's page: which element to wait for, a countdown, a CAPTCHA check, what to click. Configs are declarative: they can't run scripts. Built-in configs live in [`src/TurboDL.Core/Sites/BuiltIn`](src/TurboDL.Core/Sites/BuiltIn). Your own go in the sites folder above. **Settings** has *Open sites folder* and *Reload*. Hosts without a config get the general autopilot.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows. [Node.js](https://nodejs.org/) is only needed to run the extension's tests.

```powershell
dotnet build TurboDL.slnx            # build everything
dotnet test TurboDL.slnx             # run the .NET tests
node --test extension/test/          # run the extension tests

.\scripts\publish.ps1                # build dist\TurboDL-Setup-<version>.exe
```

`publish.ps1` builds self-contained, single-file win-x64 exes. The version comes from [`src/Directory.Build.props`](src/Directory.Build.props), plus the git commit hash.

### Layout

| Path | What it is |
|---|---|
| `src/TurboDL.Core` | Download engine, queue, storage, settings, video (yt-dlp) and archive (7-Zip) support, site configs |
| `src/TurboDL.App` | The WPF app |
| `src/TurboDL.Ipc` | Named-pipe protocol between the app, the native host and Setup |
| `src/TurboDL.NativeHost` | Chrome/Edge native messaging host that passes the extension's downloads to the app |
| `src/TurboDL.Installer`, `src/TurboDL.Setup` | Install/uninstall logic and the Setup wizard |
| `extension` | The Chrome/Edge extension (Manifest V3) |
| `tests` | xUnit tests, with fake yt-dlp and 7-Zip executables and a misbehaving local test server |

## Third-party tools

TurboDL downloads these on first use. They aren't bundled, and each has its own license: [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), [FFmpeg](https://ffmpeg.org/) builds from yt-dlp's FFmpeg-Builds (GPL), [Deno](https://deno.com/) (MIT), [7-Zip](https://www.7-zip.org/) (LGPL), and [Playwright](https://playwright.dev/) (Apache-2.0). The app is built with [WPF-UI](https://github.com/lepoco/wpfui), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) and [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore).

Download only what you have the right to download, and respect each site's terms.

## License

[MIT](LICENSE)
