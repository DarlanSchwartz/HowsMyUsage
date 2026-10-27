#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <filesystem>
#include <fstream>
#include <algorithm>
#include <cwctype>
#include <vector>
#include <string>
#include "ShellShortcuts.h"

namespace fs = std::filesystem;

class Uninstallation {
    fs::path executable, root;

    void CheckPath(const fs::path& path) const {
        auto relative = path.lexically_relative(root);
        if (relative.empty() || relative.is_absolute() || relative.has_root_name())
            throw std::runtime_error("Invalid installed-file path.");
        for (const auto& part : relative)
            if (part == L"..") throw std::runtime_error("Invalid installed-file path.");
        for (auto parent = path; !parent.empty(); parent = parent.parent_path()) {
            DWORD attributes = GetFileAttributesW(parent.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
                throw std::runtime_error("Uninstall cannot follow junctions or symbolic links.");
            if (parent == parent.root_path()) break;
        }
    }

    static std::wstring RegistryValue(const wchar_t* key, const wchar_t* name) {
        wchar_t value[16384]{}; DWORD size = sizeof(value);
        if (RegGetValueW(HKEY_CURRENT_USER, key, name, RRF_RT_REG_SZ, nullptr, value, &size) != ERROR_SUCCESS) return L"";
        return value;
    }

    void RemoveRegistration() {
        if (_wcsicmp(RegistryValue(L"Software\\HowsMyUsage", L"InstallDir").c_str(), root.c_str()) != 0) return;
        const wchar_t* run = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        auto command = RegistryValue(run, L"UsageWidget");
        auto launcher = (root / L"startup.ps1").wstring();
        std::transform(command.begin(), command.end(), command.begin(), towlower);
        std::transform(launcher.begin(), launcher.end(), launcher.begin(), towlower);
        auto native = L"\"" + (root / L"Usage.exe").wstring() + L"\" --startup";
        std::transform(native.begin(), native.end(), native.begin(), towlower);
        if (command.find(launcher) != std::wstring::npos || command == native)
            RegDeleteKeyValueW(HKEY_CURRENT_USER, run, L"UsageWidget");
        RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\HowsMyUsage");
        RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\HowsMyUsage");
    }

public:
    Uninstallation() {
        wchar_t path[MAX_PATH]{};
        if (!GetModuleFileNameW(nullptr, path, MAX_PATH)) throw std::runtime_error("Could not locate the application.");
        executable = fs::absolute(path).lexically_normal(); root = executable.parent_path();
        if (root == root.root_path() || !fs::is_regular_file(root / L"Usage.runtimeconfig.json"))
            throw std::runtime_error("Run the uninstaller from the HowsMyUsage installation folder.");
        CheckPath(executable);
    }

    void Run(bool keepPreferences) {
        HANDLE mutex = OpenMutexW(SYNCHRONIZE, FALSE, L"Usage.NativeTray");
        if (mutex) { CloseHandle(mutex); throw std::runtime_error("Exit HowsMyUsage from the tray before uninstalling."); }
        std::ifstream manifest(root / L"install-files.txt", std::ios::binary);
        if (!manifest) throw std::runtime_error("The installed-file manifest is missing. Reinstall HowsMyUsage, then uninstall it.");
        std::vector<fs::path> files, directories;
        std::string line;
        while (std::getline(manifest, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            fs::path relative = fs::u8path(line);
            if (relative.is_absolute() || relative.has_root_name()) throw std::runtime_error("Invalid installed-file manifest.");
            auto target = (root / relative).lexically_normal();
            CheckPath(target); files.push_back(target);
        }
        manifest.close();
        if (std::find(files.begin(), files.end(), root / L"Usage.exe") == files.end() ||
            std::find(files.begin(), files.end(), executable) == files.end())
            throw std::runtime_error("Invalid installed-file manifest.");
        if (!keepPreferences) {
            for (const wchar_t* name : {L"widget-settings.json", L"widget-settings.json.tmp", L"claude-session.bin", L"claude-session.bin.tmp", L"claude.log", L"startup.log"}) {
                auto path = root / name; CheckPath(path); files.push_back(path);
            }
            auto temp = root / L"temp";
            CheckPath(temp);
            if (fs::exists(temp)) {
                for (const auto& entry : fs::recursive_directory_iterator(temp)) CheckPath(entry.path());
                fs::remove_all(temp);
            }
        }
        auto programs = ShellShortcuts::Folder(FOLDERID_Programs) / L"HowsMyUsage";
        ShellShortcuts::RemoveIfTargets(programs / L"HowsMyUsage.lnk", root / L"Usage.exe");
        ShellShortcuts::RemoveIfTargets(programs / L"Uninstall HowsMyUsage.lnk", executable);
        ShellShortcuts::RemoveIfTargets(ShellShortcuts::Folder(FOLDERID_Desktop) / L"HowsMyUsage.lnk", root / L"Usage.exe");
        std::error_code error; fs::remove(programs, error);
        for (const auto& file : files) {
            if (file == executable) continue;
            fs::remove(file);
            for (auto parent = file.parent_path(); parent != root && !parent.empty(); parent = parent.parent_path())
                directories.push_back(parent);
        }
        std::sort(directories.begin(), directories.end(), [](const auto& a, const auto& b) { return a.native().size() > b.native().size(); });
        for (const auto& directory : directories) fs::remove(directory, error);
        RemoveRegistration();
    }

    void RemoveOwnExecutableAfterExit() {
        // Only our validated image and its now-empty directory are removed.
        // Values travel through environment variables to avoid shell interpolation.
        SetEnvironmentVariableW(L"HMU_UNINSTALL_FILE", executable.c_str());
        SetEnvironmentVariableW(L"HMU_UNINSTALL_DIR", root.c_str());
        wchar_t system[MAX_PATH]; GetSystemDirectoryW(system, MAX_PATH);
        auto cmd = fs::path(system) / L"cmd.exe";
        std::wstring command = L"\"" + cmd.wstring() + L"\" /D /Q /V:OFF /S /C \"\"" +
            (fs::path(system) / L"choice.exe").wstring() +
            L"\" /C Y /N /D Y /T 1 >nul & del /F /Q \"%HMU_UNINSTALL_FILE%\" & rd \"%HMU_UNINSTALL_DIR%\" 2>nul\"";
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
        if (!CreateProcessW(cmd.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, system, &startup, &process))
            throw std::runtime_error("The app was removed, but Uninstall.exe could not remove itself.");
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    }
};

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    ShellShortcuts::Apartment apartment;
    bool silent = false, keep = true;
    int count = 0; auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    for (int i = 1; i < count; i++) {
        if (wcscmp(args[i], L"/S") == 0) silent = true;
        if (wcscmp(args[i], L"/PURGE") == 0) keep = false;
    }
    LocalFree(args);
    try {
        Uninstallation uninstall;
        if (!silent) {
            TASKDIALOG_BUTTON buttons[] = {{IDOK, L"Uninstall"}, {IDCANCEL, L"Cancel"}};
            TASKDIALOGCONFIG dialog{sizeof(dialog)};
            dialog.dwFlags = TDF_VERIFICATION_FLAG_CHECKED;
            dialog.pszWindowTitle = L"HowsMyUsage"; dialog.pszMainInstruction = L"Uninstall HowsMyUsage?";
            dialog.pszVerificationText = L"Keep preferences and saved sign-in";
            dialog.cButtons = 2; dialog.pButtons = buttons; dialog.nDefaultButton = IDCANCEL;
            int button = IDCANCEL; BOOL checked = TRUE;
            if (FAILED(TaskDialogIndirect(&dialog, &button, nullptr, &checked)) || button != IDOK) return 0;
            keep = checked != FALSE;
        }
        uninstall.Run(keep);
        if (!silent) MessageBoxW(nullptr, L"HowsMyUsage was uninstalled.", L"HowsMyUsage", MB_OK | MB_ICONINFORMATION);
        uninstall.RemoveOwnExecutableAfterExit();
        return 0;
    }
    catch (const std::exception& error) {
        if (!silent) { std::string text = error.what(); std::wstring message(text.begin(), text.end()); MessageBoxW(nullptr, message.c_str(), L"HowsMyUsage", MB_ICONERROR); }
        return 1;
    }
}
