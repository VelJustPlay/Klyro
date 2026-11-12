# Klyro Browser

Klyro is currently available for Windows. Linux is in development and macOS is deferred.

## Downloads

Download the **[Windows 10/11 (x64) installer](https://github.com/VelJustPlay/Klyro/releases/latest/download/Klyro-win-Setup.exe)**. Windows 10 and 11 use the same build. Klyro requires the Microsoft Edge WebView2 Runtime; install the [Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) if it is not already present.

Linux is still being developed and macOS is deferred. They are not available as downloads yet.

Blank tabs do not start a browser engine, and closing a Windows tab releases its WebView2 control. Total RAM still depends largely on the pages and browser-engine processes in use, so it has no fixed minimum.

## Updates

The in-app updater is available in Velopack-installed Windows copies. Standalone `.exe` copies cannot self-update; run the installer once and use the installed Klyro shortcut to receive in-app updates. Linux and macOS downloads are not available yet.

## Publish a release

1. Push the code changes to `main`.
2. Create and push a canonical SemVer tag using `MAJOR.UPDATE.FIX`, without leading zeroes, for example:

   ```powershell
   git tag v1.1.2
   git push origin v1.1.2
   ```

3. GitHub Actions builds and publishes the Windows installer and update packages as a GitHub Release. Windows installed copies check for updates at startup and every six hours; the installed version is always shown in the bottom-left status bar, and an update button appears only when a newer version is available.

Use the first number for major releases, the middle number for larger updates (0–99), and the last number for small fixes (0–999). SemVer rejects leading zeroes, so use `1.1.2`, not `1.01.2`. The release tag must increase from the previous release.
