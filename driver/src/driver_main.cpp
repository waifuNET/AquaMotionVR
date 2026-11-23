// RollWorld driver: перехватывает обновления поз всех устройств в vrserver и применяет к ним
// трансформацию из общей памяти (полный 3D-поворот + сдвиг). Управляет им приложение RollWorld.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <openvr_driver.h>

#include "MinHook.h"
#include "shared.h"
#include "transform.h"

#define DRIVER_EXPORT extern "C" __declspec(dllexport)

// ------------------------------------------------------------------ лог
static void Log(const char* fmt, ...)
{
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf - 2, fmt, ap);
    va_end(ap);
    size_t n = strlen(buf);
    buf[n] = '\n'; buf[n + 1] = 0;
    if (vr::VRDriverLog()) vr::VRDriverLog()->Log(buf);
}

// ------------------------------------------------------------------ общая память
static rw::Shared* g_shm = nullptr;
static HANDLE g_map = nullptr;

static bool CreateShared()
{
    g_map = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(rw::Shared), RW_SHM_NAME);
    if (!g_map) { Log("RollWorld: CreateFileMapping failed (%lu)", GetLastError()); return false; }
    void* p = MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(rw::Shared));
    if (!p) { Log("RollWorld: MapViewOfFile failed (%lu)", GetLastError()); CloseHandle(g_map); g_map = nullptr; return false; }
    g_shm = static_cast<rw::Shared*>(p);
    memset(static_cast<void*>(g_shm), 0, sizeof(rw::Shared));
    g_shm->version = rw::kVersion;
    g_shm->driverPid = GetCurrentProcessId();
    g_shm->magic = rw::kMagic; // последним - по нему приложение понимает, что структура готова
    return true;
}

static void DestroyShared()
{
    if (g_shm) { UnmapViewOfFile(g_shm); g_shm = nullptr; }
    if (g_map) { CloseHandle(g_map); g_map = nullptr; }
}

/** Читает трансформацию по seqlock. false - не удалось прочитать согласованно (пропускаем позу без изменений). */
static bool ReadTransform(rw::Transform& out)
{
    rw::Shared* s = g_shm;
    if (!s) return false;

    for (int attempt = 0; attempt < 200; ++attempt)
    {
        uint32_t s1 = s->seq.load(std::memory_order_acquire);
        if (s1 & 1u) { YieldProcessor(); continue; }

        uint32_t en = *reinterpret_cast<const volatile uint32_t*>(&s->enabled);
        double r[4], t[3];
        for (int i = 0; i < 4; i++) r[i] = *reinterpret_cast<const volatile double*>(&s->rot[i]);
        for (int i = 0; i < 3; i++) t[i] = *reinterpret_cast<const volatile double*>(&s->trans[i]);
        uint64_t hb = *reinterpret_cast<const volatile uint64_t*>(&s->heartbeatMs);
        uint32_t fl = *reinterpret_cast<const volatile uint32_t*>(&s->flags);

        std::atomic_thread_fence(std::memory_order_acquire);
        if (s->seq.load(std::memory_order_relaxed) != s1) continue;

        out.q = { r[0], r[1], r[2], r[3] };
        out.t[0] = t[0]; out.t[1] = t[1]; out.t[2] = t[2];
        bool alive = (GetTickCount64() - hb) < rw::kHeartbeatTimeoutMs; // приложение живо?
        out.enabled = en != 0 && alive && rw::Len2(out.q) > 1e-9;
        if (out.enabled) out.q = rw::Normalized(out.q);
        out.hmdKeepOrientation = (fl & rw::kFlagHmdKeepOrientation) != 0;
        out.hmdZeroVelocity = (fl & rw::kFlagHmdZeroVelocity) != 0;
        return true;
    }
    return false;
}

// ------------------------------------------------------------------ хуки
using PoseUpdatedFn = void (*)(void* self, uint32_t dev, const vr::DriverPose_t& pose, uint32_t size);
static PoseUpdatedFn g_orig[2] = { nullptr, nullptr }; // [0] = IVRServerDriverHost_005, [1] = _006
static void* g_self[2] = { nullptr, nullptr };         // "this" хоста для каждой версии - нужен для досылки поз

// ------------------------------------------------------------------ кэш последней "сырой" позы каждого устройства
// Нужен, чтобы досылать актуальную трансформацию устройствам, которые сами давно не присылали позу
// (типичный случай - неподвижный контроллер: часть драйверов реже шлют позу без движения, и тогда
// SteamVR продолжает использовать ту трансформацию, что была на момент последнего вызова, - а она уже
// устарела, если игрок за это время успел уплыть/повернуться дальше). См. RunFrame ниже.
struct CachedPose
{
    std::atomic<bool> valid{ false };
    std::mutex mtx;
    vr::DriverPose_t raw{};
    uint32_t size = 0;
    int hookIdx = 0;
};
static CachedPose g_cache[vr::k_unMaxTrackedDeviceCount];

// Защита от двойного применения: в некоторых сборках SteamVR интерфейс _005 - это переходник,
// который внутри вызывает ту же функцию _006. Без этого счётчика поза, пришедшая через _005,
// трансформировалась бы дважды, а пришедшая через _006 - один раз, и устройства разных драйверов
// (шлем и контроллеры) разъезжались бы тем сильнее, чем больше накоплено движение.
static thread_local int g_inHook = 0;

static void Process(int idx, void* self, uint32_t dev, const vr::DriverPose_t& in, uint32_t size)
{
    PoseUpdatedFn orig = g_orig[idx];
    if (g_inHook) { orig(self, dev, in, size); return; } // трансформация уже применена выше по стеку

    if (!g_self[idx]) g_self[idx] = self; // "this" хоста нужен позже для досылки поз (RunFrame)

    if (g_shm)
    {
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_shm->poseCount));
        if (dev < 64)
        {
            auto& mask = (idx == 0) ? g_shm->seenMask0 : g_shm->seenMask1;
            mask.fetch_or(1ULL << dev, std::memory_order_relaxed);
        }
    }

    // запоминаем сырую (недо трансформированную) позу - на случай, если устройство надолго замолчит
    if (dev < vr::k_unMaxTrackedDeviceCount)
    {
        CachedPose& c = g_cache[dev];
        std::lock_guard<std::mutex> lock(c.mtx);
        c.raw = in;
        c.size = size;
        c.hookIdx = idx;
        c.valid.store(true, std::memory_order_release);
    }

    rw::Transform tf;
    if (dev < vr::k_unMaxTrackedDeviceCount && ReadTransform(tf) && tf.enabled)
    {
        vr::DriverPose_t pose;
        memset(&pose, 0, sizeof pose);
        size_t n = std::min<size_t>(size, sizeof pose); // старые драйверы могут передавать структуру меньшего размера
        memcpy(&pose, &in, n);
        rw::ApplyToDevice(tf, pose, dev == vr::k_unTrackedDeviceIndex_Hmd);
        if (g_shm) InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_shm->appliedCount));
        g_inHook++;
        orig(self, dev, pose, size);
        g_inHook--;
        return;
    }
    g_inHook++;
    orig(self, dev, in, size);
    g_inHook--;
}

static void Detour005(void* self, uint32_t dev, const vr::DriverPose_t& p, uint32_t sz) { Process(0, self, dev, p, sz); }
static void Detour006(void* self, uint32_t dev, const vr::DriverPose_t& p, uint32_t sz) { Process(1, self, dev, p, sz); }

static bool HookHost(vr::IVRDriverContext* ctx, const char* version, int idx, PoseUpdatedFn detour)
{
    vr::EVRInitError err = vr::VRInitError_None;
    void* host = ctx->GetGenericInterface(version, &err);
    if (!host) { Log("RollWorld: интерфейс %s недоступен (ошибка %d)", version, static_cast<int>(err)); return false; }

    void** vtbl = *reinterpret_cast<void***>(host);
    void* target = vtbl[1]; // IVRServerDriverHost::TrackedDevicePoseUpdated

    MH_STATUS st = MH_CreateHook(target, reinterpret_cast<void*>(detour), reinterpret_cast<void**>(&g_orig[idx]));
    if (st == MH_ERROR_ALREADY_CREATED) { Log("RollWorld: %s - функция уже перехвачена (общая с другой версией)", version); return true; }
    if (st != MH_OK) { Log("RollWorld: MH_CreateHook(%s) = %s", version, MH_StatusToString(st)); return false; }
    st = MH_EnableHook(target);
    if (st != MH_OK) { Log("RollWorld: MH_EnableHook(%s) = %s", version, MH_StatusToString(st)); return false; }
    Log("RollWorld: хук на %s установлен", version);
    return true;
}

// ------------------------------------------------------------------ досылка "застоявшихся" поз
// Раз в кадр обновляем трансформацию у всех устройств заново, из их последней известной сырой позы -
// даже если само устройство сейчас ничего не прислало. Так контроллер, который просто лежит неподвижно
// в руке, не "зависает" со старой трансформацией, пока не шевельнётся: он всё равно едет вместе с миром.
static void RebroadcastStalePoses()
{
    rw::Transform tf;
    if (!ReadTransform(tf) || !tf.enabled) return;

    for (uint32_t dev = 0; dev < vr::k_unMaxTrackedDeviceCount; dev++)
    {
        CachedPose& c = g_cache[dev];
        if (!c.valid.load(std::memory_order_acquire)) continue;

        vr::DriverPose_t pose;
        uint32_t size;
        int idx;
        {
            std::lock_guard<std::mutex> lock(c.mtx);
            pose = c.raw;
            size = c.size;
            idx = c.hookIdx;
        }
        if (!g_self[idx] || !g_orig[idx]) continue;

        rw::ApplyToDevice(tf, pose, dev == vr::k_unTrackedDeviceIndex_Hmd);
        if (g_shm) InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_shm->appliedCount));
        g_inHook++;
        g_orig[idx](g_self[idx], dev, pose, size); // вызываем оригинал напрямую - наш детур не сработает повторно
        g_inHook--;
    }
}

// ------------------------------------------------------------------ провайдер
class RollWorldProvider : public vr::IServerTrackedDeviceProvider
{
public:
    vr::EVRInitError Init(vr::IVRDriverContext* ctx) override
    {
        VR_INIT_SERVER_DRIVER_CONTEXT(ctx);
        Log("RollWorld: драйвер запущен");

        if (!CreateShared()) return vr::VRInitError_Driver_Failed;

        MH_STATUS st = MH_Initialize();
        if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED)
        {
            Log("RollWorld: MH_Initialize = %s", MH_StatusToString(st));
            return vr::VRInitError_Driver_Failed;
        }

        uint32_t mask = 0;
        if (HookHost(ctx, "IVRServerDriverHost_006", 1, Detour006)) mask |= 2;
        if (HookHost(ctx, "IVRServerDriverHost_005", 0, Detour005)) mask |= 1;
        g_shm->hookMask = mask;
        if (!mask) Log("RollWorld: ни один хук не установлен, трансформация работать не будет");
        return vr::VRInitError_None;
    }

    void Cleanup() override
    {
        MH_DisableHook(MH_ALL_HOOKS);
        Sleep(50); // дать уйти вызовам, которые уже внутри детура
        MH_Uninitialize();
        DestroyShared();
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
    }

    const char* const* GetInterfaceVersions() override { return vr::k_InterfaceVersions; }
    void RunFrame() override { RebroadcastStalePoses(); }
    bool ShouldBlockStandbyMode() override { return false; }
    void EnterStandby() override {}
    void LeaveStandby() override {}
};

static RollWorldProvider g_provider;

DRIVER_EXPORT void* HmdDriverFactory(const char* pInterfaceName, int* pReturnCode)
{
    if (strcmp(vr::IServerTrackedDeviceProvider_Version, pInterfaceName) == 0) return &g_provider;
    if (pReturnCode) *pReturnCode = vr::VRInitError_Init_InterfaceNotFound;
    return nullptr;
}