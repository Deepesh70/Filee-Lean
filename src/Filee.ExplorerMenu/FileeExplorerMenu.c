// "Convert with Filee" in the top-level Windows 11 File Explorer context menu: a tiny in-process COM server
// (IClassFactory + IExplorerCommand + IEnumExplorerCommand) that Explorer loads through the Filee sparse package. Built with
// build/build-explorer-menu.ps1; the package side lives in src/Filee.Platform.Windows/ExplorerMenu*.cs.
//
// This code runs inside explorer.exe, so it is deliberately small and defensive:
//  - no C runtime (linked with -nostdlib): only kernel32/user32/ole32/shell32/shlwapi are used;
//  - menu construction (GetTitle/GetState) only reads one tiny local file and shell attributes;
//  - dynamic submenus categorized by file type (Images, Documents, Spreadsheets, PDF, Data);
//  - Invoke starts filee-cli.exe next to this DLL silently and returns immediately (no waiting, no resident RAM).

#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define CINTERFACE
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>

// {6DB0E670-807F-4D22-BE3F-20492D0F4AB1}; must match ExplorerMenuPackage.ClassId (C#) and the package manifest.
static const CLSID kClsidCommand = {0x6db0e670, 0x807f, 0x4d22, {0xbe, 0x3f, 0x20, 0x49, 0x2d, 0x0f, 0x4a, 0xb1}};
// Local copies of the interface and folder ids, so the DLL does not need libuuid.
static const IID kIidUnknown = {0x00000000, 0x0000, 0x0000, {0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};
static const IID kIidClassFactory = {0x00000001, 0x0000, 0x0000, {0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};
static const IID kIidExplorerCommand = {0xa08ce4d0, 0xfa25, 0x44ab, {0xb5, 0x7c, 0xc7, 0xb1, 0xc3, 0x23, 0xe0, 0xb9}};
static const IID kIidEnumExplorerCommand = {0xa8882650, 0x02b9, 0x49e5, {0x9f, 0x45, 0xd6, 0x47, 0x22, 0xbb, 0x4e, 0x79}};
static const IID kIidObjectWithSelection = {0x403666b6, 0xa67b, 0x4026, {0x9f, 0x70, 0x4f, 0x51, 0xe0, 0x6f, 0xa7, 0x04}};
static const GUID kGuidNull = {0};
static const KNOWNFOLDERID kFolderRoamingAppData = {0x3eb685db, 0x65f9, 0x4cf6, {0xa0, 0x3a, 0xe3, 0xef, 0x65, 0x72, 0x9f, 0x3d}};

#define DEFAULT_TITLE L"Convert with Filee"
// Written by the app into its data folder (%APPDATA%\Filee) while the Explorer menu setting is on.
#define TITLE_FILE L"Filee\\explorer-menu.txt"
#define CLI_EXE L"filee-cli.exe"
#define APP_EXE L"Filee.exe"
#define LIST_PREFIX L"Filee-convert-"
// CreateProcess accepts at most 32767 characters; longer selections go through an @file list.
#define MAX_COMMAND_LINE 32000

#define CAT_NONE        0
#define CAT_IMAGE       (1 << 0) // .png, .jpg, .jpeg, .webp, .bmp, .gif, .svg
#define CAT_DOC         (1 << 1) // .md, .markdown, .txt
#define CAT_SHEET       (1 << 2) // .xlsx, .xls, .ods, .csv
#define CAT_PDF         (1 << 3) // .pdf
#define CAT_DATA        (1 << 4) // .json, .yaml, .yml, .toml, .xml

typedef struct SubCommandDef
{
    const WCHAR *title;
    const WCHAR *targetFormat;
    int category;
} SubCommandDef;

static const SubCommandDef kSubCommands[] = {
    // Images
    { L"JPEG Image (.jpg)",        L"jpg",  CAT_IMAGE },
    { L"PNG Image (.png)",         L"png",  CAT_IMAGE },
    { L"WebP Image (.webp)",       L"webp", CAT_IMAGE },
    { L"PDF Document (.pdf)",      L"pdf",  CAT_IMAGE },

    // Documents
    { L"PDF Document (.pdf)",      L"pdf",  CAT_DOC },
    { L"Word Document (.docx)",    L"docx", CAT_DOC },
    { L"HTML Web Page (.html)",    L"html", CAT_DOC },

    // Spreadsheets
    { L"CSV Spreadsheet (.csv)",   L"csv",  CAT_SHEET },
    { L"Excel Workbook (.xlsx)",   L"xlsx", CAT_SHEET },

    // PDF
    { L"Images (.png)",            L"png",  CAT_PDF },
    { L"Word Document (.docx)",    L"docx", CAT_PDF },
    { L"Markdown Document (.md)",  L"md",   CAT_PDF },
    { L"Plain Text (.txt)",        L"txt",  CAT_PDF },

    // Data
    { L"JSON (.json)",             L"json", CAT_DATA },
    { L"YAML (.yaml)",             L"yaml", CAT_DATA },
    { L"TOML (.toml)",             L"toml", CAT_DATA },
    { L"CSV (.csv)",               L"csv",  CAT_DATA },
};

static HMODULE g_module;
static volatile LONG g_locks; // live objects + class factory references + LockServer calls

static BOOL SameGuid(const GUID *a, const GUID *b)
{
    const BYTE *x = (const BYTE *)a;
    const BYTE *y = (const BYTE *)b;
    for (int i = 0; i < (int)sizeof(GUID); i++)
    {
        if (x[i] != y[i])
            return FALSE;
    }
    return TRUE;
}

// ---- Small helpers (no CRT) ------------------------------------------------------------------------------------

/// Growable UTF-16 buffer on the process heap.
typedef struct Text
{
    WCHAR *data;
    SIZE_T length;
    SIZE_T capacity;
    BOOL failed;
} Text;

static void TextAppendN(Text *text, const WCHAR *value, SIZE_T count)
{
    if (text->failed)
        return;
    if (text->length + count + 1 > text->capacity)
    {
        SIZE_T capacity = text->capacity ? text->capacity : 1024;
        while (text->length + count + 1 > capacity)
            capacity *= 2;
        WCHAR *grown = text->data
                           ? (WCHAR *)HeapReAlloc(GetProcessHeap(), 0, text->data, capacity * sizeof(WCHAR))
                           : (WCHAR *)HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(WCHAR));
        if (!grown)
        {
            text->failed = TRUE;
            return;
        }
        text->data = grown;
        text->capacity = capacity;
    }
    for (SIZE_T i = 0; i < count; i++)
        text->data[text->length + i] = value[i];
    text->length += count;
    text->data[text->length] = 0;
}

static void TextAppend(Text *text, const WCHAR *value)
{
    TextAppendN(text, value, (SIZE_T)lstrlenW(value));
}

static void TextFree(Text *text)
{
    if (text->data)
        HeapFree(GetProcessHeap(), 0, text->data);
    text->data = NULL;
    text->length = text->capacity = 0;
}

/// Full path of a file next to this DLL (the Filee install folder).
static BOOL PathNextToModule(const WCHAR *name, WCHAR *buffer, DWORD size)
{
    DWORD length = GetModuleFileNameW(g_module, buffer, size);
    if (length == 0 || length >= size)
        return FALSE;
    while (length > 0 && buffer[length - 1] != L'\\')
        length--;
    if (length == 0 || length + (DWORD)lstrlenW(name) >= size)
        return FALSE;
    lstrcpyW(buffer + length, name);
    return TRUE;
}

/// Finds the CLI executable next to this DLL (prefers filee-cli.exe, falls back to Filee.exe).
static BOOL FindCliExecutable(WCHAR *buffer, DWORD size)
{
    if (PathNextToModule(CLI_EXE, buffer, size) && GetFileAttributesW(buffer) != INVALID_FILE_ATTRIBUTES)
        return TRUE;
    if (PathNextToModule(APP_EXE, buffer, size) && GetFileAttributesW(buffer) != INVALID_FILE_ATTRIBUTES)
        return TRUE;
    return PathNextToModule(CLI_EXE, buffer, size);
}

/// Reads the localized title the app writes into its data folder. Returns FALSE when missing or unusable.
static BOOL ReadTitle(WCHAR *title, int size)
{
    PWSTR folder = NULL;
    if (FAILED(SHGetKnownFolderPath(&kFolderRoamingAppData, 0, NULL, &folder)) || !folder)
        return FALSE;
    WCHAR path[1024];
    BOOL fits = lstrlenW(folder) + 1 + lstrlenW(TITLE_FILE) < (int)ARRAYSIZE(path);
    if (fits)
    {
        lstrcpyW(path, folder);
        lstrcatW(path, L"\\");
        lstrcatW(path, TITLE_FILE);
    }
    CoTaskMemFree(folder);
    if (!fits)
        return FALSE;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE)
        return FALSE;
    char bytes[512];
    DWORD read = 0;
    BOOL ok = ReadFile(file, bytes, sizeof(bytes) - 1, &read, NULL);
    CloseHandle(file);
    if (!ok)
        return FALSE;

    int start = 0;
    if (read >= 3 && (BYTE)bytes[0] == 0xEF && (BYTE)bytes[1] == 0xBB && (BYTE)bytes[2] == 0xBF)
        start = 3; // UTF-8 byte order mark
    int end = start;
    while (end < (int)read && bytes[end] != '\r' && bytes[end] != '\n')
        end++;
    if (end == start)
        return FALSE;
    int chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes + start, end - start, title, size - 1);
    if (chars <= 0)
        return FALSE;
    title[chars] = 0;
    while (chars > 0 && (title[chars - 1] == L' ' || title[chars - 1] == L'\t'))
        title[--chars] = 0;
    return chars > 0;
}

/// Category of an extension.
static int GetExtensionCategory(const WCHAR *ext)
{
    if (!ext || !*ext)
        return CAT_NONE;
    if (lstrcmpiW(ext, L".png") == 0 ||
        lstrcmpiW(ext, L".jpg") == 0 ||
        lstrcmpiW(ext, L".jpeg") == 0 ||
        lstrcmpiW(ext, L".webp") == 0 ||
        lstrcmpiW(ext, L".bmp") == 0 ||
        lstrcmpiW(ext, L".gif") == 0 ||
        lstrcmpiW(ext, L".svg") == 0)
    {
        return CAT_IMAGE;
    }
    if (lstrcmpiW(ext, L".md") == 0 ||
        lstrcmpiW(ext, L".markdown") == 0 ||
        lstrcmpiW(ext, L".txt") == 0)
    {
        return CAT_DOC;
    }
    if (lstrcmpiW(ext, L".xlsx") == 0 ||
        lstrcmpiW(ext, L".xls") == 0 ||
        lstrcmpiW(ext, L".ods") == 0 ||
        lstrcmpiW(ext, L".csv") == 0)
    {
        return CAT_SHEET;
    }
    if (lstrcmpiW(ext, L".pdf") == 0)
    {
        return CAT_PDF;
    }
    if (lstrcmpiW(ext, L".json") == 0 ||
        lstrcmpiW(ext, L".yaml") == 0 ||
        lstrcmpiW(ext, L".yml") == 0 ||
        lstrcmpiW(ext, L".toml") == 0 ||
        lstrcmpiW(ext, L".xml") == 0)
    {
        return CAT_DATA;
    }
    return CAT_NONE;
}

/// Category of all selected items. Returns CAT_NONE if empty, mixed, or unsupported.
static int GetSelectionCategory(IShellItemArray *items)
{
    if (!items)
        return CAT_NONE;
    DWORD count = 0;
    if (FAILED(IShellItemArray_GetCount(items, &count)) || count == 0)
        return CAT_NONE;

    int resultCategory = CAT_NONE;
    for (DWORD i = 0; i < count; i++)
    {
        IShellItem *item = NULL;
        if (FAILED(IShellItemArray_GetItemAt(items, i, &item)))
            continue;
        LPWSTR path = NULL;
        if (SUCCEEDED(IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, &path)) && path)
        {
            LPCWSTR ext = PathFindExtensionW(path);
            int cat = GetExtensionCategory(ext);
            CoTaskMemFree(path);
            if (i == 0)
                resultCategory = cat;
            else if (resultCategory != cat)
                resultCategory = CAT_NONE;
        }
        IShellItem_Release(item);
        if (resultCategory == CAT_NONE)
            break;
    }
    return resultCategory;
}

/// Writes the paths (one per line, UTF-8) to a temporary response file.
static BOOL WriteListFile(const Text *lines, WCHAR *path, DWORD size)
{
    WCHAR folder[MAX_PATH + 1];
    DWORD length = GetTempPathW(ARRAYSIZE(folder), folder);
    if (length == 0 || length >= ARRAYSIZE(folder))
        return FALSE;

    int bytes = WideCharToMultiByte(CP_UTF8, 0, lines->data, (int)lines->length, NULL, 0, NULL, NULL);
    if (bytes <= 0)
        return FALSE;
    char *utf8 = (char *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)bytes);
    if (!utf8)
        return FALSE;
    WideCharToMultiByte(CP_UTF8, 0, lines->data, (int)lines->length, utf8, bytes, NULL, NULL);

    BOOL written = FALSE;
    for (int attempt = 0; attempt < 5 && !written; attempt++)
    {
        if (wsprintfW(path, L"%s" LIST_PREFIX L"%lu-%lu-%d.txt", folder, GetCurrentProcessId(), GetTickCount(), attempt) <= 0
            || (DWORD)lstrlenW(path) >= size)
            break;
        HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, NULL);
        if (file == INVALID_HANDLE_VALUE)
            continue;
        DWORD done = 0;
        written = WriteFile(file, utf8, (DWORD)bytes, &done, NULL) && done == (DWORD)bytes;
        CloseHandle(file);
        if (!written)
            DeleteFileW(path);
    }
    HeapFree(GetProcessHeap(), 0, utf8);
    return written;
}

/// Starts filee-cli.exe quietly in the background (no console window).
static HRESULT StartProcess(const WCHAR *exe, WCHAR *commandLine)
{
    WCHAR folder[1024];
    lstrcpynW(folder, exe, ARRAYSIZE(folder));
    for (int i = lstrlenW(folder) - 1; i >= 0 && folder[i] != L'\\'; i--)
        folder[i] = 0;

    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;

    if (!CreateProcessW(NULL, commandLine, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, folder, &startup, &process))
        return HRESULT_FROM_WIN32(GetLastError());

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

/// Invokes filee-cli.exe convert with the given target format and notification toast.
static HRESULT InvokeCli(IShellItemArray *items, const WCHAR *targetFormat)
{
    WCHAR exe[1024];
    if (!FindCliExecutable(exe, ARRAYSIZE(exe)))
        return E_FAIL;

    DWORD count = 0;
    HRESULT hr = IShellItemArray_GetCount(items, &count);
    if (FAILED(hr))
        return hr;

    Text args = {0};
    Text lines = {0};
    TextAppend(&args, L"\"");
    TextAppend(&args, exe);
    TextAppend(&args, L"\" convert");

    DWORD added = 0;
    for (DWORD i = 0; i < count; i++)
    {
        IShellItem *item = NULL;
        if (FAILED(IShellItemArray_GetItemAt(items, i, &item)))
            continue;
        LPWSTR path = NULL;
        if (SUCCEEDED(IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, &path)) && path)
        {
            TextAppend(&args, L" \"");
            TextAppend(&args, path);
            TextAppend(&args, L"\"");
            TextAppend(&lines, path);
            TextAppend(&lines, L"\r\n");
            added++;
            CoTaskMemFree(path);
        }
        IShellItem_Release(item);
    }

    if (added == 0)
    {
        TextFree(&args);
        TextFree(&lines);
        return S_FALSE;
    }

    TextAppend(&args, L" --to ");
    TextAppend(&args, targetFormat);
    TextAppend(&args, L" --quiet --notify");

    if (args.failed || lines.failed)
        hr = E_OUTOFMEMORY;
    else if (args.length < MAX_COMMAND_LINE)
        hr = StartProcess(exe, args.data);
    else
    {
        WCHAR list[MAX_PATH + 64];
        hr = E_FAIL;
        if (WriteListFile(&lines, list, ARRAYSIZE(list)))
        {
            Text command = {0};
            TextAppend(&command, L"\"");
            TextAppend(&command, exe);
            TextAppend(&command, L"\" convert @\"");
            TextAppend(&command, list);
            TextAppend(&command, L"\" --to ");
            TextAppend(&command, targetFormat);
            TextAppend(&command, L" --quiet --notify");
            hr = command.failed ? E_OUTOFMEMORY : StartProcess(exe, command.data);
            if (FAILED(hr))
                DeleteFileW(list);
            TextFree(&command);
        }
    }

    TextFree(&args);
    TextFree(&lines);
    return hr;
}

// ---- SubCommand (IExplorerCommand for cascading items) ----------------------------------------------------------

typedef struct SubCommand
{
    IExplorerCommand iface;
    volatile LONG refs;
    const WCHAR *title;
    const WCHAR *targetFormat;
    int categoryMask;
} SubCommand;

static HRESULT STDMETHODCALLTYPE SubCommand_QueryInterface(IExplorerCommand *self, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    if (SameGuid(riid, &kIidUnknown) || SameGuid(riid, &kIidExplorerCommand))
    {
        *result = self;
        IExplorerCommand_AddRef(self);
        return S_OK;
    }
    *result = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE SubCommand_AddRef(IExplorerCommand *self)
{
    return (ULONG)InterlockedIncrement(&((SubCommand *)self)->refs);
}

static ULONG STDMETHODCALLTYPE SubCommand_Release(IExplorerCommand *self)
{
    LONG refs = InterlockedDecrement(&((SubCommand *)self)->refs);
    if (refs == 0)
    {
        HeapFree(GetProcessHeap(), 0, self);
        InterlockedDecrement(&g_locks);
    }
    return (ULONG)refs;
}

static HRESULT STDMETHODCALLTYPE SubCommand_GetTitle(IExplorerCommand *self, IShellItemArray *items, LPWSTR *name)
{
    (void)items;
    if (!name)
        return E_POINTER;
    SubCommand *sub = (SubCommand *)self;
    return SHStrDupW(sub->title, name);
}

static HRESULT STDMETHODCALLTYPE SubCommand_GetIcon(IExplorerCommand *self, IShellItemArray *items, LPWSTR *icon)
{
    (void)self;
    (void)items;
    if (!icon)
        return E_POINTER;
    *icon = NULL;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE SubCommand_GetToolTip(IExplorerCommand *self, IShellItemArray *items, LPWSTR *tip)
{
    (void)self;
    (void)items;
    if (tip)
        *tip = NULL;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE SubCommand_GetCanonicalName(IExplorerCommand *self, GUID *name)
{
    (void)self;
    if (!name)
        return E_POINTER;
    *name = kGuidNull;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE SubCommand_GetState(IExplorerCommand *self, IShellItemArray *items, BOOL okToBeSlow,
                                                     EXPCMDSTATE *state)
{
    (void)okToBeSlow;
    if (!state)
        return E_POINTER;
    *state = ECS_HIDDEN;
    SubCommand *sub = (SubCommand *)self;
    int cat = GetSelectionCategory(items);
    if (cat != CAT_NONE && (cat & sub->categoryMask) != 0)
        *state = ECS_ENABLED;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE SubCommand_Invoke(IExplorerCommand *self, IShellItemArray *items, IBindCtx *context)
{
    (void)context;
    if (!items)
        return E_INVALIDARG;
    SubCommand *sub = (SubCommand *)self;
    return InvokeCli(items, sub->targetFormat);
}

static HRESULT STDMETHODCALLTYPE SubCommand_GetFlags(IExplorerCommand *self, EXPCMDFLAGS *flags)
{
    (void)self;
    if (!flags)
        return E_POINTER;
    *flags = ECF_DEFAULT;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE SubCommand_EnumSubCommands(IExplorerCommand *self, IEnumExplorerCommand **commands)
{
    (void)self;
    if (commands)
        *commands = NULL;
    return E_NOTIMPL;
}

static IExplorerCommandVtbl g_subCommandVtbl = {
    SubCommand_QueryInterface,
    SubCommand_AddRef,
    SubCommand_Release,
    SubCommand_GetTitle,
    SubCommand_GetIcon,
    SubCommand_GetToolTip,
    SubCommand_GetCanonicalName,
    SubCommand_GetState,
    SubCommand_Invoke,
    SubCommand_GetFlags,
    SubCommand_EnumSubCommands,
};

static IExplorerCommand *CreateSubCommand(const WCHAR *title, const WCHAR *targetFormat, int categoryMask)
{
    SubCommand *sub = (SubCommand *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(SubCommand));
    if (!sub)
        return NULL;
    sub->iface.lpVtbl = &g_subCommandVtbl;
    sub->refs = 1;
    sub->title = title;
    sub->targetFormat = targetFormat;
    sub->categoryMask = categoryMask;
    InterlockedIncrement(&g_locks);
    return &sub->iface;
}

// ---- CommandEnumerator (IEnumExplorerCommand) ------------------------------------------------------------------

typedef struct CommandEnumerator
{
    IEnumExplorerCommand iface;
    volatile LONG refs;
    ULONG current;
    ULONG count;
    IExplorerCommand **items;
} CommandEnumerator;

static HRESULT STDMETHODCALLTYPE Enum_QueryInterface(IEnumExplorerCommand *self, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    if (SameGuid(riid, &kIidUnknown) || SameGuid(riid, &kIidEnumExplorerCommand))
    {
        *result = self;
        IEnumExplorerCommand_AddRef(self);
        return S_OK;
    }
    *result = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE Enum_AddRef(IEnumExplorerCommand *self)
{
    return (ULONG)InterlockedIncrement(&((CommandEnumerator *)self)->refs);
}

static ULONG STDMETHODCALLTYPE Enum_Release(IEnumExplorerCommand *self)
{
    CommandEnumerator *enumerator = (CommandEnumerator *)self;
    LONG refs = InterlockedDecrement(&enumerator->refs);
    if (refs == 0)
    {
        if (enumerator->items)
        {
            for (ULONG i = 0; i < enumerator->count; i++)
            {
                if (enumerator->items[i])
                    IExplorerCommand_Release(enumerator->items[i]);
            }
            HeapFree(GetProcessHeap(), 0, enumerator->items);
        }
        HeapFree(GetProcessHeap(), 0, enumerator);
        InterlockedDecrement(&g_locks);
    }
    return (ULONG)refs;
}

static HRESULT STDMETHODCALLTYPE Enum_Next(IEnumExplorerCommand *self, ULONG celt, IExplorerCommand **pUICommand,
                                          ULONG *pceltFetched)
{
    CommandEnumerator *enumerator = (CommandEnumerator *)self;
    if (!pUICommand || (celt > 1 && !pceltFetched))
        return E_INVALIDARG;

    ULONG fetched = 0;
    while (enumerator->current < enumerator->count && fetched < celt)
    {
        pUICommand[fetched] = enumerator->items[enumerator->current];
        IExplorerCommand_AddRef(pUICommand[fetched]);
        enumerator->current++;
        fetched++;
    }
    if (pceltFetched)
        *pceltFetched = fetched;
    return fetched == celt ? S_OK : S_FALSE;
}

static HRESULT STDMETHODCALLTYPE Enum_Skip(IEnumExplorerCommand *self, ULONG celt)
{
    CommandEnumerator *enumerator = (CommandEnumerator *)self;
    if (enumerator->current + celt > enumerator->count)
    {
        enumerator->current = enumerator->count;
        return S_FALSE;
    }
    enumerator->current += celt;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Enum_Reset(IEnumExplorerCommand *self)
{
    CommandEnumerator *enumerator = (CommandEnumerator *)self;
    enumerator->current = 0;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Enum_Clone(IEnumExplorerCommand *self, IEnumExplorerCommand **ppenum)
{
    (void)self;
    if (ppenum)
        *ppenum = NULL;
    return E_NOTIMPL;
}

static IEnumExplorerCommandVtbl g_enumVtbl = {
    Enum_QueryInterface,
    Enum_AddRef,
    Enum_Release,
    Enum_Next,
    Enum_Skip,
    Enum_Reset,
    Enum_Clone,
};

// ---- Top-level Command (IExplorerCommand + IObjectWithSelection) -----------------------------------------------

typedef struct Command
{
    IExplorerCommand iface;
    IObjectWithSelection selectionIface;
    volatile LONG refs;
    IShellItemArray *selection;
    int lastCategory;
} Command;

static HRESULT STDMETHODCALLTYPE Command_QueryInterface(IExplorerCommand *self, REFIID riid, void **result);
static ULONG STDMETHODCALLTYPE Command_AddRef(IExplorerCommand *self);
static ULONG STDMETHODCALLTYPE Command_Release(IExplorerCommand *self);

static HRESULT STDMETHODCALLTYPE Selection_QueryInterface(IObjectWithSelection *self, REFIID riid, void **result)
{
    Command *cmd = CONTAINING_RECORD(self, Command, selectionIface);
    return Command_QueryInterface(&cmd->iface, riid, result);
}

static ULONG STDMETHODCALLTYPE Selection_AddRef(IObjectWithSelection *self)
{
    Command *cmd = CONTAINING_RECORD(self, Command, selectionIface);
    return Command_AddRef(&cmd->iface);
}

static ULONG STDMETHODCALLTYPE Selection_Release(IObjectWithSelection *self)
{
    Command *cmd = CONTAINING_RECORD(self, Command, selectionIface);
    return Command_Release(&cmd->iface);
}

static HRESULT STDMETHODCALLTYPE Selection_SetSelection(IObjectWithSelection *self, IShellItemArray *punk)
{
    Command *cmd = CONTAINING_RECORD(self, Command, selectionIface);
    if (cmd->selection)
    {
        IShellItemArray_Release(cmd->selection);
        cmd->selection = NULL;
    }
    if (punk)
    {
        cmd->selection = punk;
        IShellItemArray_AddRef(cmd->selection);
        cmd->lastCategory = GetSelectionCategory(cmd->selection);
    }
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Selection_GetSelection(IObjectWithSelection *self, REFIID riid, void **ppv)
{
    Command *cmd = CONTAINING_RECORD(self, Command, selectionIface);
    if (!cmd->selection)
        return E_FAIL;
    return IShellItemArray_QueryInterface(cmd->selection, riid, ppv);
}

static IObjectWithSelectionVtbl g_selectionVtbl = {
    Selection_QueryInterface,
    Selection_AddRef,
    Selection_Release,
    Selection_SetSelection,
    Selection_GetSelection,
};

static HRESULT STDMETHODCALLTYPE Command_QueryInterface(IExplorerCommand *self, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    if (SameGuid(riid, &kIidUnknown) || SameGuid(riid, &kIidExplorerCommand))
    {
        *result = self;
        IExplorerCommand_AddRef(self);
        return S_OK;
    }
    if (SameGuid(riid, &kIidObjectWithSelection))
    {
        Command *cmd = (Command *)self;
        *result = &cmd->selectionIface;
        IExplorerCommand_AddRef(self);
        return S_OK;
    }
    *result = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE Command_AddRef(IExplorerCommand *self)
{
    return (ULONG)InterlockedIncrement(&((Command *)self)->refs);
}

static ULONG STDMETHODCALLTYPE Command_Release(IExplorerCommand *self)
{
    Command *cmd = (Command *)self;
    LONG refs = InterlockedDecrement(&cmd->refs);
    if (refs == 0)
    {
        if (cmd->selection)
        {
            IShellItemArray_Release(cmd->selection);
            cmd->selection = NULL;
        }
        HeapFree(GetProcessHeap(), 0, cmd);
        InterlockedDecrement(&g_locks);
    }
    return (ULONG)refs;
}

static HRESULT STDMETHODCALLTYPE Command_GetTitle(IExplorerCommand *self, IShellItemArray *items, LPWSTR *name)
{
    (void)self;
    (void)items;
    if (!name)
        return E_POINTER;
    *name = NULL;
    WCHAR title[128];
    if (!ReadTitle(title, ARRAYSIZE(title)))
        lstrcpyW(title, DEFAULT_TITLE);
    return SHStrDupW(title, name);
}

static HRESULT STDMETHODCALLTYPE Command_GetIcon(IExplorerCommand *self, IShellItemArray *items, LPWSTR *icon)
{
    (void)self;
    (void)items;
    if (!icon)
        return E_POINTER;
    *icon = NULL;
    WCHAR path[1024];
    if (!PathNextToModule(APP_EXE L",0", path, ARRAYSIZE(path)))
    {
        if (!PathNextToModule(CLI_EXE L",0", path, ARRAYSIZE(path)))
            return E_FAIL;
    }
    return SHStrDupW(path, icon);
}

static HRESULT STDMETHODCALLTYPE Command_GetToolTip(IExplorerCommand *self, IShellItemArray *items, LPWSTR *tip)
{
    (void)self;
    (void)items;
    if (tip)
        *tip = NULL;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE Command_GetCanonicalName(IExplorerCommand *self, GUID *name)
{
    (void)self;
    if (!name)
        return E_POINTER;
    *name = kClsidCommand;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Command_GetState(IExplorerCommand *self, IShellItemArray *items, BOOL okToBeSlow,
                                                  EXPCMDSTATE *state)
{
    (void)okToBeSlow;
    if (!state)
        return E_POINTER;
    *state = ECS_HIDDEN;
    WCHAR title[128];
    if (!ReadTitle(title, ARRAYSIZE(title)))
        return S_OK;

    const SFGAOF wanted = SFGAO_FILESYSTEM | SFGAO_STREAM;
    SFGAOF attributes = 0;
    if (items && SUCCEEDED(IShellItemArray_GetAttributes(items, SIATTRIBFLAGS_AND, wanted, &attributes))
        && (attributes & wanted) == wanted)
    {
        int cat = GetSelectionCategory(items);
        if (cat != CAT_NONE)
        {
            Command *cmd = (Command *)self;
            cmd->lastCategory = cat;
            *state = ECS_ENABLED;
        }
    }
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Command_Invoke(IExplorerCommand *self, IShellItemArray *items, IBindCtx *context)
{
    (void)context;
    if (!items)
        return E_INVALIDARG;
    Command *cmd = (Command *)self;
    int cat = cmd->lastCategory;
    if (cat == CAT_NONE)
        cat = GetSelectionCategory(items);

    const WCHAR *defFormat = L"webp";
    switch (cat)
    {
        case CAT_IMAGE: defFormat = L"webp"; break;
        case CAT_DOC:   defFormat = L"pdf";  break;
        case CAT_SHEET: defFormat = L"csv";  break;
        case CAT_PDF:   defFormat = L"png";  break;
        case CAT_DATA:  defFormat = L"json"; break;
        default:        return S_OK;
    }
    return InvokeCli(items, defFormat);
}

static HRESULT STDMETHODCALLTYPE Command_GetFlags(IExplorerCommand *self, EXPCMDFLAGS *flags)
{
    (void)self;
    if (!flags)
        return E_POINTER;
    *flags = ECF_HASSUBCOMMANDS;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Command_EnumSubCommands(IExplorerCommand *self, IEnumExplorerCommand **commands)
{
    if (!commands)
        return E_POINTER;
    *commands = NULL;

    Command *cmd = (Command *)self;
    int cat = cmd->lastCategory;
    if (cat == CAT_NONE && cmd->selection)
        cat = GetSelectionCategory(cmd->selection);

    ULONG matchingCount = 0;
    for (size_t i = 0; i < ARRAYSIZE(kSubCommands); i++)
    {
        if (cat == CAT_NONE || (kSubCommands[i].category & cat) != 0)
            matchingCount++;
    }

    if (matchingCount == 0)
        return S_FALSE;

    IExplorerCommand **items = (IExplorerCommand **)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, matchingCount * sizeof(IExplorerCommand *));
    if (!items)
        return E_OUTOFMEMORY;

    ULONG idx = 0;
    for (size_t i = 0; i < ARRAYSIZE(kSubCommands); i++)
    {
        if (cat == CAT_NONE || (kSubCommands[i].category & cat) != 0)
        {
            IExplorerCommand *sub = CreateSubCommand(kSubCommands[i].title, kSubCommands[i].targetFormat, kSubCommands[i].category);
            if (!sub)
            {
                for (ULONG j = 0; j < idx; j++)
                    IExplorerCommand_Release(items[j]);
                HeapFree(GetProcessHeap(), 0, items);
                return E_OUTOFMEMORY;
            }
            items[idx++] = sub;
        }
    }

    CommandEnumerator *enumerator = (CommandEnumerator *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(CommandEnumerator));
    if (!enumerator)
    {
        for (ULONG j = 0; j < idx; j++)
            IExplorerCommand_Release(items[j]);
        HeapFree(GetProcessHeap(), 0, items);
        return E_OUTOFMEMORY;
    }

    enumerator->iface.lpVtbl = &g_enumVtbl;
    enumerator->refs = 1;
    enumerator->current = 0;
    enumerator->count = idx;
    enumerator->items = items;
    InterlockedIncrement(&g_locks);

    *commands = &enumerator->iface;
    return S_OK;
}

static IExplorerCommandVtbl g_commandVtbl = {
    Command_QueryInterface,
    Command_AddRef,
    Command_Release,
    Command_GetTitle,
    Command_GetIcon,
    Command_GetToolTip,
    Command_GetCanonicalName,
    Command_GetState,
    Command_Invoke,
    Command_GetFlags,
    Command_EnumSubCommands,
};

// ---- IClassFactory (a single static instance) ------------------------------------------------------------------

static HRESULT STDMETHODCALLTYPE Factory_QueryInterface(IClassFactory *self, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    if (SameGuid(riid, &kIidUnknown) || SameGuid(riid, &kIidClassFactory))
    {
        *result = self;
        IClassFactory_AddRef(self);
        return S_OK;
    }
    *result = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE Factory_AddRef(IClassFactory *self)
{
    (void)self;
    return (ULONG)InterlockedIncrement(&g_locks);
}

static ULONG STDMETHODCALLTYPE Factory_Release(IClassFactory *self)
{
    (void)self;
    return (ULONG)InterlockedDecrement(&g_locks);
}

static HRESULT STDMETHODCALLTYPE Factory_CreateInstance(IClassFactory *self, IUnknown *outer, REFIID riid, void **result)
{
    (void)self;
    if (!result)
        return E_POINTER;
    *result = NULL;
    if (outer)
        return CLASS_E_NOAGGREGATION;
    Command *command = (Command *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Command));
    if (!command)
        return E_OUTOFMEMORY;
    command->iface.lpVtbl = &g_commandVtbl;
    command->selectionIface.lpVtbl = &g_selectionVtbl;
    command->refs = 1;
    InterlockedIncrement(&g_locks);
    HRESULT hr = Command_QueryInterface(&command->iface, riid, result);
    Command_Release(&command->iface);
    return hr;
}

static HRESULT STDMETHODCALLTYPE Factory_LockServer(IClassFactory *self, BOOL lock)
{
    (void)self;
    if (lock)
        InterlockedIncrement(&g_locks);
    else
        InterlockedDecrement(&g_locks);
    return S_OK;
}

static IClassFactoryVtbl g_factoryVtbl = {
    Factory_QueryInterface,
    Factory_AddRef,
    Factory_Release,
    Factory_CreateInstance,
    Factory_LockServer,
};
static IClassFactory g_factory = {&g_factoryVtbl};

// ---- DLL exports -----------------------------------------------------------------------------------------------

HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void **result)
{
    if (!result)
        return E_POINTER;
    *result = NULL;
    if (!SameGuid(clsid, &kClsidCommand))
        return CLASS_E_CLASSNOTAVAILABLE;
    return Factory_QueryInterface(&g_factory, riid, result);
}

HRESULT __stdcall DllCanUnloadNow(void)
{
    return g_locks == 0 ? S_OK : S_FALSE;
}

// Entry point (there is no C runtime to initialise).
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
