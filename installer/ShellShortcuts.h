#pragma once
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wrl/client.h>
#include <filesystem>
#include <stdexcept>

namespace ShellShortcuts {
namespace fs = std::filesystem;
using Microsoft::WRL::ComPtr;

struct Apartment {
    HRESULT result = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    ~Apartment() { if (SUCCEEDED(result)) CoUninitialize(); }
};

inline fs::path Folder(REFKNOWNFOLDERID id) {
    PWSTR value = nullptr;
    if (FAILED(SHGetKnownFolderPath(id, 0, nullptr, &value)))
        throw std::runtime_error("Could not locate the Windows shortcut folder.");
    fs::path path(value);
    CoTaskMemFree(value);
    return path;
}

inline void Write(const fs::path& shortcut, const fs::path& target) {
    Apartment apartment;
    ComPtr<IShellLinkW> link;
    ComPtr<IPersistFile> file;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))) ||
        FAILED(link->SetPath(target.c_str())) || FAILED(link->SetWorkingDirectory(target.parent_path().c_str())) ||
        FAILED(link->SetIconLocation(target.c_str(), 0)) || FAILED(link.As(&file)) ||
        FAILED(file->Save(shortcut.c_str(), TRUE)))
        throw std::runtime_error("Could not create the application shortcut.");
}

inline void RemoveIfTargets(const fs::path& shortcut, const fs::path& target) {
    if (!fs::exists(shortcut)) return;
    Apartment apartment;
    ComPtr<IShellLinkW> link;
    ComPtr<IPersistFile> file;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))) ||
        FAILED(link.As(&file)) || FAILED(file->Load(shortcut.c_str(), STGM_READ))) return;
    wchar_t path[MAX_PATH]{};
    if (SUCCEEDED(link->GetPath(path, MAX_PATH, nullptr, SLGP_RAWPATH)) && _wcsicmp(path, target.c_str()) == 0)
        fs::remove(shortcut);
}
}
