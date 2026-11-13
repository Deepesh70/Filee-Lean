#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <stdio.h>

#define IDI_APP_ICON 1
#define IDR_PAYLOAD  101

int WINAPI WinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPSTR lpCmdLine, int nCmdShow)
{
    (void)hPrevInstance;
    (void)nCmdShow;

    // 1. Silent flag check
    BOOL silent = FALSE;
    if (lpCmdLine && (strstr(lpCmdLine, "/S") || strstr(lpCmdLine, "/s") ||
                      strstr(lpCmdLine, "--silent") || strstr(lpCmdLine, "--quiet")))
    {
        silent = TRUE;
    }

    // 2. Resolve destination: %LOCALAPPDATA%\Programs\Filee
    WCHAR localAppData[MAX_PATH];
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, localAppData)))
    {
        if (!silent)
        {
            MessageBoxW(NULL, L"Could not resolve the LocalAppData directory.", L"Filee Setup Error", MB_ICONERROR);
        }
        return 1;
    }

    WCHAR targetDir[MAX_PATH];
    wsprintfW(targetDir, L"%s\\Programs\\Filee", localAppData);

    // 3. User confirmation dialog (if interactive)
    if (!silent)
    {
        WCHAR prompt[1024];
        wsprintfW(prompt,
            L"Filee will be installed to:\n\n%s\n\n"
            L"Setup includes:\n"
            L"  - Fast right-click conversion menus in File Explorer\n"
            L"  - Command-line 'filee' integration into your PATH\n"
            L"  - Zero administrator privileges required\n"
            L"  - Zero background RAM consumption\n\n"
            L"Do you want to continue?",
            targetDir);

        if (MessageBoxW(NULL, prompt, L"Filee Setup", MB_ICONINFORMATION | MB_OKCANCEL) != IDOK)
        {
            return 0; // User cancelled
        }
    }

    // 4. Ensure target directory exists
    SHCreateDirectoryExW(NULL, targetDir, NULL);

    // 5. Locate embedded ZIP payload resource
    HRSRC hRes = FindResource(hInstance, MAKEINTRESOURCE(IDR_PAYLOAD), RT_RCDATA);
    if (!hRes)
    {
        if (!silent)
        {
            MessageBoxW(NULL, L"Corrupted installer: embedded payload resource missing.", L"Filee Setup Error", MB_ICONERROR);
        }
        return 2;
    }

    HGLOBAL hMem = LoadResource(hInstance, hRes);
    DWORD payloadSize = SizeofResource(hInstance, hRes);
    void *payloadData = LockResource(hMem);
    if (!payloadData || payloadSize == 0)
    {
        if (!silent)
        {
            MessageBoxW(NULL, L"Failed to load embedded payload.", L"Filee Setup Error", MB_ICONERROR);
        }
        return 3;
    }

    // 6. Write temporary payload zip file
    WCHAR tempPath[MAX_PATH];
    GetTempPathW(MAX_PATH, tempPath);
    WCHAR tempZip[MAX_PATH];
    wsprintfW(tempZip, L"%s\\filee_setup_%u.zip", tempPath, GetCurrentProcessId());

    HANDLE hFile = CreateFileW(tempZip, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE)
    {
        if (!silent)
        {
            MessageBoxW(NULL, L"Could not write temporary installer files.", L"Filee Setup Error", MB_ICONERROR);
        }
        return 4;
    }

    DWORD bytesWritten = 0;
    WriteFile(hFile, payloadData, payloadSize, &bytesWritten, NULL);
    CloseHandle(hFile);

    // 7. Extract archive using Windows native tar.exe (System32)
    WCHAR sysDir[MAX_PATH];
    GetSystemDirectoryW(sysDir, MAX_PATH);
    WCHAR tarPath[MAX_PATH];
    wsprintfW(tarPath, L"%s\\tar.exe", sysDir);

    WCHAR tarCmd[MAX_PATH * 3];
    wsprintfW(tarCmd, L"\"%s\" -xf \"%s\" -C \"%s\"", tarPath, tempZip, targetDir);

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    ZeroMemory(&pi, sizeof(pi));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    BOOL extracted = FALSE;
    if (CreateProcessW(NULL, tarCmd, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
    {
        WaitForSingleObject(pi.hProcess, 120000);
        DWORD exitCode = 0;
        GetExitCodeProcess(pi.hProcess, &exitCode);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        extracted = (exitCode == 0);
    }

    DeleteFileW(tempZip);

    if (!extracted)
    {
        if (!silent)
        {
            MessageBoxW(NULL, L"Failed to extract Filee installation files.", L"Filee Setup Error", MB_ICONERROR);
        }
        return 5;
    }

    // 8. Register shell integration, Win11 package, PATH, and Installed Apps entry
    WCHAR cliPath[MAX_PATH];
    wsprintfW(cliPath, L"%s\\filee-cli.exe", targetDir);

    WCHAR installCmd[MAX_PATH * 2];
    wsprintfW(installCmd, L"\"%s\" install --quiet", cliPath);

    ZeroMemory(&si, sizeof(si));
    ZeroMemory(&pi, sizeof(pi));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    if (CreateProcessW(NULL, installCmd, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
    {
        WaitForSingleObject(pi.hProcess, 30000);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }

    // 9. Completion confirmation (if interactive)
    if (!silent)
    {
        MessageBoxW(NULL,
            L"Filee has been installed successfully!\n\n"
            L"- Right-click any supported file in File Explorer to convert\n"
            L"- Type 'filee' in any PowerShell or Command Prompt window\n"
            L"- Manage or uninstall anytime in Windows Settings -> Installed Apps",
            L"Filee Setup Complete", MB_ICONINFORMATION | MB_OK);
    }

    return 0;
}
