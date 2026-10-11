#include "../qp_map.h"
#include <array>
#include <cstdio>
#include <tuple>

auto metadataValues(const QualityMapMetadata& value) {
    return std::make_tuple(value.size, value.peripheralTiles, value.peripheralChangedTiles,
        value.peripheralSaturatedTiles, value.peripheralClampedTiles, value.protectedTiles,
        value.protectedMismatches, value.peripheralMinDelta, value.peripheralMaxDelta,
        value.centerOffset, value.peripheralOffset, value.enabled,
        value.centerTiles, value.centerChangedTiles, value.centerClampedTiles, value.centerMinDelta, value.centerMaxDelta);
}

int main() {
    std::array<int8_t, 16> input{}, output{};
    QualityMapMetadata metadata{};
    input.fill(5); input[1] = 31; input[8] = 0; input[14] = 30;
    if (!ApplyQualityMap(input.data(), output.data(), 16, 128, -12, 12, true, &metadata)) return 1;
    const int8_t expected[] = {-7,31,-7,-7,17,17,17,17,-12,-7,-7,-7,5,17,31,17};
    for (int i = 0; i < 16; ++i) if (output[i] != expected[i]) { printf("tile %d: %d\n", i, output[i]); return 2; }
    if (metadata.peripheralTiles != 7 || metadata.peripheralChangedTiles != 7 ||
        metadata.peripheralSaturatedTiles != 1 || metadata.peripheralClampedTiles != 1 ||
        metadata.peripheralMinDelta != 17 || metadata.peripheralMaxDelta != 31 ||
        metadata.protectedTiles != 2 || metadata.protectedMismatches != 0 ||
        metadata.centerTiles != 7 || metadata.centerChangedTiles != 7 || metadata.centerClampedTiles ||
        metadata.centerMinDelta != -12 || metadata.centerMaxDelta != -7) return 3;

    // +31 is an offset for ordinary tiles, not permission to overwrite mask/timing data.
    // The zero source tile reaches 31 without clamping; six other peripheral tiles clamp.
    input[4] = 0;
    if (!ApplyQualityMap(input.data(), output.data(), 16, 128, -12, 31, true, &metadata)) return 4;
    const std::array<int8_t, 16> at31 = {-7,31,-7,-7,31,31,31,31,-12,-7,-7,-7,5,31,31,31};
    if (output != at31 || metadata.size != 16 || !metadata.enabled ||
        metadata.centerOffset != -12 || metadata.peripheralOffset != 31 ||
        metadata.peripheralTiles != 7 || metadata.peripheralChangedTiles != 7 ||
        metadata.peripheralSaturatedTiles != 7 || metadata.peripheralClampedTiles != 6 ||
        metadata.peripheralMinDelta != 31 || metadata.peripheralMaxDelta != 31 ||
        metadata.protectedTiles != 2 || metadata.protectedMismatches != 0) return 5;

    if (!ApplyQualityMap(input.data(), output.data(), 16, 128, -12, 31, false, &metadata) || output != input) return 6;
    if (metadata.enabled || metadata.peripheralChangedTiles || metadata.peripheralSaturatedTiles ||
        metadata.peripheralClampedTiles || metadata.peripheralTiles != 7 ||
        metadata.peripheralMinDelta != 0 || metadata.peripheralMaxDelta != 30 ||
        metadata.protectedTiles != 2 || metadata.protectedMismatches) return 7;

    auto inPlace = input;
    if (!ApplyQualityMap(inPlace.data(), inPlace.data(), 16, 128, -12, 31, true, &metadata) || inPlace != at31) return 8;
    if (metadata.peripheralChangedTiles != 7 || metadata.protectedMismatches) return 9;

    // A mask at the timing position is counted once; an all-protected map has no ordinary range.
    input.fill(31);
    if (!ApplyQualityMap(input.data(), output.data(), 16, 128, -12, 31, true, &metadata) || output != input) return 10;
    if (metadata.protectedTiles != 16 || metadata.protectedMismatches || metadata.peripheralTiles ||
        metadata.peripheralChangedTiles || metadata.peripheralSaturatedTiles || metadata.peripheralClampedTiles ||
        metadata.peripheralMinDelta || metadata.peripheralMaxDelta) return 11;

    const auto priorOutput = output;
    const auto priorMetadata = metadataValues(metadata);
    input[3] = -1;
    if (ApplyQualityMap(input.data(), output.data(), 16, 128, 0, 0, true, &metadata) ||
        output != priorOutput || metadataValues(metadata) != priorMetadata) return 12;
    input[3] = 32;
    if (ApplyQualityMap(input.data(), output.data(), 16, 128, 0, 0, false, &metadata) ||
        output != priorOutput || metadataValues(metadata) != priorMetadata) return 13;
    input[3] = 31;
    if (ApplyQualityMap(input.data(), output.data(), 15, 128, 0, 0, true, &metadata) ||
        ApplyQualityMap(input.data(), output.data(), 16, 160, 0, 0, true, &metadata) ||
        ApplyQualityMap(input.data(), output.data(), 16, 0, 0, 0, true, &metadata) ||
        output != priorOutput || metadataValues(metadata) != priorMetadata) return 14;

    if (!QualityOffsetsValid(-20, -12) || !QualityOffsetsValid(12, 31) ||
        QualityOffsetsValid(-21, 0) || QualityOffsetsValid(13, 0) ||
        QualityOffsetsValid(0, -13) || QualityOffsetsValid(0, 32)) return 15;
    input.fill(0); input[1]=31;
    if(!ApplyQualityMap(input.data(),output.data(),16,128,-20,20,true,&metadata) ||
        output[0]!=-20 || output[1]!=31 || output[4]!=20 || output[12]!=0 ||
        metadata.centerTiles!=7 || metadata.centerChangedTiles!=7 || metadata.centerClampedTiles ||
        metadata.centerMinDelta!=-20 || metadata.centerMaxDelta!=-20 || metadata.protectedMismatches) return 16;
    printf("QP map boundary, protected tiles, metadata, invalid/no-change and offset limits passed.\n");
    return 0;
}
