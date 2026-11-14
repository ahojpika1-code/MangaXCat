# MangaXcat

MangaXcat is a Windows desktop manga reader and library manager built with WPF and WebView2.

## Features

- Discover MangaDex titles and browse English chapters.
- Save favourites, manga links, and chapter reading progress locally.
- Sync MangaDex favourites and read progress when connected to an account.
- Open manga and source websites in in-app tabs.
- Import manga links from MangaFire, ThunderScans, WEBTOON, and MANGA Plus.

## Build

Requirements:

- Windows 10 or later
- Windows PowerShell
- .NET Framework 4.8
- `curl.exe`
- Microsoft Edge WebView2 Runtime

From the project directory, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The script downloads the Microsoft WebView2 SDK and uBlock Origin Lite into ignored `packages/` and `extensions/` folders, then builds `MangaXcat.exe`. The generated executable and runtime DLLs are intentionally not tracked in this source-only repository.

## Local data

User settings, favourites, saved chapters, browser tabs, and MangaDex session data remain under `%LOCALAPPDATA%\MangaShelf` so existing data is preserved across the rename.

## Third-party components

The build downloads Microsoft WebView2 and uBlock Origin Lite. Their licenses and notices are provided by their respective projects:

- [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
- [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home)

No license is currently declared for the MangaXcat application source.
