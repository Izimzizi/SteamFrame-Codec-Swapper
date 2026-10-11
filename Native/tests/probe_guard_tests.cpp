#include <windows.h>
#include <cstdio>
using Export = DWORD(WINAPI*)(void*);
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 1;
    const auto module = LoadLibraryW(argv[1]);
    if (!module) return 2;
    const auto version = reinterpret_cast<Export>(GetProcAddress(module, "GetProbeVersion"));
    const auto start = reinterpret_cast<Export>(GetProcAddress(module, "StartProbe"));
    const auto stop = reinterpret_cast<Export>(GetProcAddress(module, "StopProbe"));
    const auto set = reinterpret_cast<Export>(GetProcAddress(module, "SetQuality"));
    if (!version || !start || !stop || !set) return 3;
    if (version(nullptr) != 1 || start(nullptr) != 1001 || stop(nullptr) != 0 || set(nullptr) != 1008) return 4;
    // No hooks installed here, so unloading this test host's copy is safe.
    FreeLibrary(module);
    return 0;
}
