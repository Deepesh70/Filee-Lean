# Filee Setup & Installation Guide

This guide covers setup instructions for both **End-Users** (installing and using Filee on Windows) and **Developers** (building and testing from source).

---

## 1. End-User Guide (Using Filee)

Filee is an ultra-lean file converter with zero resident RAM: it runs on-demand via the Windows Explorer right-click context menu or from the command line.

### System Requirements
- **OS:** Windows 10 or Windows 11 (64-bit).
- **Admin Rights:** None needed (0 UAC prompts).
- **Runtimes:** None needed (all release packages are 100% self-contained).

---

### Option A: 1-Click Installer (Recommended)

1. Download **`Filee-Setup.exe`** from the latest GitHub Release.
2. Double-click **`Filee-Setup.exe`** and click **OK**.
3. Setup automatically:
   - Installs Filee to `%LOCALAPPDATA%\Programs\Filee`.
   - Registers all Explorer right-click context menus.
   - Deploys the Windows 11 sparse package (`FileeExplorerMenu.msix`).
   - Adds `filee` to your User `PATH` (works in any terminal immediately).
   - Registers Filee in Windows Settings → **Installed Apps**.

---

### Option B: Portable Package (.zip)

1. Download **`Filee-win-x64.zip`** from the latest GitHub Release.
2. Extract the archive anywhere on your machine (e.g., `D:\Tools\Filee`).
3. Double-click **`install.cmd`** in the extracted folder to register context menus and add Filee to your User `PATH` in-place.
4. To remove shell integration later, double-click **`uninstall.cmd`**.

---

### How to Use

1. **Right-Click in Explorer:** Right-click any supported file (e.g. `image.png`, `notes.md`, `sheet.xlsx`, `report.pdf`, `config.json`).
2. **Choose Format:** Hover over **Convert with Filee ▸** and click your desired output format.
3. **Execution:** The mouse cursor switches to the busy/loading state, no command prompt window appears, and a native Windows toast notification appears once the converted file is ready.

---

### Command Line Usage

From any PowerShell, Command Prompt, or Windows Terminal window:

```powershell
# Convert single file
filee convert photo.png --to webp --notify

# Convert multiple files with custom quality
filee convert a.png b.png --to jpg --quality 85 --notify

# Watch a folder for incoming files
filee watch D:\Incoming --to pdf --move-originals

# Shell integration commands
filee install      # Registers context menus and PATH
filee uninstall    # Removes context menus and PATH
```

---

### How to Uninstall

You can uninstall Filee at any time using standard Windows tools:
1. Open **Windows Settings** (<kbd>Win</kbd> + <kbd>I</kbd>).
2. Go to **Apps** → **Installed Apps**.
3. Search for **Filee** and click **Uninstall**.

Or run `filee uninstall` from your terminal or `uninstall.cmd` from the portable folder.

---

## 2. Developer Guide (Building from Source)

### Prerequisites
- Windows 10 or 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- MinGW GCC (`gcc.exe` and `windres.exe` in PATH)
- PowerShell 7+ (`pwsh`)

### Build Steps

```powershell
# 1. Clone repository
git clone https://github.com/KnifeLemon/Filee.git
cd Filee

# 2. Build and verify test suite
dotnet test Filee.slnx

# 3. Build release packages (Filee-Setup.exe and Filee-win-x64.zip)
pwsh build/build-release.ps1
```

All release artifacts will be placed in the `Releases/` directory accompanied by `SHA256SUMS.txt`.
