#pragma once
#include <windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

namespace inspection {
constexpr size_t MaxFileSize = 128 * 1024 * 1024;
struct Section { uint32_t rva{}; std::vector<unsigned char> bytes; };
struct Image { uintptr_t base{}; uint32_t size{}; std::vector<Section> code; };
struct Mask { uint32_t offset{}, size{}; };
struct Pattern {
    const char* name; uint32_t length; std::array<unsigned char, 64> prefix, prefixMask;
    const Mask* masks; size_t maskCount; const char* digest;
};
struct QualityLayout { bool supported{}; uint32_t startRva{}; };
inline bool hash(const std::vector<unsigned char>& bytes, std::string& result) {
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE handle{};
    DWORD objectSize{}, written{}; unsigned char digest[32]{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return false;
    bool good = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectSize), sizeof(objectSize), &written, 0) >= 0;
    std::vector<unsigned char> object(objectSize);
    if (good) good = BCryptCreateHash(algorithm, &handle, object.data(), objectSize, nullptr, 0, 0) >= 0;
    if (good) good = BCryptHashData(handle, const_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), 0) >= 0;
    if (good) good = BCryptFinishHash(handle, digest, 32, 0) >= 0;
    if (handle) BCryptDestroyHash(handle);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (good) {
        char text[65]{};
        for (int i = 0; i < 32; ++i) sprintf_s(text + i * 2, 3, "%02x", digest[i]);
        result = text;
    }
    return good;
}
inline bool headers(const unsigned char* data, size_t size, IMAGE_NT_HEADERS64& nt, size_t& sectionOffset) {
    if (size < sizeof(IMAGE_DOS_HEADER) || size < sizeof(IMAGE_NT_HEADERS64)) return false;
    IMAGE_DOS_HEADER dos{}; memcpy(&dos, data, sizeof(dos));
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < 0 || static_cast<size_t>(dos.e_lfanew) > size - sizeof(nt)) return false;
    memcpy(&nt, data + dos.e_lfanew, sizeof(nt));
    if (nt.Signature != IMAGE_NT_SIGNATURE || nt.FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        !(nt.FileHeader.Characteristics & IMAGE_FILE_DLL) || nt.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        nt.FileHeader.SizeOfOptionalHeader < sizeof(IMAGE_OPTIONAL_HEADER64) || !nt.FileHeader.NumberOfSections ||
        nt.FileHeader.NumberOfSections > 96 || !nt.OptionalHeader.SizeOfImage || nt.OptionalHeader.SizeOfImage > MaxFileSize) return false;
    sectionOffset = static_cast<size_t>(dos.e_lfanew) + offsetof(IMAGE_NT_HEADERS64, OptionalHeader) + nt.FileHeader.SizeOfOptionalHeader;
    return sectionOffset <= size && nt.FileHeader.NumberOfSections <= (size - sectionOffset) / sizeof(IMAGE_SECTION_HEADER);
}
inline bool fileImage(const wchar_t* path, Image& image) {
    try {
        const auto length = std::filesystem::file_size(path);
        if (length < 512 || length > MaxFileSize) return false;
        std::ifstream file(std::filesystem::path(path), std::ios::binary);
        std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(file)), {});
        if (bytes.size() != length) return false;
        IMAGE_NT_HEADERS64 nt{}; size_t table{};
        if (!headers(bytes.data(), bytes.size(), nt, table)) return false;
        for (const char* marker : {"nvEncodeAPI64.dll", "NvEncodeAPICreateInstance"})
            if (std::search(bytes.begin(), bytes.end(), marker, marker + strlen(marker)) == bytes.end()) return false;
        Image result{}; result.base = static_cast<uintptr_t>(nt.OptionalHeader.ImageBase); result.size = nt.OptionalHeader.SizeOfImage;
        for (uint32_t i = 0; i < nt.FileHeader.NumberOfSections; ++i) {
            IMAGE_SECTION_HEADER section{}; memcpy(&section, bytes.data() + table + i * sizeof(section), sizeof(section));
            if (section.PointerToRawData > bytes.size() || section.SizeOfRawData > bytes.size() - section.PointerToRawData ||
                section.VirtualAddress > result.size || section.Misc.VirtualSize > result.size - section.VirtualAddress) return false;
            if (section.Characteristics & IMAGE_SCN_MEM_EXECUTE) {
                const auto codeLength = (std::min)(section.SizeOfRawData, section.Misc.VirtualSize);
                if (codeLength) result.code.push_back({section.VirtualAddress, std::vector<unsigned char>(
                    bytes.begin() + section.PointerToRawData, bytes.begin() + section.PointerToRawData + codeLength)});
            }
        }
        if (result.code.empty()) return false;
        image = std::move(result); return true;
    } catch (...) { return false; }
}
inline bool readable(HMODULE owner, const unsigned char* begin, size_t length) {
    auto cursor = begin; const auto end = begin + length;
    while (cursor < end) {
        MEMORY_BASIC_INFORMATION memory{};
        if (!VirtualQuery(cursor, &memory, sizeof(memory)) || memory.AllocationBase != owner ||
            memory.State != MEM_COMMIT || (memory.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
        const auto next = static_cast<const unsigned char*>(memory.BaseAddress) + memory.RegionSize;
        if (next <= cursor) return false;
        cursor = next;
    }
    return true;
}
inline bool moduleImage(HMODULE module, Image& image) {
    const auto base = reinterpret_cast<const unsigned char*>(module);
    MEMORY_BASIC_INFORMATION first{};
    if (!module || !VirtualQuery(base, &first, sizeof(first)) || first.AllocationBase != module ||
        first.RegionSize < 4096 || !readable(module, base, 4096)) return false;
    IMAGE_NT_HEADERS64 nt{}; size_t table{};
    if (!headers(base, 4096, nt, table)) return false;
    Image result{}; result.base = reinterpret_cast<uintptr_t>(module); result.size = nt.OptionalHeader.SizeOfImage;
    for (uint32_t i = 0; i < nt.FileHeader.NumberOfSections; ++i) {
        IMAGE_SECTION_HEADER section{}; memcpy(&section, base + table + i * sizeof(section), sizeof(section));
        if (section.VirtualAddress > result.size || section.Misc.VirtualSize > result.size - section.VirtualAddress) return false;
        if (section.Characteristics & IMAGE_SCN_MEM_EXECUTE) {
            const auto length = (std::min)(section.SizeOfRawData, section.Misc.VirtualSize);
            if (length && !readable(module, base + section.VirtualAddress, length)) return false;
            if (length) result.code.push_back({section.VirtualAddress, std::vector<unsigned char>(
                base + section.VirtualAddress, base + section.VirtualAddress + length)});
        }
    }
    if (result.code.empty()) return false;
    image = std::move(result); return true;
}
inline bool executableCaller(const Image& image, uintptr_t caller) {
    if (caller < image.base || caller - image.base >= image.size) return false;
    const auto rva = caller - image.base;
    for (const auto& section : image.code) if (rva >= section.rva && rva - section.rva < section.bytes.size()) return true;
    return false;
}
inline QualityLayout qualityLayout(const Image& image, const Pattern* patterns, size_t count) {
    QualityLayout result{};
    for (size_t p = 0; p < count; ++p) {
        const auto& pattern = patterns[p]; uint32_t foundRva{}; uint32_t matches{};
        if (pattern.length < pattern.prefix.size()) return {};
        for (const auto& section : image.code) {
            if (section.bytes.size() < pattern.length) continue;
            for (size_t offset = 0; offset <= section.bytes.size() - pattern.length; ++offset) {
                const auto* candidate = section.bytes.data() + offset;
                if (candidate[0] != pattern.prefix[0]) continue;
                bool prefix = true;
                for (size_t i = 0; i < pattern.prefix.size(); ++i)
                    if (pattern.prefixMask[i] && candidate[i] != pattern.prefix[i]) { prefix = false; break; }
                if (!prefix) continue;
                std::vector<unsigned char> normalized(candidate, candidate + pattern.length);
                for (size_t m = 0; m < pattern.maskCount; ++m) {
                    const auto& mask = pattern.masks[m];
                    if (mask.offset > normalized.size() || mask.size > normalized.size() - mask.offset) return {};
                    std::fill(normalized.begin() + mask.offset, normalized.begin() + mask.offset + mask.size, static_cast<unsigned char>(0));
                }
                std::string digest;
                if (!hash(normalized, digest) || digest != pattern.digest) continue;
                if (++matches > 1) return {}; // Ambiguity never authorizes a quality hook.
                foundRva = section.rva + static_cast<uint32_t>(offset);
            }
        }
        if (matches != 1) return {};
        if (p == 0) result.startRva = foundRva;
    }
    result.supported = count > 0; return result;
}
}
