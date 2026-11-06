# SteamCloudWipe

Delete your own Steam Cloud save files, for any game, whether or not it is installed.

This includes **Auto-Cloud** games, whose saves live in folders like AppData or Documents. Older tools based on `ISteamRemoteStorage` cannot see or delete those files.

## Usage

1. Download the latest release, or build it yourself (see below).
2. Run `SteamCloudWipe.exe`.
3. Scan the QR code with the Steam mobile app (**Steam Guard → Scan a QR code**) and approve the login.
4. Tick the games you want to wipe, or click **Select all**.
5. Click **Delete selected** and confirm.

Each game is checked again after deleting. The log shows `OK` when no files remain, or `FAIL` with the number of files left.

**Deletion is permanent.** Steam keeps no older versions of deleted cloud files. To keep anything, download it first from https://store.steampowered.com/account/remotestorage.

## Prevent re-uploads

If a game is still installed and its local save folder still exists, Steam can upload those files again the next time you play. Delete the local save folder too. [PCGamingWiki](https://www.pcgamingwiki.com) lists each game's save location.

## How it works

Steam only syncs a deletion when a file disappears *while the game is running*. Deleting files locally at any other time just makes Steam download them again.

This tool skips that mechanism and talks to Steam's Cloud service directly, the way the Steam client does:

1. It logs in through [SteamKit2](https://github.com/SteamRE/SteamKit) with a QR code, as a Steam client.
2. It reads the list of games with cloud files from your account's **Steam Cloud** page.
3. For each game, it calls:
   - `Cloud.EnumerateUserFiles`, which lists every file on the server, including Auto-Cloud files.
   - `Cloud.BeginAppUploadBatch`, then `Cloud.ClientDeleteFile` for each file (16 in parallel), then `Cloud.CompleteAppUploadBatchBlocking`. This is the same sequence the Steam client uses.
   - `Cloud.EnumerateUserFiles` again, to confirm nothing is left.

Your password never touches the tool, and nothing is saved to disk.

## Build

Requires the .NET 10 SDK and Windows.

```
dotnet build -c Release
```

## Notes

- Some entries are Steam's own data rather than game saves:
  - **Steam Client (7)** may refuse deletes.
  - **Steam Controller Configs (241100)** holds your controller layouts. Deleting it resets them.
- Not affiliated with Valve. Use at your own risk.
