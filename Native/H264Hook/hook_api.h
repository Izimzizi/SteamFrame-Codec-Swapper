#pragma once
#include <stdint.h>

// Export macros for the hook DLL; HOOK_BUILD is defined by the translation units compiled into it.
#ifdef _WIN32
#define HOOK_CALL __cdecl
#ifdef HOOK_BUILD
#define HOOK_API __declspec(dllexport)
#else
#define HOOK_API __declspec(dllimport)
#endif
#else
#define HOOK_CALL
#define HOOK_API
#endif
