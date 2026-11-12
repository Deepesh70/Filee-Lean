# Filee Setup & Installation Guide

This guide covers setup instructions for both **End-Users** (installing and using Filee on Windows) and **Developers** (building and testing from source).

---

## 1. End-User Guide (Using Filee)

Filee is an ultra-lean file converter with zero resident RAM: it runs on-demand via the Windows Explorer right-click context menu or from the command line.

### System Requirements
- **OS:** Windows 10 or Windows 11 (64-bit).
- **Runtime:** .NET 10 Desktop Runtime (or included in self-contained builds).

---

### Step-by-Step Installation

#### Step 1: Download or Extract Filee
Place the Filee folder in your preferred location (for example, `C:\Program Files\Filee` or `C:\Users\<YourUsername>\AppData\Local\Programs\Filee`).

Ensure the folder contains:
- `filee-cli.exe` (headless converter engine)
- `filee-run.exe` (windowless launcher that triggers the mouse loading spinner)
- `FileeExplorerMenu.dll` (native COM shell extension)

#### Step 2: Register Explorer Context Menu (No Admin Required)
Open PowerShell (press <kbd>Win</kbd> + <kbd>X</kbd> → **Terminal** or **PowerShell**) and run:

```powershell
$installDir = "C:\Path\To\Filee" # Set your actual Filee directory path here

$cli = Join-Path $installDir "filee-cli.exe"
$launcher = Join-Path $installDir "filee-run.exe"

# 1. Images (.png, .jpg, .webp, .bmp, .gif, .svg)
$imgRoot = "HKCU:\Software\Classes\SystemFileAssociations\image\shell\Filee"
New-Item -Path $imgRoot -Force | Out-Null
Set-ItemProperty $imgRoot -Name "MUIVerb" -Value "Convert with Filee"
Set-ItemProperty $imgRoot -Name "SubCommands" -Value ""
Set-ItemProperty $imgRoot -Name "Icon" -Value "`"$cli`",0"
@(
    @("to_webp", "Convert to WebP (.webp)", "webp"),
    @("to_png",  "Convert to PNG (.png)",   "png"),
    @("to_jpg",  "Convert to JPEG (.jpg)",  "jpg"),
    @("to_pdf",  "Convert to PDF (.pdf)",   "pdf")
) | ForEach-Object {
    $sub = "$imgRoot\shell\$($_[0])"
    New-Item -Path "$sub\command" -Force | Out-Null
    Set-ItemProperty $sub -Name "MUIVerb" -Value $_[1]
    Set-ItemProperty "$sub\command" -Name "(Default)" -Value "`"$launcher`" convert `"%1`" --to $($_[2]) --quiet --notify"
}

# 2. Documents (.md, .txt)
foreach ($ext in ".md", ".txt") {
    $docRoot = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\Filee"
    New-Item -Path $docRoot -Force | Out-Null
    Set-ItemProperty $docRoot -Name "MUIVerb" -Value "Convert with Filee"
    Set-ItemProperty $docRoot -Name "SubCommands" -Value ""
    Set-ItemProperty $docRoot -Name "Icon" -Value "`"$cli`",0"
    @(
        @("to_pdf",  "Convert to PDF (.pdf)",        "pdf"),
        @("to_docx", "Convert to Word (.docx)",      "docx"),
        @("to_html", "Convert to HTML Web Page (.html)", "html")
    ) | ForEach-Object {
        $sub = "$docRoot\shell\$($_[0])"
        New-Item -Path "$sub\command" -Force | Out-Null
        Set-ItemProperty $sub -Name "MUIVerb" -Value $_[1]
        Set-ItemProperty "$sub\command" -Name "(Default)" -Value "`"$launcher`" convert `"%1`" --to $($_[2]) --quiet --notify"
    }
}

# 3. PDF Documents (.pdf)
$pdfRoot = "HKCU:\Software\Classes\SystemFileAssociations\.pdf\shell\Filee"
New-Item -Path $pdfRoot -Force | Out-Null
Set-ItemProperty $pdfRoot -Name "MUIVerb" -Value "Convert with Filee"
Set-ItemProperty $pdfRoot -Name "SubCommands" -Value ""
Set-ItemProperty $pdfRoot -Name "Icon" -Value "`"$cli`",0"
@(
    @("to_png",  "Extract as Images (.png)",     "png"),
    @("to_docx", "Convert to Word (.docx)",      "docx"),
    @("to_md",   "Convert to Markdown (.md)",    "md"),
    @("to_txt",  "Extract Plain Text (.txt)",    "txt")
) | ForEach-Object {
    $sub = "$pdfRoot\shell\$($_[0])"
    New-Item -Path "$sub\command" -Force | Out-Null
    Set-ItemProperty $sub -Name "MUIVerb" -Value $_[1]
    Set-ItemProperty "$sub\command" -Name "(Default)" -Value "`"$launcher`" convert `"%1`" --to $($_[2]) --quiet --notify"
}

# 4. Spreadsheets (.xlsx, .xls, .ods, .csv)
foreach ($ext in ".xlsx", ".xls", ".ods", ".csv") {
    $sheetRoot = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\Filee"
    New-Item -Path $sheetRoot -Force | Out-Null
    Set-ItemProperty $sheetRoot -Name "MUIVerb" -Value "Convert with Filee"
    Set-ItemProperty $sheetRoot -Name "SubCommands" -Value ""
    Set-ItemProperty $sheetRoot -Name "Icon" -Value "`"$cli`",0"
    @(
        @("to_csv",  "Convert to CSV (.csv)",        "csv"),
        @("to_xlsx", "Convert to Excel (.xlsx)",     "xlsx")
    ) | ForEach-Object {
        $sub = "$sheetRoot\shell\$($_[0])"
        New-Item -Path "$sub\command" -Force | Out-Null
        Set-ItemProperty $sub -Name "MUIVerb" -Value $_[1]
        Set-ItemProperty "$sub\command" -Name "(Default)" -Value "`"$launcher`" convert `"%1`" --to $($_[2]) --quiet --notify"
    }
}

# 5. Structured Data (.json, .yaml, .yml, .toml, .xml)
foreach ($ext in ".json", ".yaml", ".yml", ".toml", ".xml") {
    $dataRoot = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\Filee"
    New-Item -Path $dataRoot -Force | Out-Null
    Set-ItemProperty $dataRoot -Name "MUIVerb" -Value "Convert with Filee"
    Set-ItemProperty $dataRoot -Name "SubCommands" -Value ""
    Set-ItemProperty $dataRoot -Name "Icon" -Value "`"$cli`",0"
    @(
        @("to_json", "Convert to JSON (.json)",      "json"),
        @("to_yaml", "Convert to YAML (.yaml)",      "yaml"),
        @("to_toml", "Convert to TOML (.toml)",      "toml"),
        @("to_csv",  "Convert to CSV Table (.csv)",  "csv")
    ) | ForEach-Object {
        $sub = "$dataRoot\shell\$($_[0])"
        New-Item -Path "$sub\command" -Force | Out-Null
        Set-ItemProperty $sub -Name "MUIVerb" -Value $_[1]
        Set-ItemProperty "$sub\command" -Name "(Default)" -Value "`"$launcher`" convert `"%1`" --to $($_[2]) --quiet --notify"
    }
}

Write-Host "Filee Explorer Context Menu registered successfully!" -ForegroundColor Green
```

This takes effect **immediately** without needing to restart Explorer or restart your computer.

---

### How to Use
1. **Right-Click in Explorer:** Right-click any supported file (e.g. `image.png`, `notes.md`, `sheet.xlsx`, `report.pdf`, `config.json`).
2. **Choose Format:** Hover over **Convert with Filee ▸** and click your desired output format.
3. **Execution:** The mouse cursor switches to the busy/loading state, no command prompt window appears, and a native Windows Action Center notification appears once the converted file is ready.

---

### Command Line Usage
To convert files from your terminal or scripts:

```powershell
# Convert single file
filee convert photo.png --to webp --notify

# Convert multiple files with custom quality
filee convert a.png b.png --to jpg --quality 85 --notify

# Watch a folder for incoming files
filee watch D:\Incoming --to pdf --move-originals
```

---

### How to Uninstall / Unregister
To remove the context menu entries from your user account:

```powershell
$keys = @(
    "HKCU:\Software\Classes\SystemFileAssociations\image\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.md\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.txt\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.pdf\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.xlsx\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.xls\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.ods\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.csv\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.json\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.yaml\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.yml\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.toml\shell\Filee",
    "HKCU:\Software\Classes\SystemFileAssociations\.xml\shell\Filee"
)
foreach ($key in $keys) {
    Remove-Item -Path $key -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Filee context menu removed." -ForegroundColor Green
```

---

## 2. Developer Guide (Building from Source)

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`dotnet --version` should return `10.0.xxx`).
- PowerShell 7 (`pwsh`) or Windows PowerShell.
- Git.

*Note: Visual Studio C++ and Windows SDK are **not** required. The build script automatically downloads a pinned, portable Zig 0.16.0 compiler into `build/.cache` to compile native Win32 C code.*

---

### Build Instructions

#### 1. Clone the Repository
```powershell
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
```

#### 2. Build the Headless CLI & Core Engine
```powershell
dotnet build src/Filee.Cli/Filee.Cli.csproj
```

#### 3. Build the Native Windows 11 Explorer Extension
```powershell
pwsh build/build-explorer-menu.ps1
```
This script:
1. Downloads and SHA-256 checks `zig-x86_64-windows-0.16.0` into `build/.cache`.
2. Compiles `src/Filee.ExplorerMenu/FileeExplorerMenu.c` with `-nostdlib` into `FileeExplorerMenu.dll` (16.5 KB).
3. Packages `FileeExplorerMenu.msix` using the Windows packaging API.

#### 4. Compile the Windowless Launcher (`filee-run.exe`)
```powershell
$zig = "build/.cache/zig-x86_64-windows-0.16.0/zig.exe"
& $zig cc -target x86_64-windows-gnu -O2 -s -nostdlib -Wall -Wextra `
    -isystem "build/.cache/zig-x86_64-windows-0.16.0/lib/libc/include/any-windows-any" `
    src/Filee.ExplorerMenu/filee-run.c -lkernel32 -luser32 `
    "-Wl,--entry=WinMain" "-Wl,--subsystem,windows" `
    -o publish/win-x64/filee-run.exe
```

#### 5. Publish Release Binaries
```powershell
dotnet publish src/Filee.Cli/Filee.Cli.csproj -c Release -r win-x64 -o publish/win-x64 --self-contained false
Copy-Item build/.cache/explorer-menu/FileeExplorerMenu.* publish/win-x64/ -Force
Copy-Item publish/win-x64/filee-cli.exe publish/win-x64/Filee.exe -Force
```

---

### Running Automated Tests

Run the core engine test suite (1,140 unit and integration tests):
```powershell
dotnet test tests/Filee.Core.Tests/Filee.Core.Tests.csproj --no-restore
```

Run the SkiaSharp image converter test suite:
```powershell
dotnet test tests/Filee.Engines.Tests/Filee.Engines.Tests.csproj --no-restore
```

All tests should pass with 0 failures.
