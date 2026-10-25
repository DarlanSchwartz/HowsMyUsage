#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include <commctrl.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <setupapi.h>
#include <filesystem>
#include <fstream>
#include <thread>
#include <string>
#include <vector>
#include <algorithm>
#include <cwctype>
using std::min;
using std::max;
#include <gdiplus.h>

namespace fs = std::filesystem;
constexpr COLORREF Background = RGB(11, 14, 19), Surface = RGB(28, 32, 39), Text = RGB(241, 245, 249);
constexpr UINT Progress = WM_APP + 1, Complete = WM_APP + 2;
enum class Stage { Ready, Installing, Done, Failed };

class Installation {
    HWND window;
    fs::path destination;
    unsigned total = 0, extracted = 0;
    static UINT CALLBACK Extract(PVOID context, UINT message, UINT_PTR parameter, UINT_PTR) {
        auto self = static_cast<Installation*>(context);
        if (message == SPFILENOTIFY_FILEINCABINET) {
            auto file = reinterpret_cast<FILE_IN_CABINET_INFO_W*>(parameter);
            fs::path relative(file->NameInCabinet);
            if (relative.is_absolute() || relative.has_root_name()) return FILEOP_ABORT;
            for (const auto& part : relative) if (part == L"..") return FILEOP_ABORT;
            auto target = self->destination / relative;
            if (target.native().size() >= MAX_PATH) return FILEOP_ABORT;
            try { Validate(target); } catch (...) { return FILEOP_ABORT; }
            std::error_code error;
            fs::create_directories(target.parent_path(), error);
            if (error) return FILEOP_ABORT;
            wcscpy_s(file->FullTargetName, target.c_str());
            return FILEOP_DOIT;
        }
        if (message == SPFILENOTIFY_FILEEXTRACTED) {
            auto file = reinterpret_cast<FILEPATHS_W*>(parameter);
            if (file->Win32Error) return FILEOP_ABORT;
            PostMessageW(self->window, Progress, ++self->extracted * 100 / std::max(1u, self->total), 0);
        }
        if (message == SPFILENOTIFY_NEEDNEWCABINET) return ERROR_CANCELLED;
        return NO_ERROR;
    }
    static UINT CALLBACK Count(PVOID context, UINT message, UINT_PTR, UINT_PTR) {
        if (message == SPFILENOTIFY_FILEINCABINET) { ++*static_cast<unsigned*>(context); return FILEOP_SKIP; }
        return NO_ERROR;
    }
    static void Register(const wchar_t* key, const wchar_t* name, const std::wstring& value) {
        if (RegSetKeyValueW(HKEY_CURRENT_USER, key, name, REG_SZ, value.c_str(), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t))) != ERROR_SUCCESS)
            throw std::runtime_error("Could not save installation settings.");
    }
public:
    Installation(HWND owner, fs::path folder) : window(owner), destination(std::move(folder)) {}
    void CreateDesktopShortcut() {
        HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        IShellLinkW* link = nullptr;
        HRESULT result = CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link));
        if (SUCCEEDED(result)) {
            link->SetPath((destination / L"Usage.exe").c_str());
            link->SetWorkingDirectory(destination.c_str());
            link->SetIconLocation((destination / L"Usage.exe").c_str(), 0);
            link->SetDescription(L"HowsMyUsage");
            IPersistFile* file = nullptr;
            result = link->QueryInterface(IID_PPV_ARGS(&file));
            if (SUCCEEDED(result)) {
                PWSTR desktop = nullptr;
                result = SHGetKnownFolderPath(FOLDERID_Desktop, 0, nullptr, &desktop);
                if (SUCCEEDED(result)) result = file->Save((fs::path(desktop) / L"HowsMyUsage.lnk").c_str(), TRUE);
                CoTaskMemFree(desktop);
                file->Release();
            }
            link->Release();
        }
        if (SUCCEEDED(initialized)) CoUninitialize();
        if (FAILED(result)) throw std::runtime_error("App installed, but Desktop shortcut creation failed. Retry without that option.");
    }
    static fs::path Validate(const std::wstring& value) {
        if (value.empty()) throw std::runtime_error("Choose an installation folder.");
        fs::path path = fs::absolute(fs::path(value)).lexically_normal();
        auto root = path.root_name().wstring();
        std::transform(root.begin(), root.end(), root.begin(), towupper);
        if (root.size() != 2 || root[1] != L':' || path == path.root_path())
            throw std::runtime_error("Choose an application folder, not the root of a drive.");
        if (path.native().size() > 160) throw std::runtime_error("Choose a shorter installation path.");
        // Avoid overwriting files through an unexpected junction or symbolic link.
        for (auto parent = path; !parent.empty(); parent = parent.parent_path()) {
            DWORD attributes = GetFileAttributesW(parent.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
                throw std::runtime_error("Choose a folder without junctions or symbolic links.");
            if (parent == parent.root_path()) break;
        }
        return path;
    }
    void Run(bool startup, bool desktopShortcut) {
        HANDLE mutex = OpenMutexW(SYNCHRONIZE, FALSE, L"Usage.NativeTray");
        if (mutex) { CloseHandle(mutex); throw std::runtime_error("Exit HowsMyUsage from the tray, then try again."); }
        fs::create_directories(destination);
        // Cabinet scratch data stays on the destination drive, never in Windows TEMP.
        GUID id; CoCreateGuid(&id);
        wchar_t unique[40]; StringFromGUID2(id, unique, 40);
        auto scratch = destination / (std::wstring(L".setup-") + unique);
        fs::create_directory(scratch);
        struct Cleanup { fs::path folder; ~Cleanup() { std::error_code error; fs::remove_all(folder, error); } } cleanup{scratch};
        auto resource = FindResourceW(nullptr, MAKEINTRESOURCEW(101), RT_RCDATA);
        auto data = LockResource(LoadResource(nullptr, resource));
        DWORD bytes = SizeofResource(nullptr, resource);
        if (!data || !bytes) throw std::runtime_error("The installer payload is missing.");
        auto cabinet = scratch / L"app.cab";
        std::ofstream stream(cabinet, std::ios::binary);
        stream.write(static_cast<const char*>(data), bytes); stream.close();
        if (!stream) throw std::runtime_error("Could not write installation files.");
        if (!SetupIterateCabinetW(cabinet.c_str(), 0, Count, &total) || !total)
            throw std::runtime_error("Could not read the installer payload.");
        if (!SetupIterateCabinetW(cabinet.c_str(), 0, Extract, this))
            throw std::runtime_error("Could not install all files. Check free space and folder permissions, then retry.");
        const auto folder = destination.wstring();
        Register(L"Software\\HowsMyUsage", L"InstallDir", folder);
        const wchar_t* uninstall = L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\HowsMyUsage";
        Register(uninstall, L"DisplayName", L"HowsMyUsage");
        Register(uninstall, L"Publisher", L"Darlan Schwartz");
        Register(uninstall, L"DisplayVersion", SETUP_VERSION);
        Register(uninstall, L"InstallLocation", folder);
        Register(uninstall, L"DisplayIcon", folder + L"\\Usage.exe,0");
        wchar_t system[MAX_PATH]; GetSystemDirectoryW(system, MAX_PATH);
        auto powershell = std::wstring(system) + L"\\WindowsPowerShell\\v1.0\\powershell.exe";
        Register(uninstall, L"UninstallString", L"\"" + powershell + L"\" -NoProfile -ExecutionPolicy Bypass -File \"" + folder + L"\\Uninstall.ps1\"");
        const wchar_t* run = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        if (startup) Register(run, L"UsageWidget", L"\"" + powershell + L"\" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + folder + L"\\startup.ps1\"");
        if (!startup) {
            wchar_t existing[4096]; DWORD length = sizeof(existing);
            if (RegGetValueW(HKEY_CURRENT_USER, run, L"UsageWidget", RRF_RT_REG_SZ, nullptr, existing, &length) == ERROR_SUCCESS &&
                std::wstring(existing).find(folder + L"\\startup.ps1") != std::wstring::npos)
                RegDeleteKeyValueW(HKEY_CURRENT_USER, run, L"UsageWidget");
        }
        if (desktopShortcut) CreateDesktopShortcut();
    }
};

class SetupWindow {
    HWND window = nullptr, path = nullptr, browse = nullptr, startup = nullptr, shortcut = nullptr, action = nullptr;
    HFONT font = nullptr;
    HBRUSH background = CreateSolidBrush(Background), surface = CreateSolidBrush(Surface);
    Stage stage = Stage::Ready;
    int percent = 0, dpi = 96;
    std::wstring installedFolder, error;
    std::wstring initialFolder;
    bool autoInstall = false;
    int S(int value) const { return MulDiv(value, dpi, 96); }
    HWND Control(const wchar_t* type, const wchar_t* text, DWORD style, int id, int x, int y, int w, int h) {
        HWND control = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | style,
            S(x), S(y), S(w), S(h), window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), nullptr, nullptr);
        SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        return control;
    }
    static std::wstring DefaultFolder() {
        wchar_t previous[MAX_PATH]; DWORD size = sizeof(previous);
        if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\HowsMyUsage", L"InstallDir", RRF_RT_REG_SZ, nullptr, previous, &size) == ERROR_SUCCESS)
            return previous;
        PWSTR local = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) return L"";
        auto folder = fs::path(local) / L"Programs" / L"HowsMyUsage";
        CoTaskMemFree(local);
        return folder.wstring();
    }
    void ChooseFolder() {
        IFileOpenDialog* picker = nullptr;
        if (FAILED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&picker)))) return;
        picker->SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
        picker->SetTitle(L"Choose installation folder");
        if (SUCCEEDED(picker->Show(window))) {
            IShellItem* item = nullptr;
            if (SUCCEEDED(picker->GetResult(&item))) {
                PWSTR folder = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &folder))) {
                    SetWindowTextW(path, (fs::path(folder) / L"HowsMyUsage").c_str()); CoTaskMemFree(folder);
                }
                item->Release();
            }
        }
        picker->Release();
    }
    void Begin() {
        wchar_t value[1024]; GetWindowTextW(path, value, 1024);
        try { installedFolder = Installation::Validate(value).wstring(); }
        catch (const std::exception& e) { ShowError(e.what()); return; }
        stage = Stage::Installing; error.clear();
        for (HWND control : {path, browse, startup, shortcut, action}) ShowWindow(control, SW_HIDE);
        InvalidateRect(window, nullptr, TRUE);
        const bool start = SendMessageW(startup, BM_GETCHECK, 0, 0) == BST_CHECKED;
        const bool desktopShortcut = SendMessageW(shortcut, BM_GETCHECK, 0, 0) == BST_CHECKED;
        std::thread([owner = window, folder = installedFolder, start, desktopShortcut]() {
            try { Installation(owner, folder).Run(start, desktopShortcut); PostMessageW(owner, Complete, 0, 0); }
            catch (const std::exception& e) {
                const std::string text = e.what();
                PostMessageW(owner, Complete, 1, reinterpret_cast<LPARAM>(new std::wstring(text.begin(), text.end())));
            }
        }).detach();
    }
    void ShowError(const char* text) {
        ShowError(std::wstring(text, text + strlen(text)));
    }
    void ShowError(const std::wstring& text) {
        error = text; stage = Stage::Failed;
        SetWindowTextW(action, L"Try again");
        for (HWND control : {path, browse, startup, shortcut, action}) ShowWindow(control, SW_SHOW);
        InvalidateRect(window, nullptr, TRUE);
    }
    void Paint() {
        PAINTSTRUCT paint; HDC dc = BeginPaint(window, &paint);
        Gdiplus::Graphics g(dc); g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        g.ScaleTransform(dpi / 96.0f, dpi / 96.0f);
        g.Clear(Gdiplus::Color(255, 11, 14, 19));
        Gdiplus::SolidBrush card(Gdiplus::Color(255, 18, 23, 30)), white(Gdiplus::Color(255, 241, 245, 249));
        g.FillRectangle(&card, 0, 0, 230, 390);
        HICON icon = static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(1), IMAGE_ICON, S(92), S(92), 0));
        DrawIconEx(dc, S(69), S(98), icon, S(92), S(92), 0, nullptr, DI_NORMAL); DestroyIcon(icon);
        Gdiplus::FontFamily family(L"Segoe UI");
        Gdiplus::Font brand(&family, 21, Gdiplus::FontStyleBold, Gdiplus::UnitPixel);
        Gdiplus::StringFormat center; center.SetAlignment(Gdiplus::StringAlignmentCenter);
        g.DrawString(L"HowsMyUsage", -1, &brand, Gdiplus::RectF(10, 207, 210, 40), &center, &white);
        Gdiplus::Font title(&family, 27, Gdiplus::FontStyleBold, Gdiplus::UnitPixel);
        const wchar_t* heading = stage == Stage::Done ? L"Ready" : stage == Stage::Installing ? L"Installing" : L"Install";
        g.DrawString(heading, -1, &title, Gdiplus::PointF(268, 54), &white);
        if (stage == Stage::Ready || stage == Stage::Failed) {
            Gdiplus::SolidBrush field(Gdiplus::Color(255, 28, 32, 39)), muted(Gdiplus::Color(255, 163, 174, 188));
            g.FillRectangle(&field, 270, 128, 277, 44);
            Gdiplus::Font body(&family, 14, Gdiplus::FontStyleRegular, Gdiplus::UnitPixel);
            g.DrawString(L"App files and settings will be saved here.", -1, &body,
                Gdiplus::RectF(270, 184, 365, 38), nullptr, &muted);
        }
        if (stage == Stage::Installing) {
            Gdiplus::SolidBrush track(Gdiplus::Color(255, 28, 32, 39)), accent(Gdiplus::Color(255, 87, 214, 180));
            g.FillRectangle(&track, 270, 163, 365, 6);
            g.FillRectangle(&accent, 270, 163, 365 * percent / 100, 6);
            Gdiplus::Font number(&family, 18, Gdiplus::FontStyleRegular, Gdiplus::UnitPixel);
            g.DrawString((std::to_wstring(percent) + L"%").c_str(), -1, &number, Gdiplus::PointF(270, 192), &white);
        }
        if (!error.empty()) {
            Gdiplus::SolidBrush red(Gdiplus::Color(255, 251, 146, 146));
            Gdiplus::Font body(&family, 13, Gdiplus::FontStyleRegular, Gdiplus::UnitPixel);
            g.DrawString(error.c_str(), -1, &body, Gdiplus::RectF(270, 90, 365, 32), nullptr, &red);
        }
        EndPaint(window, &paint);
    }
    void DrawButton(DRAWITEMSTRUCT* item) {
        HDC dc = item->hDC;
        const bool primary = item->CtlID == 3;
        HBRUSH brush = CreateSolidBrush(primary ? RGB(87, 214, 180) : Surface);
        FillRect(dc, &item->rcItem, background);
        auto oldBrush = SelectObject(dc, brush); auto oldPen = SelectObject(dc, GetStockObject(NULL_PEN));
        RoundRect(dc, 0, 0, item->rcItem.right, item->rcItem.bottom, S(12), S(12));
        SelectObject(dc, oldBrush); SelectObject(dc, oldPen); DeleteObject(brush);
        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, primary ? Background : Text);
        auto oldFont = SelectObject(dc, font); wchar_t text[100]; GetWindowTextW(item->hwndItem, text, 100);
        DrawTextW(dc, text, -1, &item->rcItem, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        SelectObject(dc, oldFont);
        if (item->itemState & ODS_FOCUS) { RECT rect = item->rcItem; InflateRect(&rect, -S(4), -S(4)); DrawFocusRect(dc, &rect); }
    }
    static LRESULT CALLBACK Checkbox(HWND control, UINT message, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR data) {
        auto self = reinterpret_cast<SetupWindow*>(data);
        if (message == WM_NCDESTROY) RemoveWindowSubclass(control, Checkbox, id);
        if (message == WM_ERASEBKGND) return 1;
        if (message != WM_PAINT) return DefSubclassProc(control, message, w, l);
        PAINTSTRUCT paint; HDC dc = BeginPaint(control, &paint);
        RECT bounds; GetClientRect(control, &bounds); FillRect(dc, &bounds, self->background);
        Gdiplus::Graphics g(dc); g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        float size = static_cast<float>(self->S(22)), y = (bounds.bottom - size) / 2;
        bool checked = SendMessageW(control, BM_GETCHECK, 0, 0) == BST_CHECKED;
        Gdiplus::SolidBrush fill(checked ? Gdiplus::Color(255, 87, 214, 180) : Gdiplus::Color(255, 28, 32, 39));
        g.FillRectangle(&fill, 0.0f, y, size, size);
        if (checked) {
            Gdiplus::Pen check(Gdiplus::Color(255, 11, 14, 19), static_cast<float>(self->S(2)));
            g.DrawLine(&check, size * .23f, y + size * .5f, size * .43f, y + size * .7f);
            g.DrawLine(&check, size * .43f, y + size * .7f, size * .8f, y + size * .28f);
        }
        SelectObject(dc, self->font); SetBkMode(dc, TRANSPARENT); SetTextColor(dc, Text);
        bounds.left = self->S(34);
        wchar_t label[100]; GetWindowTextW(control, label, 100);
        DrawTextW(dc, label, -1, &bounds, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        if (GetFocus() == control) { InflateRect(&bounds, -2, -2); DrawFocusRect(dc, &bounds); }
        EndPaint(control, &paint); return 0;
    }
    LRESULT Handle(UINT message, WPARAM w, LPARAM l) {
        switch (message) {
        case WM_CREATE: {
            dpi = GetDpiForWindow(window);
            font = CreateFontW(-S(16), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
            path = Control(L"EDIT", (initialFolder.empty() ? DefaultFolder() : initialFolder).c_str(), ES_AUTOHSCROLL, 1, 282, 140, 253, 20);
            browse = Control(L"BUTTON", L"Browse", BS_OWNERDRAW, 2, 557, 128, 78, 44);
            startup = Control(L"BUTTON", L"Start with Windows", BS_AUTOCHECKBOX, 5, 270, 231, 365, 32);
            SetWindowSubclass(startup, Checkbox, 1, reinterpret_cast<DWORD_PTR>(this));
            shortcut = Control(L"BUTTON", L"Desktop shortcut", BS_AUTOCHECKBOX, 6, 270, 269, 365, 32);
            SetWindowSubclass(shortcut, Checkbox, 1, reinterpret_cast<DWORD_PTR>(this));
            SendMessageW(shortcut, BM_SETCHECK, BST_CHECKED, 0);
            wchar_t existing[4096]; DWORD bytes = sizeof(existing);
            bool enabled = RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", L"UsageWidget", RRF_RT_REG_SZ, nullptr, existing, &bytes) == ERROR_SUCCESS;
            SendMessageW(startup, BM_SETCHECK, enabled ? BST_CHECKED : BST_UNCHECKED, 0);
            action = Control(L"BUTTON", L"Install", BS_OWNERDRAW, 3, 270, 313, 365, 44);
            BOOL dark = TRUE; DwmSetWindowAttribute(window, 20, &dark, sizeof(dark));
            COLORREF caption = Background; DwmSetWindowAttribute(window, 35, &caption, sizeof(caption));
            return 0;
        }
        case WM_PAINT: Paint(); return 0;
        case WM_DRAWITEM: DrawButton(reinterpret_cast<DRAWITEMSTRUCT*>(l)); return TRUE;
        case WM_CTLCOLOREDIT: SetTextColor(reinterpret_cast<HDC>(w), Text); SetBkColor(reinterpret_cast<HDC>(w), Surface); return reinterpret_cast<LRESULT>(surface);
        case WM_CTLCOLORSTATIC:
        case WM_CTLCOLORBTN: SetTextColor(reinterpret_cast<HDC>(w), Text); SetBkColor(reinterpret_cast<HDC>(w), Background); return reinterpret_cast<LRESULT>(background);
        case WM_COMMAND:
            if (HIWORD(w) != BN_CLICKED) return 0;
            if (LOWORD(w) == 2) ChooseFolder();
            if (LOWORD(w) == 3 && stage != Stage::Installing) {
                if (stage != Stage::Done) Begin();
                else {
                    auto exe = fs::path(installedFolder) / L"Usage.exe";
                    ShellExecuteW(window, L"open", exe.c_str(), nullptr, installedFolder.c_str(), SW_SHOWNORMAL);
                    DestroyWindow(window);
                }
            }
            return 0;
        case Progress: percent = static_cast<int>(w); InvalidateRect(window, nullptr, FALSE); return 0;
        case Complete:
            if (w) {
                auto text = reinterpret_cast<std::wstring*>(l); ShowError(*text); delete text; return 0;
            }
            stage = Stage::Done;
            SetWindowTextW(action, L"Open HowsMyUsage");
            ShowWindow(action, SW_SHOW); InvalidateRect(window, nullptr, TRUE); return 0;
        case WM_CLOSE: if (stage != Stage::Installing) DestroyWindow(window); return 0;
        case WM_DESTROY: PostQuitMessage(0); return 0;
        }
        return DefWindowProcW(window, message, w, l);
    }
    static LRESULT CALLBACK Procedure(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
        auto self = reinterpret_cast<SetupWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            self = static_cast<SetupWindow*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);
            self->window = hwnd; SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        return self ? self->Handle(message, w, l) : DefWindowProcW(hwnd, message, w, l);
    }
public:
    SetupWindow(std::wstring folder = L"", bool install = false) : initialFolder(std::move(folder)), autoInstall(install) {}
    ~SetupWindow() { DeleteObject(font); DeleteObject(background); DeleteObject(surface); }
    int Run(HINSTANCE instance) {
        WNDCLASSW type{}; type.hInstance = instance; type.lpszClassName = L"HowsMyUsageSetup";
        type.lpfnWndProc = Procedure; type.hCursor = LoadCursorW(nullptr, IDC_ARROW); type.hbrBackground = background;
        type.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1)); RegisterClassW(&type);
        dpi = GetDpiForSystem(); RECT rect{0, 0, S(680), S(390)};
        DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        AdjustWindowRectExForDpi(&rect, style, FALSE, 0, dpi);
        window = CreateWindowExW(0, type.lpszClassName, L"HowsMyUsage Setup", style, CW_USEDEFAULT, CW_USEDEFAULT,
            rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, instance, this);
        if (!window) return 1;
        ShowWindow(window, SW_SHOW); UpdateWindow(window);
        if (autoInstall) PostMessageW(window, WM_COMMAND, MAKEWPARAM(3, BN_CLICKED), 0);
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            if (IsDialogMessageW(window, &message)) continue;
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        return 0;
    }
};

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Gdiplus::GdiplusStartupInput input; ULONG_PTR token;
    if (Gdiplus::GdiplusStartup(&token, &input, nullptr) != Gdiplus::Ok) return 1;
    int result = 0;
    bool silent = false;
    try {
        int count = 0; auto args = CommandLineToArgvW(GetCommandLineW(), &count);
        std::wstring folder; bool automatic = false, startup = false, shortcut = true;
        for (int i = 1; i < count; i++) {
            std::wstring arg = args[i];
            if (arg == L"/S") silent = true;
            if (arg == L"/INSTALL") automatic = true;
            if (arg == L"/STARTUP") startup = true;
            if (arg == L"/NOSHORTCUT") shortcut = false;
            if (arg.rfind(L"/D=", 0) == 0) folder = arg.substr(3);
        }
        LocalFree(args);
        if (silent) Installation(nullptr, Installation::Validate(folder)).Run(startup, shortcut);
        else result = SetupWindow(folder, automatic).Run(instance);
    }
    catch (...) { if (!silent) MessageBoxW(nullptr, L"Setup could not start.", L"HowsMyUsage", MB_ICONERROR); result = 1; }
    Gdiplus::GdiplusShutdown(token); CoUninitialize(); return result;
}
