#pragma once
#include <windows.h>
#include <string>

// The tray's data folder (%LOCALAPPDATA%\CodecSwapper) as a wide path. The ANSI environment and file APIs turn a user name with
// characters outside the system code page into question marks, so every file the hook reads or writes there goes through this.
inline std::wstring hook_data_path(const wchar_t* name){
 wchar_t base[MAX_PATH]{};
 const DWORD length=GetEnvironmentVariableW(L"LOCALAPPDATA",base,MAX_PATH);
 if(!length||length>=MAX_PATH)return {};
 const std::wstring directory=std::wstring(base)+L"\\CodecSwapper";
 CreateDirectoryW(directory.c_str(),nullptr);
 return directory+L"\\"+name;
}
