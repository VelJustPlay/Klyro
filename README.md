# Klyro Browser

Klyro is a lightweight Windows browser built with WPF and WebView2.

## Install

Download and run `Klyro-win-Setup.exe` from the [latest GitHub release](https://github.com/VelJustPlay/Klyro/releases/latest).

The updater is available in Velopack-installed copies of Klyro. Existing standalone `.exe` copies cannot self-update; run the installer once and use the installed Klyro shortcut to receive in-app updates.

## Publish an update

1. Push the code changes to `main`.
2. Create and push a new semantic version tag, for example:

   ```powershell
   git tag v1.0.1
   git push origin v1.0.1
   ```

3. GitHub Actions builds the Windows installer and update packages, then publishes them as a GitHub Release. Installed copies check for updates at startup and every six hours; when one is available, the **Update available** button appears in the browser's status bar. Clicking it downloads the update and restarts Klyro to install it.

Release tags must increase in semantic version order. The first release using this updater should be tagged `v1.0.0`; later releases use a higher version such as `v1.0.1`.
