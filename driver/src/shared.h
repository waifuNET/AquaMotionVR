#pragma once
// Общая память между драйвером (пишет статистику) и приложением RollWorld (пишет трансформацию).
// Раскладка ДОЛЖНА совпадать с DriverLink.cs. Проверяется static_assert-ами ниже.
#include <atomic>
#include <cstddef>
#include <cstdint>

#define RW_SHM_NAME "Local\\RollWorld_Shm_v1"

namespace rw
{
    constexpr uint32_t kMagic = 0x444C5752; // "RWLD"
    constexpr uint32_t kVersion = 2;
    constexpr uint64_t kHeartbeatTimeoutMs = 5000; // если приложение молчит дольше - трансформация отключается

    // Флаги вида. Влияют только на устройство 0 (шлем); остальные устройства всегда трансформируются целиком.
    constexpr uint32_t kFlagHmdKeepOrientation = 1; // ориентация шлема НЕ трансформируется (вид ровный), позиция - трансформируется
    constexpr uint32_t kFlagHmdZeroVelocity = 2;    // обнулять скорости и ускорения шлема (помогает с чёрными краями на Oculus/WMR)

    struct Shared
    {
        uint32_t magic;                 // 0
        uint32_t version;               // 4
        std::atomic<uint32_t> seq;      // 8    seqlock: нечётное значение = приложение сейчас пишет
        uint32_t enabled;               // 12   1 = применять трансформацию
        double rot[4];                  // 16   поворот игрока (w, x, y, z), в осях трекинга
        double trans[3];                // 48   сдвиг игрока, м
        uint64_t heartbeatMs;           // 72   GetTickCount64() приложения
        uint64_t poseCount;             // 80   (драйвер) сколько обновлений поз прошло через хук
        uint64_t appliedCount;          // 88   (драйвер) сколько раз применена трансформация
        uint32_t hookMask;              // 96   (драйвер) бит0 = IVRServerDriverHost_005, бит1 = _006
        uint32_t driverPid;             // 100  (драйвер)
        uint32_t flags;                 // 104  (приложение) биты kFlag* ниже; старые версии приложения оставляли здесь 0
        uint32_t reserved;              // 108
        std::atomic<uint64_t> seenMask0; // 112  (драйвер) бит i = устройство i хоть раз прошло через хук _005
        std::atomic<uint64_t> seenMask1; // 120  (драйвер) бит i = устройство i хоть раз прошло через хук _006
    };                                  // 128

    static_assert(sizeof(std::atomic<uint32_t>) == 4, "atomic32 size");
    static_assert(sizeof(std::atomic<uint64_t>) == 8, "atomic64 size");
    static_assert(sizeof(Shared) == 128, "Shared size");
    static_assert(offsetof(Shared, seq) == 8, "seq");
    static_assert(offsetof(Shared, rot) == 16, "rot");
    static_assert(offsetof(Shared, trans) == 48, "trans");
    static_assert(offsetof(Shared, heartbeatMs) == 72, "heartbeat");
    static_assert(offsetof(Shared, poseCount) == 80, "poseCount");
    static_assert(offsetof(Shared, appliedCount) == 88, "appliedCount");
    static_assert(offsetof(Shared, hookMask) == 96, "hookMask");
    static_assert(offsetof(Shared, driverPid) == 100, "driverPid");
    static_assert(offsetof(Shared, flags) == 104, "flags");
    static_assert(offsetof(Shared, seenMask0) == 112, "seenMask0");
    static_assert(offsetof(Shared, seenMask1) == 120, "seenMask1");
}