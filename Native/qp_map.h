#pragma once
#include <algorithm>
#include <cstdint>
#include <cstddef>

// Valve's live map has four consecutive regions: gaze, periphery, gaze, periphery.
// 31 is a mask/sentinel, and the first tile of the final row carries timing data.
struct QualityMapMetadata {
    uint32_t size{}, peripheralTiles{}, peripheralChangedTiles{};
    uint32_t centerTiles{}, centerChangedTiles{}, centerClampedTiles{};
    int centerMinDelta{}, centerMaxDelta{};
    uint32_t peripheralSaturatedTiles{}, peripheralClampedTiles{};
    uint32_t protectedTiles{}, protectedMismatches{};
    int peripheralMinDelta{}, peripheralMaxDelta{}, centerOffset{}, peripheralOffset{};
    bool enabled{};
};

inline bool QualityOffsetsValid(int center, int periphery) {
    return center >= -20 && center <= 12 && periphery >= -12 && periphery <= 31;
}

inline bool ApplyQualityMap(const int8_t* source, int8_t* destination, uint32_t size,
                            uint32_t width, int center, int periphery, bool enabled,
                            QualityMapMetadata* metadata = nullptr) {
    if (!source || !destination || !width || !size || size % 4) return false;
    for (uint32_t i = 0; i < size; ++i) if (source[i] < 0 || source[i] > 31) return false;
    const auto quarter = size / 4;
    const auto columns = width / 32;
    if (!columns || size % columns) return false;
    const auto clock = columns * (size / columns - 1);
    QualityMapMetadata observed{};
    observed.size = size;
    observed.centerOffset = center;
    observed.peripheralOffset = periphery;
    observed.enabled = enabled;
    for (uint32_t i = 0; i < size; ++i) {
        const int original = source[i];
        const bool protectedTile = original == 31 || i == clock;
        const bool peripheralTile = ((i / quarter) & 1) != 0;
        const int requested = original + (enabled && !protectedTile ? (peripheralTile ? periphery : center) : 0);
        const int value = protectedTile ? original : std::clamp(requested, -20, 31);
        destination[i] = static_cast<int8_t>(value);
        if (protectedTile) {
            ++observed.protectedTiles;
            observed.protectedMismatches += destination[i] != original;
        } else if (peripheralTile) {
            if (!observed.peripheralTiles) observed.peripheralMinDelta = observed.peripheralMaxDelta = value;
            else {
                observed.peripheralMinDelta = std::min(observed.peripheralMinDelta, value);
                observed.peripheralMaxDelta = std::max(observed.peripheralMaxDelta, value);
            }
            ++observed.peripheralTiles;
            observed.peripheralChangedTiles += value != original;
            observed.peripheralSaturatedTiles += value == 31;
            observed.peripheralClampedTiles += value != requested;
        } else {
            if (!observed.centerTiles) observed.centerMinDelta = observed.centerMaxDelta = value;
            else {
                observed.centerMinDelta = std::min(observed.centerMinDelta, value);
                observed.centerMaxDelta = std::max(observed.centerMaxDelta, value);
            }
            ++observed.centerTiles;
            observed.centerChangedTiles += value != original;
            observed.centerClampedTiles += value != requested;
        }
    }
    // An invalid map leaves both the destination and the last valid metadata intact.
    if (metadata) *metadata = observed;
    return true;
}
