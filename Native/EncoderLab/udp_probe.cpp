#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <algorithm>
#include <charconv>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <vector>

// Original, bounded UDP delivery experiment. A receiver must already be running
// at the explicitly supplied IPv4 address, port 39764. Payload is synthetic.
static int number(const char* text, int minimum, int maximum) {
    int value = 0;
    const auto end = text + std::strlen(text);
    const auto result = std::from_chars(text, end, value);
    if (result.ec != std::errc{} || result.ptr != end || value < minimum || value > maximum)
        throw std::runtime_error("Invalid numeric argument");
    return value;
}

struct Resources {
    bool winsock = false;
    SOCKET socket = INVALID_SOCKET;
    HANDLE timer = nullptr;
    ~Resources() {
        if (timer) CloseHandle(timer);
        if (socket != INVALID_SOCKET) closesocket(socket);
        if (winsock) WSACleanup();
    }
};

int main(int argc, char** argv) {
    if (argc != 6) {
        std::cerr << "Usage: udp_probe IPV4 MBPS PACKET_BYTES SECONDS BATCH_MICROSECONDS\n";
        return 2;
    }
    sockaddr_in peer{};
    int mbps, size, seconds, burstUs;
    try {
        // Complete validation before initializing Winsock or creating a socket.
        mbps = number(argv[2], 10, 650);
        size = number(argv[3], 256, 1400);
        seconds = number(argv[4], 1, 15);
        burstUs = number(argv[5], 100, 12000);
        peer.sin_family = AF_INET;
        peer.sin_port = htons(39764);
        if (InetPtonA(AF_INET, argv[1], &peer.sin_addr) != 1)
            throw std::runtime_error("Destination must be an explicit IPv4 address");
        const auto host = ntohl(peer.sin_addr.s_addr);
        if (!host || host == 0xffffffffU || (host & 0xf0000000U) == 0xe0000000U)
            throw std::runtime_error("Destination must be a unicast IPv4 address");
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 2;
    }

    Resources resources;
    try {
        WSADATA w{};
        if (WSAStartup(MAKEWORD(2, 2), &w)) throw std::runtime_error("WSAStartup failed");
        resources.winsock = true;
        resources.socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (resources.socket == INVALID_SOCKET) throw std::runtime_error("socket failed");
        int buffer = 1024 * 1024;
        const DWORD sendTimeout = 2000;
        if (setsockopt(resources.socket, SOL_SOCKET, SO_SNDBUF,
                       reinterpret_cast<const char*>(&buffer), sizeof(buffer)) ||
            setsockopt(resources.socket, SOL_SOCKET, SO_SNDTIMEO,
                       reinterpret_cast<const char*>(&sendTimeout), sizeof(sendTimeout)))
            throw std::runtime_error("Socket bounds could not be applied");
        if (connect(resources.socket, reinterpret_cast<const sockaddr*>(&peer), sizeof(peer)))
            throw std::runtime_error("UDP endpoint selection failed");
        resources.timer = CreateWaitableTimerExW(nullptr, nullptr,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (!resources.timer) throw std::runtime_error("High-resolution timer unavailable");

        std::vector<char> packet(size);
        for (int i = 0; i < size; ++i) packet[i] = static_cast<char>(i * 139 + 71);
        const uint64_t total = static_cast<uint64_t>(mbps) * 1000000 * seconds / (size * 8);
        uint64_t sent = 0, errors = 0, sendUs = 0, maxSendUs = 0, slowSends = 0;
        using Clock = std::chrono::steady_clock;
        const auto start = Clock::now();
        const uint64_t groups = static_cast<uint64_t>(seconds) * 1000000 / burstUs;
        for (uint64_t group = 0; group < groups; ++group) {
            const auto deadline = start + std::chrono::microseconds(group * burstUs);
            const auto remain = std::chrono::duration_cast<std::chrono::microseconds>(deadline - Clock::now()).count();
            if (remain > 160) {
                LARGE_INTEGER due{};
                due.QuadPart = -(remain - 100) * 10;
                if (!SetWaitableTimer(resources.timer, &due, 0, nullptr, nullptr, FALSE) ||
                    WaitForSingleObject(resources.timer, 2100) != WAIT_OBJECT_0)
                    throw std::runtime_error("Bounded pacing wait failed");
            }
            while (Clock::now() < deadline) YieldProcessor();
            const uint64_t end = total * (group + 1) / groups;
            while (sent < end) {
                std::memcpy(packet.data(), &sent, sizeof(sent));
                const auto began = Clock::now();
                const int result = send(resources.socket, packet.data(), size, 0);
                const auto us = static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - began).count());
                sendUs += us;
                maxSendUs = (std::max)(maxSendUs, us);
                slowSends += us > 1000;
                if (result != size) { ++errors; break; }
                ++sent;
            }
            if (errors) break;
        }
        const double elapsed = std::chrono::duration<double>(Clock::now() - start).count();
        FILETIME created{}, exited{}, kernel{}, user{};
        if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user))
            throw std::runtime_error("CPU-time query failed");
        ULARGE_INTEGER k{}, u{};
        k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
        u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
        std::cout << "{\"sent_packets\":" << sent << ",\"payload_bytes\":" << sent * size
                  << ",\"send_errors\":" << errors << ",\"seconds\":" << elapsed
                  << ",\"mbps\":" << sent * size * 8 / elapsed / 1e6 << ",\"batch_us\":" << burstUs
                  << ",\"send_total_ms\":" << sendUs / 1000.0 << ",\"max_send_ms\":" << maxSendUs / 1000.0
                  << ",\"send_over_1ms\":" << slowSends
                  << ",\"process_cpu_ms\":" << (k.QuadPart + u.QuadPart) / 10000.0 << "}\n";
        return errors ? 1 : 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}