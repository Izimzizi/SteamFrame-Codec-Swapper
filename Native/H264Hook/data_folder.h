#pragma once
#include <windows.h>
#include <string>

// The tray's data folder (%LOCALAPPDATA%\CodecSwapper) as a wide path. The ANSI environment and file APIs turn a user name with
// characters outside the system code page into question marks, so every file the hook reads or writes there goes through this.
// The folder is looked up and created once; later calls only append the file name.
inline std::wstring hook_data_path(const wchar_t* name){
 static const std::wstring directory=[]()->std::wstring{
  wchar_t base[MAX_PATH]{};
  const DWORD length=GetEnvironmentVariableW(L"LOCALAPPDATA",base,MAX_PATH);
  if(!length||length>=MAX_PATH)return {};
  const std::wstring folder=std::wstring(base)+L"\\CodecSwapper";
  CreateDirectoryW(folder.c_str(),nullptr);
  return folder;
 }();
 return directory.empty()?std::wstring():directory+L"\\"+name;
}
