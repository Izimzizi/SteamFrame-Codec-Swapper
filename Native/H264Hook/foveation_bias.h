#pragma once
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <string>
#include <vector>
#include "../qp_map.h"

// The foveation quality bias chosen in the tray or on the HUD, read from foveation.txt in the tray's data folder about twice a second:
//   gaze=-20..12  periphery=-12..31
// Both are offsets added to Valve's own quality map (lower is sharper), the gaze region and the periphery separately; the map's masks and
// timing tile are kept. No file, or both zero, leaves the map exactly as SteamVR made it. Used for both codecs, so a change reaches the next frame.
struct FoveationBias { int gaze = 0, periphery = 0; };

inline FoveationBias FoveationBiasNow() {
    static std::mutex gate;
    static ULONGLONG checked = 0;
    static FoveationBias current;
    std::lock_guard<std::mutex> lock(gate);
    const ULONGLONG now = GetTickCount64();
    if (checked && now - checked < 500) return current;
    checked = now;
    current = FoveationBias{};
    char base[MAX_PATH]{};
    if (!GetEnvironmentVariableA("LOCALAPPDATA", base, MAX_PATH)) return current;
    FILE* file = nullptr;
    const std::string path = std::string(base) + "\\CodecSwapper\\foveation.txt";
    if (fopen_s(&file, path.c_str(), "r") || !file) return current;
    FoveationBias read;
    char line[64];
    while (fgets(line, sizeof(line), file)) {
        int number = 0;
        if (sscanf_s(line, "gaze=%d", &number) == 1) read.gaze = number;
        else if (sscanf_s(line, "periphery=%d", &number) == 1) read.periphery = number;
    }
    fclose(file);
    if (QualityOffsetsValid(read.gaze, read.periphery)) current = read;
    return current;
}

// Returns the map to encode with: the original when the bias is off or the map is not one this knows how to bias, otherwise the biased copy kept in scratch.
inline const int8_t* FoveationApplyMap(const int8_t* map, uint32_t entries, uint32_t width, std::vector<int8_t>& scratch) {
    if (!map || !entries) return map;
    const FoveationBias bias = FoveationBiasNow();
    if (!bias.gaze && !bias.periphery) return map;
    scratch.resize(entries);
    return ApplyQualityMap(map, scratch.data(), entries, width, bias.gaze, bias.periphery, true) ? scratch.data() : map;
}
