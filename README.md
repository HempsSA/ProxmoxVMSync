# DarkSync Proxmox Archive 2.0.0

Desktop GUI tool for managing Proxmox VE backup archives. Scan, copy, and retain VM backups from multiple sources to an external archive with automatic scheduling, health monitoring, and SFTP support.

![Dark Theme](https://img.shields.io/badge/theme-dark-blue) ![.NET 9](https://img.shields.io/badge/.NET-9.0-purple) ![WPF](https://img.shields.io/badge/UI-WPF-green)

---

## Features

- **Multi-source scanning** — Scan local SMB/NFS shares and remote SFTP servers for VM backups
- **Smart sync planning** — Automatically calculates which backups need copying based on retention rules and importance levels
- **Importance-based retention** — Color-coded importance levels (Standard / Important / Critical) with automatic copy count
- **Health monitoring** — Per-VM health status (Healthy, Stale, Under-retained, Missing, Critical)
- **24-hour scheduler** — Automatic daily sync/copy or dry run at a configurable time
- **SFTP support** — Connect to remote Proxmox hosts via SFTP with password or key-file authentication
- **ntfy notifications** — Get push notifications on success or failure via [ntfy.sh](https://ntfy.sh)
- **Export / Import settings** — Share configurations between machines as JSON
- **Dark & Light themes** — Full dark mode with custom-styled controls
- **System tray** — Minimizes to tray; scheduler stays active while the app runs
- **Operation history** — Detailed log of all sync/copy/dry-run operations with VM health snapshots

## Screenshots

| Backup Archive | Archive Settings | Notifications |
|---|---|---|
| VM table with color-coded importance, copy/delete/sync controls | Retention policy, free space, archive identity | ntfy push notification configuration |

## Getting Started

### Prerequisites

- Windows 10/11
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later

### Build

```bash
git clone https://github.com/HempsSA/ProxmoxVMSync.git
cd ProxmoxVMSync
dotnet build -c Release
```

### Run

```bash
dotnet run --project DarkSync -c Release
```

Or find the executable at:
```
DarkSync\bin\Release\net9.0-windows\DarkSync.exe
```

## Configuration

Settings are stored at:
```
%AppData%\DarkSync\config.json
```

You can export/import settings from the GUI for easy migration between machines.

## How It Works

1. **Add sources** — Enter backup locations as `Name|path` (one per line). Supports local paths and SFTP URIs.
2. **Add VM IDs** — Click "Add VM IDs" and enter Proxmox VM IDs. Set importance levels by clicking the badge.
3. **Set destination** — Point to an external archive folder and initialize it.
4. **Sync / Copy** — Run a dry run first to preview, then execute the copy. Old backups are handled per your retention policy.

### Retention Modes

| Mode | Behavior |
|---|---|
| **Keep all** | Never delete external backups |
| **Move to recycle folder** | Move excess backups to `.darksync_recycle/` |
| **Delete permanently** | Remove excess backups immediately |

### Importance Levels

| Level | Copies | Color |
|---|---|---|
| 1 - Standard | 1 | 🔵 Blue |
| 2 - Important | 2 | 🟢 Green |
| 3 - Critical | 3 | 🔴 Red |

## Tech Stack

- **.NET 9** / WPF (Windows Presentation Foundation)
- **CommunityToolkit.Mvvm** — MVVM framework with source generators
- **SSH.NET** — SFTP client for remote backup access
- **Microsoft.Data.Sqlite** — Local history database
- **System.Text.Json** — Configuration serialization

## License

MIT
