#include "../driver_patterns.h"
#include <cstdio>
int wmain(int argc, wchar_t** argv) {
    using namespace inspection;
    // Small independent instruction fixture with an object offset and relative call.
    std::vector<unsigned char> function(96, 0x90);
    const unsigned char beginning[] = {0x48,0x8b,0x81,0x60,0x9a,0,0,0xe8,0x11,0x22,0x33,0x44};
    memcpy(function.data(), beginning, sizeof(beginning));
    const unsigned char objectField[] = {0x48,0x8b,0x81,0x08,0x02,0,0};
    memcpy(function.data() + 70, objectField, sizeof(objectField));
    const Mask masks[] = {{8,4}};
    auto normalized = function; std::fill(normalized.begin()+8, normalized.begin()+12, static_cast<unsigned char>(0));
    std::string digest; if (!hash(normalized,digest)) return 1;
    Pattern pattern{}; pattern.name="fixture";pattern.length=96;pattern.masks=masks;pattern.maskCount=1;pattern.digest=digest.c_str();
    std::copy(normalized.begin(),normalized.begin()+64,pattern.prefix.begin());pattern.prefixMask.fill(1);
    std::fill(pattern.prefixMask.begin()+8,pattern.prefixMask.begin()+12,static_cast<unsigned char>(0));
    Image image{};image.base=0x180000000;image.size=65536;
    image.code.push_back({0x1234,std::vector<unsigned char>(16,0xcc)});
    auto& code=image.code.front().bytes;code.insert(code.end(),function.begin(),function.end());
    auto match=qualityLayout(image,&pattern,1);
    if(!match.supported || match.startRva!=0x1244) return 2; // Derive location rather than fixed RVA.
    code[16+8]^=0x55;code[16+9]^=0x33;
    if(!qualityLayout(image,&pattern,1).supported) return 3; // Relocated call remains recognizable.
    code[16+73]^=1;
    if(qualityLayout(image,&pattern,1).supported) return 4; // Changed object offset beyond prefix rejected.
    code[16+73]^=1;code.insert(code.end(),function.begin(),function.end());
    if(qualityLayout(image,&pattern,1).supported) return 5; // Two full matching functions are ambiguous.
    if(!executableCaller(image,image.base+0x1244) || executableCaller(image,image.base-1) || executableCaller(image,image.base+0x8000)) return 6;
    IMAGE_NT_HEADERS64 nt{};size_t table{};unsigned char shortHeader[64]{};
    if(headers(shortHeader,sizeof(shortHeader),nt,table)) return 7;
    for(int i=1;i<argc;++i) {
        Image real{};if(!fileImage(argv[i],real)) return 8;
        const auto known=qualityLayout(real,qualityPatterns,std::size(qualityPatterns));
        if(!known.supported || known.startRva!=0xa23ff0) return 9;
        printf("Compatible file %d: strict quality layout recognized at RVA 0x%x\n",i,known.startRva);
    }
    puts("PASS: relocated calls, moved function, private-field mutation, ambiguous layout, executable caller bounds, malformed PE.");
    return 0;
}
