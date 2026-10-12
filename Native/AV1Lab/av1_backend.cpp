#define AV1_BACKEND_BUILD
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "av1_backend.h"
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <chrono>
#include <cstring>
#include <mutex>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include "nvEncodeAPI12.h"

using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;
static thread_local std::string last_error;
static void check(NVENCSTATUS status, const char* action) {
    if (status != NV_ENC_SUCCESS)
        throw std::runtime_error(std::string(action)+": NVENC status "+std::to_string(status));
}
static void require(bool valid, const char* reason) {
    if (!valid) throw std::invalid_argument(reason);
}
static void rates(uint32_t average, uint32_t peak) {
    require(average >= 1000000 && average <= 480000000 && peak >= average && peak <= 480000000,
        "Frame AV1 level 6.1 trial: rate must be 1..480 Mbps with peak >= average");
}

struct Av1Session {
    HMODULE module = nullptr;
    NV_ENCODE_API_FUNCTION_LIST api{};
    void* encoder = nullptr;
    NV_ENC_OUTPUT_PTR output = nullptr;
    ComPtr<ID3D11Device> device;
    NV_ENC_CONFIG nv_config{};
    NV_ENC_INITIALIZE_PARAMS init{};
    Av1Config config{};
    Av1Stats stats{};
    std::vector<uint8_t> packet;
    struct Input {
        ComPtr<ID3D11Texture2D> texture;
        NV_ENC_REGISTERED_PTR registered;
    };
    std::vector<Input> inputs;
    std::mutex lock;
    bool next_keyframe = true;
    ~Av1Session() {
        if (encoder) {
            for(auto& input:inputs)api.nvEncUnregisterResource(encoder,input.registered);
            if (output) api.nvEncDestroyBitstreamBuffer(encoder, output);
            api.nvEncDestroyEncoder(encoder);
        }
        if (module) FreeLibrary(module);
    }
};

extern "C" int AV1_CALL Av1Create(void* d3d, const Av1Config* config, Av1Session** result) {
    if (result) *result = nullptr;
    std::unique_ptr<Av1Session> s;
    try {
        require(d3d && config && result, "Missing create arguments");
        require(config->size == sizeof(Av1Config) && config->version == 2, "Unsupported config ABI");
        require(config->width >= 128 && config->width <= 8192 && config->height >= 128 &&
            config->height <= 8192 && (config->width % 2)==0 && (config->height % 2)==0,
            "Invalid even encode dimensions");
        require(config->fps == 120, "This prototype requires 120 fps");
        require(config->bit_depth == 8 || config->bit_depth == 10, "Bit depth must be 8 or 10");
        require(config->input_bit_depth == 8 || config->input_bit_depth == 10,"Input bit depth must be 8 or 10");
        require(config->level_index<=17 || config->level_index==24,"Unsupported Frame AV1 level request");
        auto valid_tiles=[](uint32_t n){return n==0||n==1||n==2||n==4||n==8;};
        require(valid_tiles(config->tile_columns)&&valid_tiles(config->tile_rows),"Uniform tile counts must be 0,1,2,4,8");
        require(config->enable_delta_map <= 1 && config->keyframe_interval > 0, "Invalid codec options");
        rates(config->average_bps,config->peak_bps);
        s.reset(new Av1Session); s->config=*config;
        auto* device=static_cast<ID3D11Device*>(d3d); s->device=device;
        s->module=LoadLibraryExW(L"nvEncodeAPI64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
        require(s->module!=nullptr,"System NVENC API unavailable");
        using Create = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
        using Version = NVENCSTATUS(NVENCAPI*)(uint32_t*);
        auto create=reinterpret_cast<Create>(GetProcAddress(s->module,"NvEncodeAPICreateInstance"));
        auto version=reinterpret_cast<Version>(GetProcAddress(s->module,"NvEncodeAPIGetMaxSupportedVersion"));
        require(create && version,"NVENC entry points unavailable");
        uint32_t supported=0;check(version(&supported),"query API version");
        require(supported >= ((NVENCAPI_MAJOR_VERSION<<4)|NVENCAPI_MINOR_VERSION),"NVENC 12.1 is required");
        s->api.version=NV_ENCODE_API_FUNCTION_LIST_VER;check(create(&s->api),"create function table");
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};open.version=NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        open.device=device;open.deviceType=NV_ENC_DEVICE_TYPE_DIRECTX;open.apiVersion=NVENCAPI_VERSION;
        check(s->api.nvEncOpenEncodeSessionEx(&open,&s->encoder),"open modern session");
        auto cap=[&](NV_ENC_CAPS selector) {
            NV_ENC_CAPS_PARAM query{};query.version=NV_ENC_CAPS_PARAM_VER;query.capsToQuery=selector;
            int value=0;check(s->api.nvEncGetEncodeCaps(s->encoder,NV_ENC_CODEC_AV1_GUID,&query,&value),"AV1 capability");return value;
        };
        require(config->width <= static_cast<uint32_t>(cap(NV_ENC_CAPS_WIDTH_MAX)) &&
            config->height <= static_cast<uint32_t>(cap(NV_ENC_CAPS_HEIGHT_MAX)),"AV1 dimensions unsupported");
        if(config->bit_depth==10)require(cap(NV_ENC_CAPS_SUPPORT_10BIT_ENCODE)!=0,"10-bit AV1 unsupported");
        require(cap(NV_ENC_CAPS_SUPPORT_CUSTOM_VBV_BUF_SIZE)!=0,"Low-delay VBV unsupported");
        require(cap(NV_ENC_CAPS_SUPPORT_DYN_BITRATE_CHANGE)!=0,"Dynamic rate unsupported");
        NV_ENC_PRESET_CONFIG preset{};preset.version=NV_ENC_PRESET_CONFIG_VER;
        preset.presetCfg.version=NV_ENC_CONFIG_VER;
        check(s->api.nvEncGetEncodePresetConfigEx(s->encoder,NV_ENC_CODEC_AV1_GUID,
            NV_ENC_PRESET_P2_GUID,NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,&preset),"AV1 P2 ULL preset");
        s->nv_config=preset.presetCfg;
        auto& c=s->nv_config;c.profileGUID=NV_ENC_AV1_PROFILE_MAIN_GUID;
        c.gopLength=config->keyframe_interval;c.frameIntervalP=1;
        auto& rc=c.rcParams;rc.rateControlMode=NV_ENC_PARAMS_RC_VBR;
        rc.averageBitRate=config->average_bps;rc.maxBitRate=config->peak_bps;
        rc.vbvBufferSize=config->average_bps/120;rc.vbvInitialDelay=0;
        rc.multiPass=NV_ENC_MULTI_PASS_DISABLED;rc.enableLookahead=0;rc.lookaheadDepth=0;
        rc.enableAQ=0;rc.enableTemporalAQ=0;rc.zeroReorderDelay=1;
        rc.qpMapMode=config->enable_delta_map?NV_ENC_QP_MAP_DELTA:NV_ENC_QP_MAP_DISABLED;
        auto& a=c.encodeCodecConfig.av1Config;
        // SM8650 vendor capability table stops at level 6.1/high tier.
        // AUTO can emit unconstrained level index 31 for small high-rate clips.
        a.level=config->level_index;
        a.tier=NV_ENC_TIER_AV1_1;
        a.pixelBitDepthMinus8=config->bit_depth-8;a.inputPixelBitDepthMinus8=config->input_bit_depth-8;
        a.enableBitstreamPadding=0;a.repeatSeqHdr=1;a.idrPeriod=config->keyframe_interval;
        a.enableCustomTileConfig=0;a.numTileColumns=config->tile_columns;a.numTileRows=config->tile_rows;
        auto& init=s->init;init.version=NV_ENC_INITIALIZE_PARAMS_VER;
        init.encodeGUID=NV_ENC_CODEC_AV1_GUID;init.presetGUID=NV_ENC_PRESET_P2_GUID;
        init.encodeWidth=init.darWidth=init.maxEncodeWidth=config->width;
        init.encodeHeight=init.darHeight=init.maxEncodeHeight=config->height;
        init.frameRateNum=120;init.frameRateDen=1;init.enablePTD=1;init.encodeConfig=&c;
        init.tuningInfo=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
        const auto initialized=s->api.nvEncInitializeEncoder(s->encoder,&init);
        if(initialized!=NV_ENC_SUCCESS) {
            const char* detail=s->api.nvEncGetLastErrorString(s->encoder);
            throw std::runtime_error("initialize AV1: NVENC status "+std::to_string(initialized)+
                (detail?std::string("; ")+detail:std::string()));
        }
        NV_ENC_CREATE_BITSTREAM_BUFFER out{};out.version=NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
        check(s->api.nvEncCreateBitstreamBuffer(s->encoder,&out),"create bitstream output");s->output=out.bitstreamBuffer;
        s->stats.size=sizeof(Av1Stats);s->stats.version=1;
        s->stats.width=config->width;s->stats.height=config->height;s->stats.fps=120;
        s->stats.bit_depth=config->bit_depth;s->stats.average_bps=config->average_bps;s->stats.peak_bps=config->peak_bps;
        s->stats.map_entries=config->enable_delta_map?((config->width+63)/64)*((config->height+63)/64):0;
        *result=s.release();last_error.clear();return 0;
    } catch(const std::exception& e) {last_error=e.what();return -1;}
}

extern "C" int AV1_CALL Av1Encode(Av1Session* s, void* texture_ptr, uint64_t frame,
    uint64_t timestamp, const int8_t* map, uint32_t count, uint32_t keyframe, Av1Packet* packet) {
    try {
        require(s && texture_ptr && packet,"Missing encode arguments");
        require(packet->size==sizeof(Av1Packet) && packet->version==1,"Unsupported packet ABI");
        std::lock_guard<std::mutex> guard(s->lock);
        require(count==s->stats.map_entries && ((count==0 && !map)||(count && map)),"Invalid AV1 64x64 delta map");
        require(keyframe<=1,"Invalid keyframe flag");
        auto* texture=static_cast<ID3D11Texture2D*>(texture_ptr);
        D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);
        ComPtr<ID3D11Device> owner;texture->GetDevice(&owner);
        require(owner.Get()==s->device.Get(),"Texture belongs to another D3D device");
        NV_ENC_BUFFER_FORMAT format=NV_ENC_BUFFER_FORMAT_UNDEFINED;
        if(desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM && s->config.input_bit_depth==8)format=NV_ENC_BUFFER_FORMAT_ARGB;
        if(desc.Format==DXGI_FORMAT_NV12 && s->config.input_bit_depth==8)format=NV_ENC_BUFFER_FORMAT_NV12;
        if(desc.Format==DXGI_FORMAT_P010 && s->config.input_bit_depth==10)format=NV_ENC_BUFFER_FORMAT_YUV420_10BIT;
        require(desc.Width==s->config.width && desc.Height==s->config.height &&
            format!=NV_ENC_BUFFER_FORMAT_UNDEFINED && desc.ArraySize==1 && desc.MipLevels==1 &&
            desc.SampleDesc.Count==1 && desc.Usage==D3D11_USAGE_DEFAULT,"Unsupported input texture layout");
        const auto start=Clock::now();
        NV_ENC_REGISTERED_PTR registered=nullptr;
        for(auto& cached:s->inputs)if(cached.texture.Get()==texture){registered=cached.registered;break;}
        if(!registered) {
            require(s->inputs.size()<16,"Input resource cache exhausted; recreate session");
            NV_ENC_REGISTER_RESOURCE reg{};reg.version=NV_ENC_REGISTER_RESOURCE_VER;
            reg.resourceType=NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;reg.resourceToRegister=texture;
            reg.width=desc.Width;reg.height=desc.Height;reg.bufferFormat=format;reg.bufferUsage=NV_ENC_INPUT_IMAGE;
            check(s->api.nvEncRegisterResource(s->encoder,&reg),"register texture");
            registered=reg.registeredResource;
            try { s->inputs.push_back({ComPtr<ID3D11Texture2D>(texture),registered}); }
            catch(...) {s->api.nvEncUnregisterResource(s->encoder,registered);throw;}
        }
        NV_ENC_MAP_INPUT_RESOURCE input{};input.version=NV_ENC_MAP_INPUT_RESOURCE_VER;input.registeredResource=registered;
        bool mapped=false,locked=false;
        try {
            check(s->api.nvEncMapInputResource(s->encoder,&input),"map texture");mapped=true;
            NV_ENC_PIC_PARAMS pic{};pic.version=NV_ENC_PIC_PARAMS_VER;
            pic.inputBuffer=input.mappedResource;pic.bufferFmt=input.mappedBufferFmt;
            pic.inputWidth=desc.Width;pic.inputHeight=desc.Height;pic.outputBitstream=s->output;
            pic.pictureStruct=NV_ENC_PIC_STRUCT_FRAME;pic.frameIdx=static_cast<uint32_t>(frame);
            pic.inputTimeStamp=timestamp;pic.qpDeltaMap=const_cast<int8_t*>(map);pic.qpDeltaMapSize=count;
            if(keyframe || s->next_keyframe)pic.encodePicFlags=NV_ENC_PIC_FLAG_FORCEIDR|NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
            ++s->stats.submitted;check(s->api.nvEncEncodePicture(s->encoder,&pic),"encode picture");
            NV_ENC_LOCK_BITSTREAM out{};out.version=NV_ENC_LOCK_BITSTREAM_VER;out.outputBitstream=s->output;
            check(s->api.nvEncLockBitstream(s->encoder,&out),"lock bitstream");locked=true;
            require(out.outputTimeStamp==timestamp,"Output timestamp reordered unexpectedly");
            const auto* begin=static_cast<const uint8_t*>(out.bitstreamBufferPtr);
            s->packet.assign(begin,begin+out.bitstreamSizeInBytes);
            packet->data=s->packet.data();packet->bytes=out.bitstreamSizeInBytes;
            packet->timestamp_us=out.outputTimeStamp;packet->frame_id=frame;
            packet->keyframe=out.pictureType==NV_ENC_PIC_TYPE_IDR || out.pictureType==NV_ENC_PIC_TYPE_I;
            check(s->api.nvEncUnlockBitstream(s->encoder,s->output),"unlock bitstream");locked=false;
            check(s->api.nvEncUnmapInputResource(s->encoder,input.mappedResource),"unmap texture");mapped=false;
            ++s->stats.completed;s->stats.encoded_bytes+=packet->bytes;s->next_keyframe=false;
            packet->encode_ms=std::chrono::duration<double,std::milli>(Clock::now()-start).count();
        } catch(...) {
            if(locked)s->api.nvEncUnlockBitstream(s->encoder,s->output);
            if(mapped)s->api.nvEncUnmapInputResource(s->encoder,input.mappedResource);
            ++s->stats.errors;throw;
        }
        last_error.clear();return 0;
    } catch(const std::exception& e){last_error=e.what();return -1;}
}

extern "C" int AV1_CALL Av1SetRate(Av1Session* s,uint32_t average,uint32_t peak) {
    try {
        require(s!=nullptr,"Missing session");rates(average,peak);
        std::lock_guard<std::mutex> guard(s->lock);
        NV_ENC_CONFIG next=s->nv_config;next.rcParams.averageBitRate=average;
        next.rcParams.maxBitRate=peak;next.rcParams.vbvBufferSize=average/120;
        NV_ENC_RECONFIGURE_PARAMS change{};change.version=NV_ENC_RECONFIGURE_PARAMS_VER;
        change.reInitEncodeParams=s->init;change.reInitEncodeParams.encodeConfig=&next;
        check(s->api.nvEncReconfigureEncoder(s->encoder,&change),"reconfigure rate");
        s->nv_config=next;s->stats.average_bps=s->config.average_bps=average;
        s->stats.peak_bps=s->config.peak_bps=peak;s->next_keyframe=true;
        last_error.clear();return 0;
    } catch(const std::exception& e){last_error=e.what();return -1;}
}
extern "C" int AV1_CALL Av1GetStats(Av1Session* s,Av1Stats* stats) {
    try {
        require(s && stats && stats->size==sizeof(Av1Stats) && stats->version==1,"Unsupported stats ABI");
        std::lock_guard<std::mutex> guard(s->lock);*stats=s->stats;return 0;
    } catch(const std::exception& e){last_error=e.what();return -1;}
}
extern "C" const char* AV1_CALL Av1LastError(){return last_error.c_str();}
extern "C" void AV1_CALL Av1Destroy(Av1Session* s){delete s;}
