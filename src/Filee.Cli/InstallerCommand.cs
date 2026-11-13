// Automatic Windows Explorer context menu, User PATH, and Uninstall registration.
// Runs with zero administrator rights, zero background RAM, and zero restarts.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Filee.Cli;

[SupportedOSPlatform("windows")]
internal static class InstallerCommand
{
    public static int Install(CliOptions options, TextWriter output, TextWriter errors)
    {
        if (!OperatingSystem.IsWindows())
        {
            errors.WriteLine("Automatic shell integration is currently supported on Windows.");
            return Cli.UsageError;
        }

        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var cliPath = Path.Combine(installDir, "filee-cli.exe");
        var launcherPath = Path.Combine(installDir, "filee-run.exe");
        if (!File.Exists(launcherPath))
            launcherPath = cliPath;

        try
        {
            RegisterCascadingMenus(cliPath, launcherPath);
            RegisterWindows11Package(installDir);
            RegisterUserPath(installDir);
            RegisterUninstallEntry(installDir, cliPath, launcherPath);

            if (!options.Quiet)
            {
                output.WriteLine("Filee installed successfully!");
                output.WriteLine($"- Location: {installDir}");
                output.WriteLine("- Windows Explorer right-click context menu registered.");
                output.WriteLine("- User PATH updated (run 'filee' anywhere in Terminal/PowerShell/CMD).");
                output.WriteLine("- Windows Settings -> Installed Apps entry created.");
            }

            if (options.Notify || !options.Quiet)
            {
                NativeToast.Show("Filee Installed", "Right-click any supported file in File Explorer to convert.");
            }

            return Cli.Success;
        }
        catch (Exception ex)
        {
            errors.WriteLine($"Failed to complete setup: {ex.Message}");
            return 1;
        }
    }

    public static int Uninstall(CliOptions options, TextWriter output, TextWriter errors)
    {
        if (!OperatingSystem.IsWindows())
        {
            errors.WriteLine("Automatic uninstall is currently supported on Windows.");
            return Cli.UsageError;
        }

        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

        try
        {
            UnregisterCascadingMenus();
            UnregisterWindows11Package();
            UnregisterUserPath(installDir);
            UnregisterUninstallEntry();

            if (!options.Quiet)
            {
                output.WriteLine("Filee uninstalled successfully.");
                output.WriteLine("- Windows Explorer right-click context menus removed.");
                output.WriteLine("- User PATH entry removed.");
                output.WriteLine("- Windows Settings entry removed.");
            }

            if (options.Notify || !options.Quiet)
            {
                NativeToast.Show("Filee Uninstalled", "Context menu and PATH integration removed.");
            }

            var programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Filee");
            if (string.Equals(installDir, programsDir, StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 2 /nobreak > NUL & rmdir /s /q \"{installDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                Process.Start(psi);
            }

            return Cli.Success;
        }
        catch (Exception ex)
        {
            errors.WriteLine($"Failed to complete uninstall: {ex.Message}");
            return 1;
        }
    }

    private static void RegisterCascadingMenus(string cliPath, string launcherPath)
    {
        using var hkcu = Registry.CurrentUser;

        // 1. Images
        RegisterGroup(hkcu, @"Software\Classes\SystemFileAssociations\image\shell\Filee", cliPath,
        [
            ("to_webp", "Convert to WebP (.webp)", $"\"{launcherPath}\" convert \"%1\" --to webp --quiet --notify"),
            ("to_png",  "Convert to PNG (.png)",   $"\"{launcherPath}\" convert \"%1\" --to png --quiet --notify"),
            ("to_jpg",  "Convert to JPEG (.jpg)",  $"\"{launcherPath}\" convert \"%1\" --to jpg --quiet --notify"),
            ("to_pdf",  "Convert to PDF (.pdf)",   $"\"{launcherPath}\" convert \"%1\" --to pdf --quiet --notify"),
        ]);

        // 2. Documents
        foreach (var ext in new[] { ".md", ".txt" })
        {
            RegisterGroup(hkcu, $@"Software\Classes\SystemFileAssociations\{ext}\shell\Filee", cliPath,
            [
                ("to_pdf",  "Convert to PDF (.pdf)",        $"\"{launcherPath}\" convert \"%1\" --to pdf --quiet --notify"),
                ("to_docx", "Convert to Word (.docx)",      $"\"{launcherPath}\" convert \"%1\" --to docx --quiet --notify"),
                ("to_html", "Convert to HTML Web Page (.html)", $"\"{launcherPath}\" convert \"%1\" --to html --quiet --notify"),
            ]);
        }

        // 3. PDF
        RegisterGroup(hkcu, @"Software\Classes\SystemFileAssociations\.pdf\shell\Filee", cliPath,
        [
            ("to_png",  "Extract as Images (.png)",     $"\"{launcherPath}\" convert \"%1\" --to png --quiet --notify"),
            ("to_docx", "Convert to Word (.docx)",      $"\"{launcherPath}\" convert \"%1\" --to docx --quiet --notify"),
            ("to_md",   "Convert to Markdown (.md)",    $"\"{launcherPath}\" convert \"%1\" --to md --quiet --notify"),
            ("to_txt",  "Extract Plain Text (.txt)",    $"\"{launcherPath}\" convert \"%1\" --to txt --quiet --notify"),
        ]);

        // 4. Spreadsheets
        foreach (var ext in new[] { ".xlsx", ".xls", ".ods", ".csv" })
        {
            RegisterGroup(hkcu, $@"Software\Classes\SystemFileAssociations\{ext}\shell\Filee", cliPath,
            [
                ("to_csv",  "Convert to CSV (.csv)",        $"\"{launcherPath}\" convert \"%1\" --to csv --quiet --notify"),
                ("to_xlsx", "Convert to Excel (.xlsx)",     $"\"{launcherPath}\" convert \"%1\" --to xlsx --quiet --notify"),
            ]);
        }

        // 5. Structured Data
        foreach (var ext in new[] { ".json", ".yaml", ".yml", ".toml", ".xml" })
        {
            RegisterGroup(hkcu, $@"Software\Classes\SystemFileAssociations\{ext}\shell\Filee", cliPath,
            [
                ("to_json", "Convert to JSON (.json)",      $"\"{launcherPath}\" convert \"%1\" --to json --quiet --notify"),
                ("to_yaml", "Convert to YAML (.yaml)",      $"\"{launcherPath}\" convert \"%1\" --to yaml --quiet --notify"),
                ("to_toml", "Convert to TOML (.toml)",      $"\"{launcherPath}\" convert \"%1\" --to toml --quiet --notify"),
                ("to_csv",  "Convert to CSV (.csv)",        $"\"{launcherPath}\" convert \"%1\" --to csv --quiet --notify"),
            ]);
        }
    }

    private static void RegisterGroup(RegistryKey root, string keyPath, string cliPath, (string id, string label, string cmd)[] subcommands)
    {
        using var groupKey = root.CreateSubKey(keyPath, writable: true);
        if (groupKey == null) return;

        groupKey.SetValue("MUIVerb", "Convert with Filee");
        groupKey.SetValue("SubCommands", "");
        groupKey.SetValue("Icon", $"\"{cliPath}\",0");

        using var shellKey = groupKey.CreateSubKey("shell", writable: true);
        if (shellKey == null) return;

        foreach (var (id, label, cmd) in subcommands)
        {
            using var subKey = shellKey.CreateSubKey(id, writable: true);
            subKey.SetValue("MUIVerb", label);
            using var cmdKey = subKey.CreateSubKey("command", writable: true);
            cmdKey.SetValue("", cmd);
        }
    }

    private static void UnregisterCascadingMenus()
    {
        using var hkcu = Registry.CurrentUser;

        TryDeleteKey(hkcu, @"Software\Classes\SystemFileAssociations\image\shell\Filee");
        foreach (var ext in new[] { ".md", ".txt", ".pdf", ".xlsx", ".xls", ".ods", ".csv", ".json", ".yaml", ".yml", ".toml", ".xml" })
        {
            TryDeleteKey(hkcu, $@"Software\Classes\SystemFileAssociations\{ext}\shell\Filee");
        }
    }

    private static void TryDeleteKey(RegistryKey root, string subKey)
    {
        try
        {
            root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void RegisterWindows11Package(string installDir)
    {
        var msix = Path.Combine(installDir, "FileeExplorerMenu.msix");
        if (!File.Exists(msix)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Add-AppxPackage -Path '{msix}' -ExternalLocation '{installDir}' -AllowUnsigned -ErrorAction SilentlyContinue\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    private static void UnregisterWindows11Package()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-AppxPackage Filee.ExplorerMenu -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    private static void RegisterUserPath(string installDir)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

        if (!parts.Any(p => string.Equals(p, installDir, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(installDir);
            Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), EnvironmentVariableTarget.User);
        }
    }

    private static void UnregisterUserPath(string installDir)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.Equals(p, installDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), EnvironmentVariableTarget.User);
    }

    private static void RegisterUninstallEntry(string installDir, string cliPath, string launcherPath)
    {
        using var hkcu = Registry.CurrentUser;
        using var key = hkcu.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Filee", writable: true);
        if (key == null) return;

        key.SetValue("DisplayName", "Filee");
        key.SetValue("DisplayVersion", Help.Version);
        key.SetValue("Publisher", "Syth");
        key.SetValue("DisplayIcon", $"\"{cliPath}\",0");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{launcherPath}\" uninstall");
        key.SetValue("QuietUninstallString", $"\"{launcherPath}\" uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("URLInfoAbout", "https://github.com/Deepesh70/Filee-Lean");
    }

    private static void UnregisterUninstallEntry()
    {
        using var hkcu = Registry.CurrentUser;
        TryDeleteKey(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Filee");
    }
}
