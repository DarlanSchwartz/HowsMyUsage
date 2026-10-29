#pragma once
#include <windows.h>
#include <filesystem>
#include <string>
#include <stdexcept>

namespace StartupIntegration {
inline DWORD Run(const std::filesystem::path& root, const wchar_t* arguments) {
    auto exe = root / L"Usage.exe";
    auto command = L"\"" + exe.wstring() + L"\" " + arguments;
    STARTUPINFOW info{sizeof(info)}; PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, root.c_str(), &info, &process))
        throw std::runtime_error("Could not configure Windows startup.");
    CloseHandle(process.hThread);
    DWORD wait = WaitForSingleObject(process.hProcess, 30000), result = 2;
    if (wait == WAIT_OBJECT_0) GetExitCodeProcess(process.hProcess, &result);
    CloseHandle(process.hProcess);
    if (wait != WAIT_OBJECT_0) throw std::runtime_error("Windows startup configuration timed out.");
    return result;
}
inline void Configure(const std::filesystem::path& root, bool enabled) {
    if (Run(root, enabled ? L"--configure-startup --enable" : L"--configure-startup") != 0)
        throw std::runtime_error("Could not configure Windows startup. Check Task Scheduler availability.");
}
}
