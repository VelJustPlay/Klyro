# Klyro Browser

Klyro is a desktop browser for Windows, Linux, and macOS.

## Downloads

Get the latest platform build from [GitHub Releases](https://github.com/VelJustPlay/Klyro/releases/latest):

- **Windows 10/11 (x64):** [Installer](https://github.com/VelJustPlay/Klyro/releases/latest/download/Klyro-win-Setup.exe). Both Windows versions use the same build. Windows requires the Microsoft Edge WebView2 Runtime; install the [Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) if it is not already present.
- **Linux (x64):** [AppImage](https://github.com/VelJustPlay/Klyro/releases/latest/download/Klyro-linux-x86_64.AppImage). The system must provide GTK 3, WebKitGTK 4.1, and libsoup 3. On Debian/Ubuntu, install `libgtk-3-0 libwebkit2gtk-4.1-0 libsoup-3.0-0`.
- **macOS (Apple silicon):** [arm64 app bundle](https://github.com/VelJustPlay/Klyro/releases/latest/download/Klyro-macos-arm64.zip).
- **macOS (Intel):** [x64 app bundle](https://github.com/VelJustPlay/Klyro/releases/latest/download/Klyro-macos-x64.zip).

The current `v1.1.2` release contains Windows downloads only. Linux and macOS assets will be available after the cross-platform build is published in a new tagged release.

macOS builds are not signed or notarized. macOS may require you to approve the app in Privacy & Security before opening it.

## Updates

The in-app updater is available in Velopack-installed Windows copies. Standalone `.exe` copies cannot self-update; run the installer once and use the installed Klyro shortcut to receive in-app updates. Linux and macOS builds are updated by downloading a newer release.

## Workspaces

Use the **+** button for a browser tab or the adjacent tab-type menu to open a browser tab, Notes, or a C# workspace. Notes supports formatted text and saving or opening `.txt` and `.rtf` files.

Use the **Downloads** button in the navigation bar to open download history from any website. Recent downloads are saved locally and remain available after restarting Klyro.

The C# workspace is available in the Windows desktop app. It creates a console project and `Program.cs` under `%LOCALAPPDATA%\LightBrowser\CSharpWorkspace`. It requires the .NET 10 SDK. The editor highlights C# syntax, and the black terminal colors warnings and errors for readability. Set the program name and choose **Download .exe** to publish a self-contained executable to your Downloads folder. Edit and save with **Ctrl+S**, run the program with **F5**, or enter commands in the integrated terminal.

## Publish a release

1. Push the code changes to `main`.
2. Create and push a canonical SemVer tag using `MAJOR.UPDATE.FIX`, without leading zeroes, for example:

   ```powershell
   git tag v1.1.2
   git push origin v1.1.2
   ```

3. GitHub Actions builds the Windows installer, Linux AppImage, and macOS arm64/x64 bundles, then publishes them together as a GitHub Release. Windows installed copies check for updates at startup and every six hours; when one is available, the **Update available** button appears in the browser's status bar.

Use the first number for major releases, the middle number for larger updates (0–99), and the last number for small fixes (0–999). SemVer rejects leading zeroes, so use `1.1.2`, not `1.01.2`. The release tag must increase from the previous release.
