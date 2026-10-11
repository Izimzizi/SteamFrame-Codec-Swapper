// Exercise the hooks in this isolated executable with fake NVENC entry points.
// No SteamVR process, GPU session, injection or real video payload is involved.
#include "../encoder_probe.cpp"
NVENCSTATUS lockResult = NV_ENC_SUCCESS, encodeResult = NV_ENC_SUCCESS;
NVENCSTATUS NVENCAPI fakeLock(void*, NV_ENC_LOCK_BITSTREAM*) { return lockResult; }
NVENCSTATUS NVENCAPI fakeEncode(void*, NV_ENC_PIC_PARAMS*) { return encodeResult; }
NVENCSTATUS NVENCAPI fakeDestroy(void*) { return NV_ENC_SUCCESS; }
int main() {
    originalLock = fakeLock; originalEncode = fakeEncode; originalDestroy = fakeDestroy;
    targets[1] = reinterpret_cast<void*>(fakeEncode); targets[2] = reinterpret_cast<void*>(fakeLock);
    // Test-only executable range: calls below originate from this host, not SteamVR.
    driverImage.base = reinterpret_cast<uintptr_t>(&main); driverImage.size = 16384;
    driverImage.code.push_back({0, std::vector<unsigned char>(16384)});
    installed.store(true);
    NV_ENC_LOCK_BITSTREAM output{};
    output.version = NV_ENC_LOCK_BITSTREAM_VER;
    output.outputBitstream = reinterpret_cast<void*>(123);
    output.bitstreamBufferPtr = reinterpret_cast<void*>(1); // Poison pointer: must never be read.
    output.bitstreamSizeInBytes = 4096; output.frameIdx = 1; output.outputTimeStamp = 50; output.frameAvgQP=24;
    auto valve = reinterpret_cast<void*>(100), unrelated = reinterpret_cast<void*>(200);
    hookLock(valve, &output);
    if (stats.encodedBytes || stats.encodedFrames) return 1; // Unlearned session ignored.
    std::array<unsigned char, 40000> encoder{};
    *reinterpret_cast<void**>(encoder.data() + 520) = valve;
    *reinterpret_cast<uint32_t*>(encoder.data() + 39520) = NV_ENCODE_API_FUNCTION_LIST_VER;
    *reinterpret_cast<void**>(encoder.data() + 39656) = targets[1];
    *reinterpret_cast<void**>(encoder.data() + 39664) = targets[2];
    currentEncoder = reinterpret_cast<uintptr_t>(encoder.data());
    NV_ENC_PIC_PARAMS picture{};
    picture.version = NV_ENC_PIC_PARAMS_VER;
    picture.outputBitstream = output.outputBitstream;
    hookEncode(valve, &picture); currentEncoder = 0;
    hookLock(unrelated, &output);
    if (stats.encodedBytes || stats.encodedFrames || stats.frames != 1) return 2;
    hookLock(valve, &output); hookLock(valve, &output);
    if (stats.encodedBytes != 4096 || stats.encodedFrames != 1 || stats.lastEncodedTime <= 0 ||
        stats.quantizerSamples!=1 || stats.quantizerSum!=24 || stats.lastFrameQp!=24 || stats.lastFrameBytes!=4096) return 3;
    // A new submission is countable even if the caller repeats its frame identifiers.
    currentEncoder = reinterpret_cast<uintptr_t>(encoder.data());
    hookEncode(valve, &picture); currentEncoder = 0;
    output.bitstreamSizeInBytes = 1024; output.frameAvgQP=22;
    hookLock(valve, &output);
    if (stats.encodedBytes != 5120 || stats.encodedFrames != 2 || stats.quantizerSamples!=2 ||
        stats.quantizerSum!=46 || stats.lastFrameQp!=22 || stats.lastFrameBytes!=1024) return 4;
    output.frameIdx = 2; output.outputTimeStamp = 100;
    hookLock(valve, &output);
    if (stats.encodedBytes != 5120 || stats.encodedFrames != 2) return 8; // Metadata alone is not a new submission.
    lockResult = NV_ENC_ERR_LOCK_BUSY; hookLock(valve, &output);
    if (stats.lockErrors || stats.encodedBytes != 5120) return 5;
    lockResult = NV_ENC_ERR_GENERIC; hookLock(valve, &output);
    if (stats.lockErrors != 1 || stats.encodedBytes != 5120) return 6;
    encodeResult = NV_ENC_ERR_GENERIC;
    currentEncoder = reinterpret_cast<uintptr_t>(encoder.data());
    hookEncode(valve, &picture); currentEncoder = 0;
    lockResult = NV_ENC_SUCCESS; hookLock(valve, &output);
    if (stats.encodedBytes != 5120 || stats.encodeErrors != 1) return 9; // Failed submission cannot reopen the previous output.
    lockResult = NV_ENC_SUCCESS; hookDestroy(valve); output.frameIdx = 3; hookLock(valve, &output);
    if (stats.encodedBytes != 5120 || !sessions.empty()) return 7;
    picture.version = 0;
    hookEncode(valve, &picture);
    if (stats.rejectedApiCalls != 1 || stats.frames != 3) return 10;
    driverImage.code.clear(); picture.version = NV_ENC_PIC_PARAMS_VER;
    hookEncode(valve, &picture);
    if (stats.frames != 3 || !sessions.empty()) return 11; // Non-Valve caller ignored.
    printf("API=0x%08x lockVersion=0x%08x functionListVersion=0x%08x lockSize=%zu sizeOffset=%zu frameIdxOffset=%zu timestampOffset=%zu lockPointerOffset=%zu\n",
        NVENCAPI_VERSION, NV_ENC_LOCK_BITSTREAM_VER, NV_ENCODE_API_FUNCTION_LIST_VER,
        sizeof(NV_ENC_LOCK_BITSTREAM), offsetof(NV_ENC_LOCK_BITSTREAM, bitstreamSizeInBytes),
        offsetof(NV_ENC_LOCK_BITSTREAM, frameIdx), offsetof(NV_ENC_LOCK_BITSTREAM, outputTimeStamp),
        offsetof(NV_ENCODE_API_FUNCTION_LIST, nvEncLockBitstream));
    return 0;
}
