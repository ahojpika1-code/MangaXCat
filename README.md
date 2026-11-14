# MangaXcat

MangaXcat is a Windows desktop manga reader and library manager built with WPF and WebView2. 
 Ai coded ( cant code use that information how you would like )

 ## <img width="2559" height="1392" alt="Snímek obrazovky 2026-11-14 191544" src="https://github.com/user-attachments/assets/0bcd9f0f-0555-4f36-9625-5ac4c052b9f4" />

## Features


- Discover MangaDex titles and browse English chapters.
- Save favourites, manga links, and chapter reading progress locally.
- Sync MangaDex favourites and read progress when connected to an account.
- Open manga and source websites in in-app tabs.
- Import manga links from MangaFire, ThunderScans, WEBTOON, and MANGA Plus.

<img width="2553" height="1369" alt="Snímek obrazovky 2026-11-14 185316" src="https://github.com/user-attachments/assets/c3017831-ab80-4bb4-990a-8bc8d6f3a4f5" />

## Install and run

MangaXcat currently provides source code rather than a prebuilt installer. To install it from a GitHub source ZIP:

1. On the repository page, select **Code** > **Download ZIP**.
2. Extract the ZIP to a folder you can write to, such as `Documents\MangaXcat`.
3. Make sure the requirements below are installed.
4. Double-click `Launch MangaXcat.bat`. On first launch, the script builds the app, downloads its build dependencies, creates a desktop shortcut, and starts MangaXcat.

To build without launching, open PowerShell in the extracted folder and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Then run `MangaXcat.exe`.

### Requirements

- Windows 10 or later
- .NET Framework 4.8
- Microsoft Edge WebView2 Runtime
- Windows PowerShell and `curl.exe` (included with current Windows versions)
- An internet connection for the first build and MangaDex browsing

The build script downloads the Microsoft WebView2 SDK and uBlock Origin Lite into `packages/` and `extensions/`, then builds `MangaXcat.exe`. These generated files are intentionally excluded from the source repository.

If Windows blocks the launcher, right-click `Launch MangaXcat.bat`, choose **Properties**, select **Unblock** if shown, then try again. Only run the app if you trust the source.

## Local data

User settings, favourites, saved chapters, browser tabs, and MangaDex session data remain under `%LOCALAPPDATA%\MangaShelf` so existing data is preserved across the rename.

## Third-party components

The build downloads Microsoft WebView2 and uBlock Origin Lite. Their licenses and notices are provided by their respective projects:

- [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
- [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home)

No license is currently declared for the MangaXcat application source.
