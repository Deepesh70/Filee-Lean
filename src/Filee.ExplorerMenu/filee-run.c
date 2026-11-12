#define WIN32_LEAN_AND_MEAN
#include <windows.h>

int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR lpCmd, int nShow)
{
    (void)hInst;
    (void)hPrev;
    (void)lpCmd;
    (void)nShow;

    LPCWSTR cmdLine = GetCommandLineW();
    BOOL inQuote = FALSE;
    while (*cmdLine && (*cmdLine > L' ' || inQuote))
    {
        if (*cmdLine == L'"')
            inQuote = !inQuote;
        cmdLine++;
    }
    while (*cmdLine && *cmdLine <= L' ')
        cmdLine++;

    WCHAR exe[MAX_PATH];
    DWORD len = GetModuleFileNameW(NULL, exe, MAX_PATH);
    while (len > 0 && exe[len - 1] != L'\\')
        len--;
    exe[len] = 0;
    lstrcatW(exe, L"filee-cli.exe");

    DWORD fullCmdLen = lstrlenW(exe) + lstrlenW(cmdLine) + 8;
    WCHAR *fullCmd = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, fullCmdLen * sizeof(WCHAR));
    if (!fullCmd)
        return 1;

    fullCmd[0] = L'"';
    fullCmd[1] = 0;
    lstrcatW(fullCmd, exe);
    lstrcatW(fullCmd, L"\" ");
    lstrcatW(fullCmd, cmdLine);

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    ZeroMemory(&pi, sizeof(pi));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    SetCursor(LoadCursor(NULL, IDC_WAIT));

    DWORD exitCode = 0;
    if (CreateProcessW(NULL, fullCmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi))
    {
        WaitForSingleObject(pi.hProcess, INFINITE);
        GetExitCodeProcess(pi.hProcess, &exitCode);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }
    else
    {
        exitCode = GetLastError();
    }
    HeapFree(GetProcessHeap(), 0, fullCmd);
    return (int)exitCode;
}
