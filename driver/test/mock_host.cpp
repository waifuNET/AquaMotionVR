// Тест драйвера без SteamVR: подделка vrserver (IVRDriverContext + IVRServerDriverHost) + "приложение",
// которое пишет трансформацию в общую память. Собирается mingw'ом, запускается в Windows или Wine.
#include <windows.h>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <openvr_driver.h>
#include "../src/shared.h"
#include "../src/transform.h"

static int g_fail = 0;
#define CHECK(cond, ...) do { if (!(cond)) { printf("  FAIL: " __VA_ARGS__); printf("\n"); g_fail++; } else { printf("  ok:   " __VA_ARGS__); printf("\n"); } } while (0)

// ---- поддельный vrserver ----
struct MockLog : vr::IVRDriverLog { void Log(const char* m) override { printf("    [driver log] %s", m); } };

template <int N>
struct MockHost : vr::IVRServerDriverHost
{
    vr::DriverPose_t last{};
    uint32_t lastDev = 0, lastSize = 0;
    int calls = 0;
    bool TrackedDeviceAdded(const char*, vr::ETrackedDeviceClass, vr::ITrackedDeviceServerDriver*) override { return true; }
#if defined(__GNUC__)
    __attribute__((noinline))
#endif
    void TrackedDevicePoseUpdated(uint32_t dev, const vr::DriverPose_t& p, uint32_t sz) override { last = p; lastDev = dev; lastSize = sz; calls += N; }
    void VsyncEvent(double) override {}
    void VendorSpecificEvent(uint32_t, vr::EVREventType, const vr::VREvent_Data_t&, double) override {}
    bool IsExiting() override { return false; }
    bool PollNextEvent(vr::VREvent_t*, uint32_t) override { return false; }
    void GetRawTrackedDevicePoses(float, vr::TrackedDevicePose_t*, uint32_t) override {}
    void RequestRestart(const char*, const char*, const char*, const char*) override {}
    uint32_t GetFrameTimings(vr::Compositor_FrameTiming*, uint32_t) override { return 0; }
    void SetDisplayEyeToHead(uint32_t, const vr::HmdMatrix34_t&, const vr::HmdMatrix34_t&) override {}
    void SetDisplayProjectionRaw(uint32_t, const vr::HmdRect2_t&, const vr::HmdRect2_t&) override {}
    void SetRecommendedRenderTargetSize(uint32_t, uint32_t, uint32_t) override {}
};

static MockHost<1> host006;
static MockHost<2> host005;
static MockLog mockLog;
static char dummy[256];

struct MockContext : vr::IVRDriverContext
{
    void* GetGenericInterface(const char* v, vr::EVRInitError* e) override
    {
        if (e) *e = vr::VRInitError_None;
        if (!strcmp(v, "IVRServerDriverHost_006")) return static_cast<vr::IVRServerDriverHost*>(&host006);
        if (!strcmp(v, "IVRServerDriverHost_005")) return static_cast<vr::IVRServerDriverHost*>(&host005);
        if (!strcmp(v, vr::IVRDriverLog_Version)) return static_cast<vr::IVRDriverLog*>(&mockLog);
        if (!strcmp(v, vr::IVRSettings_Version) || !strcmp(v, vr::IVRProperties_Version) ||
            !strcmp(v, vr::IVRDriverManager_Version) || !strcmp(v, vr::IVRResources_Version)) return dummy;
        if (e) *e = vr::VRInitError_Init_InterfaceNotFound;
        return nullptr;
    }
    vr::DriverHandle_t GetDriverHandle() override { return 1; }
};

// ---- "приложение": пишет трансформацию как DriverLink.cs ----
static rw::Shared* shm;
static void AppWrite(bool enabled, const double q[4], const double t[3], uint64_t hb, bool leaveOdd = false, uint32_t flags = 0)
{
    uint32_t s = shm->seq.load();
    shm->seq.store(s + 1);
    shm->enabled = enabled ? 1 : 0;
    shm->flags = flags;
    for (int i = 0; i < 4; i++) shm->rot[i] = q[i];
    for (int i = 0; i < 3; i++) shm->trans[i] = t[i];
    shm->heartbeatMs = hb;
    if (!leaveOdd) shm->seq.store(s + 2);
}

static vr::DriverPose_t MakePose()
{
    vr::DriverPose_t p; memset(&p, 0, sizeof p);
    p.qWorldFromDriverRotation = { 1, 0, 0, 0 };
    p.qDriverFromHeadRotation = { 1, 0, 0, 0 };
    p.qRotation = { 1, 0, 0, 0 };
    p.vecPosition[0] = 0.1; p.vecPosition[1] = 1.6; p.vecPosition[2] = -0.3;
    p.vecWorldFromDriverTranslation[0] = 0.5; p.vecWorldFromDriverTranslation[1] = 0; p.vecWorldFromDriverTranslation[2] = 1.0;
    p.poseIsValid = true; p.deviceIsConnected = true; p.result = vr::TrackingResult_Running_OK;
    return p;
}

int main()
{
    printf("== загрузка драйвера ==\n");
    HMODULE dll = LoadLibraryA("driver_01rollworld.dll");
    CHECK(dll != nullptr, "LoadLibrary");
    if (!dll) return 1;
    typedef void* (*Factory)(const char*, int*);
    Factory factory = (Factory)GetProcAddress(dll, "HmdDriverFactory");
    CHECK(factory != nullptr, "экспорт HmdDriverFactory");
    int rc = 0;
    auto* provider = (vr::IServerTrackedDeviceProvider*)factory(vr::IServerTrackedDeviceProvider_Version, &rc);
    CHECK(provider != nullptr, "фабрика вернула провайдер");
    void* bad = factory("IServerTrackedDeviceProvider_999", &rc);
    CHECK(bad == nullptr && rc == vr::VRInitError_Init_InterfaceNotFound, "чужая версия интерфейса отклоняется");

    MockContext ctx;
    auto err = provider->Init(&ctx);
    CHECK(err == vr::VRInitError_None, "Init вернул None (%d)", (int)err);

    HANDLE m = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, RW_SHM_NAME);
    CHECK(m != nullptr, "общая память создана драйвером");
    shm = (rw::Shared*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(rw::Shared));
    CHECK(shm && shm->magic == rw::kMagic && shm->version == rw::kVersion, "magic/version");
    CHECK(shm->hookMask == 3, "установлены оба хука (маска %u)", shm->hookMask);
    CHECK(shm->driverPid == GetCurrentProcessId(), "pid драйвера");

    // интерфейсы берём так же, как драйверы у vrserver - через GetGenericInterface (компилятор не сможет "выпрямить" вызов мимо vtable)
    vr::IVRServerDriverHost* h = static_cast<vr::IVRServerDriverHost*>(ctx.GetGenericInterface("IVRServerDriverHost_006", nullptr));
    vr::IVRServerDriverHost* h005 = static_cast<vr::IVRServerDriverHost*>(ctx.GetGenericInterface("IVRServerDriverHost_005", nullptr));
    double id[4] = { 1, 0, 0, 0 }, zero[3] = { 0, 0, 0 };
    uint64_t now = GetTickCount64();

    printf("== выключено: поза проходит без изменений ==\n");
    { auto p = MakePose(); h->TrackedDevicePoseUpdated(3, p, sizeof p);
      CHECK(host006.calls == 1 && host006.lastDev == 3, "поза дошла до vrserver");
      CHECK(memcmp(&host006.last, &p, sizeof p) == 0, "поза не изменена");
      CHECK(shm->poseCount == 1 && shm->appliedCount == 0, "счётчики: поз=%llu применено=%llu", (unsigned long long)shm->poseCount, (unsigned long long)shm->appliedCount); }

    printf("== включено: поворот 90° вокруг X + сдвиг ==\n");
    { double a = 3.14159265358979323846 / 4; // половина угла для 90°
      double q[4] = { std::cos(a), std::sin(a), 0, 0 }; double t[3] = { 1, 2, 3 };
      AppWrite(true, q, t, now);
      auto p = MakePose(); h->TrackedDevicePoseUpdated(0, p, sizeof p);
      auto& r = host006.last;
      // ожидание: q' = q * q_old ; t' = R(q) * t_old + t
      rw::Transform tf; tf.q = { q[0], q[1], q[2], q[3] }; tf.t[0] = 1; tf.t[1] = 2; tf.t[2] = 3;
      double rt[3]; rw::Rotate(tf.q, p.vecWorldFromDriverTranslation, rt);
      CHECK(std::fabs(r.qWorldFromDriverRotation.w - q[0]) < 1e-9 && std::fabs(r.qWorldFromDriverRotation.x - q[1]) < 1e-9, "кватернион мира скомпонован");
      CHECK(std::fabs(r.vecWorldFromDriverTranslation[0] - (rt[0] + 1)) < 1e-9 &&
            std::fabs(r.vecWorldFromDriverTranslation[1] - (rt[1] + 2)) < 1e-9 &&
            std::fabs(r.vecWorldFromDriverTranslation[2] - (rt[2] + 3)) < 1e-9, "сдвиг: (%.3f, %.3f, %.3f)", r.vecWorldFromDriverTranslation[0], r.vecWorldFromDriverTranslation[1], r.vecWorldFromDriverTranslation[2]);
      // R(90° вокруг X) * (0.5, 0, 1) = (0.5, -1, 0) -> + (1,2,3) = (1.5, 1, 3)
      CHECK(std::fabs(r.vecWorldFromDriverTranslation[0] - 1.5) < 1e-9 && std::fabs(r.vecWorldFromDriverTranslation[1] - 1.0) < 1e-9 && std::fabs(r.vecWorldFromDriverTranslation[2] - 3.0) < 1e-9, "ожидаемые числа (1.5, 1, 3)");
      CHECK(r.vecPosition[1] == 1.6 && r.poseIsValid, "остальные поля позы не тронуты");
      CHECK(shm->appliedCount == 1, "счётчик применений"); }

    printf("== нулевой кватернион мира (некоторые драйверы) ==\n");
    { double a = 3.14159265358979323846 / 4; double q[4] = { std::cos(a), 0, std::sin(a), 0 };
      AppWrite(true, q, zero, now);
      auto p = MakePose(); p.qWorldFromDriverRotation = { 0, 0, 0, 0 };
      h->TrackedDevicePoseUpdated(1, p, sizeof p);
      CHECK(std::fabs(host006.last.qWorldFromDriverRotation.w - q[0]) < 1e-9 && std::fabs(host006.last.qWorldFromDriverRotation.y - q[2]) < 1e-9, "w=0 трактуется как единичный"); }

    printf("== старая структура меньшего размера ==\n");
    { AppWrite(true, id, zero, now);
      auto p = MakePose(); uint32_t small = 200; h->TrackedDevicePoseUpdated(2, p, small);
      CHECK(host006.lastSize == small, "размер структуры передан как есть (%u)", host006.lastSize); }

    printf("== хук на версию 005 ==\n");
    { double q[4] = { std::cos(0.3), 0, 0, std::sin(0.3) }; double t[3] = { 0, 5, 0 };
      AppWrite(true, q, t, now);
      auto p = MakePose(); h005->TrackedDevicePoseUpdated(4, p, sizeof p);
      CHECK(host005.calls == 2 && std::fabs(host005.last.vecWorldFromDriverTranslation[1] - (5 + 0.5 * std::sin(0.6))) < 1e-9, "005 тоже трансформируется"); }

    printf("== ровный вид (kFlagHmdKeepOrientation): шлем не вращается, но двигается ==\n");
    { // поворот 90° вокруг Y + сдвиг; у драйвера уже есть свой мир (поворот 30° вокруг Z и сдвиг), шлем повёрнут
      double a = 3.14159265358979323846 / 4; double q[4] = { std::cos(a), 0, std::sin(a), 0 }; double t[3] = { 1, 2, 3 };
      AppWrite(true, q, t, GetTickCount64(), false, rw::kFlagHmdKeepOrientation);
      auto p = MakePose(); p.qWorldFromDriverRotation = { std::cos(0.26), 0, 0, std::sin(0.26) };
      p.qRotation = { std::cos(0.4), std::sin(0.4), 0, 0 };
      p.vecVelocity[0] = 1; p.vecVelocity[1] = 0; p.vecVelocity[2] = 0;
      h->TrackedDevicePoseUpdated(0, p, sizeof p);
      auto r = host006.last;
      auto worldPos = [](const vr::DriverPose_t& d, double out[3]) { rw::Rotate(rw::Normalized(d.qWorldFromDriverRotation), d.vecPosition, out); for (int i = 0; i < 3; i++) out[i] += d.vecWorldFromDriverTranslation[i]; };
      double w0[3], w1[3], ex[3];
      worldPos(p, w0); worldPos(r, w1);
      rw::Transform tf; tf.q = { q[0], q[1], q[2], q[3] }; for (int i = 0; i < 3; i++) tf.t[i] = t[i];
      rw::Rotate(tf.q, w0, ex); for (int i = 0; i < 3; i++) ex[i] += t[i];
      CHECK(std::fabs(w1[0] - ex[0]) < 1e-9 && std::fabs(w1[1] - ex[1]) < 1e-9 && std::fabs(w1[2] - ex[2]) < 1e-9, "позиция шлема = X(позиция): (%.4f, %.4f, %.4f)", w1[0], w1[1], w1[2]);
      auto qw0 = rw::Mul(rw::Normalized(p.qWorldFromDriverRotation), p.qRotation), qw1 = rw::Mul(rw::Normalized(r.qWorldFromDriverRotation), r.qRotation);
      CHECK(std::fabs(qw0.w - qw1.w) < 1e-9 && std::fabs(qw0.x - qw1.x) < 1e-9 && std::fabs(qw0.y - qw1.y) < 1e-9 && std::fabs(qw0.z - qw1.z) < 1e-9, "мировая ориентация шлема не изменилась");
      // скорость в осях драйвера: v' = Rw^-1 Rx Rw v  ->  в мире это Rx * (Rw v)
      double v0[3], v1[3], vex[3]; rw::Rotate(rw::Normalized(p.qWorldFromDriverRotation), p.vecVelocity, v0); rw::Rotate(tf.q, v0, vex);
      rw::Rotate(rw::Normalized(r.qWorldFromDriverRotation), r.vecVelocity, v1);
      CHECK(std::fabs(v1[0] - vex[0]) < 1e-9 && std::fabs(v1[1] - vex[1]) < 1e-9 && std::fabs(v1[2] - vex[2]) < 1e-9, "линейная скорость шлема повёрнута как позиция");
      // остальные устройства при том же флаге трансформируются целиком
      auto pc = MakePose(); h->TrackedDevicePoseUpdated(3, pc, sizeof pc);
      auto qc = rw::Mul(rw::Normalized(host006.last.qWorldFromDriverRotation), host006.last.qRotation);
      CHECK(std::fabs(qc.y - std::sin(a)) < 1e-9 && std::fabs(qc.w - std::cos(a)) < 1e-9, "контроллер (устройство 3) поворачивается целиком");
      // выключено -> шлем проходит без изменений даже с флагом
      AppWrite(false, id, zero, GetTickCount64(), false, rw::kFlagHmdKeepOrientation);
      auto p2 = MakePose(); h->TrackedDevicePoseUpdated(0, p2, sizeof p2);
      CHECK(memcmp(&host006.last, &p2, sizeof p2) == 0, "enabled=0 -> шлем без изменений"); }

    printf("== обнуление скоростей шлема (kFlagHmdZeroVelocity) ==\n");
    { double q[4] = { std::cos(0.3), 0, 0, std::sin(0.3) }; double t[3] = { 0, 1, 0 };
      AppWrite(true, q, t, GetTickCount64(), false, rw::kFlagHmdZeroVelocity);
      auto p = MakePose(); p.vecVelocity[0] = 1; p.vecAngularVelocity[1] = 2; p.vecAcceleration[2] = 3; p.vecAngularAcceleration[0] = 4;
      h->TrackedDevicePoseUpdated(0, p, sizeof p);
      auto& r = host006.last;
      CHECK(r.vecVelocity[0] == 0 && r.vecAngularVelocity[1] == 0 && r.vecAcceleration[2] == 0 && r.vecAngularAcceleration[0] == 0, "скорости и ускорения шлема = 0");
      auto qw = rw::Mul(rw::Normalized(r.qWorldFromDriverRotation), r.qRotation);
      CHECK(std::fabs(qw.w - std::cos(0.3)) < 1e-9 && std::fabs(qw.z - std::sin(0.3)) < 1e-9, "а трансформация шлема осталась полной");
      auto pc = MakePose(); pc.vecVelocity[0] = 1; h->TrackedDevicePoseUpdated(3, pc, sizeof pc);
      CHECK(host006.last.vecVelocity[0] == 1, "у контроллера скорость не тронута"); }

    printf("== защита: приложение не подаёт признаков жизни ==\n");
    { AppWrite(true, id, zero, now - 6000);
      double q[4] = { std::cos(0.3), 0, 0, std::sin(0.3) }; double t[3] = { 0, 5, 0 };
      AppWrite(true, q, t, GetTickCount64() - 6000);
      auto p = MakePose(); h->TrackedDevicePoseUpdated(5, p, sizeof p);
      CHECK(memcmp(&host006.last, &p, sizeof p) == 0, "устаревший heartbeat -> поза без изменений"); }

    printf("== защита: писатель завис в середине записи (seq нечётный) ==\n");
    { double q[4] = { std::cos(0.3), 0, 0, std::sin(0.3) }; double t[3] = { 0, 5, 0 };
      AppWrite(true, q, t, GetTickCount64(), true);
      auto p = MakePose(); DWORD t0 = GetTickCount(); h->TrackedDevicePoseUpdated(6, p, sizeof p); DWORD dt = GetTickCount() - t0;
      CHECK(memcmp(&host006.last, &p, sizeof p) == 0, "поза проходит без изменений");
      CHECK(dt < 50, "и не зависает (%lu мс)", dt);
      shm->seq.store(shm->seq.load() + 1); }

    printf("== устройство с недопустимым номером ==\n");
    { AppWrite(true, id, zero, GetTickCount64());
      double q[4] = { std::cos(0.3), 0, 0, std::sin(0.3) }; double t[3] = { 0, 5, 0 };
      AppWrite(true, q, t, GetTickCount64());
      auto p = MakePose(); h->TrackedDevicePoseUpdated(9999, p, sizeof p);
      CHECK(memcmp(&host006.last, &p, sizeof p) == 0, "номер >= 64 не трогаем"); }

    printf("== выключение и выгрузка ==\n");
    { AppWrite(false, id, zero, GetTickCount64());
      auto p = MakePose(); h->TrackedDevicePoseUpdated(7, p, sizeof p);
      CHECK(memcmp(&host006.last, &p, sizeof p) == 0, "enabled=0 -> без изменений");
      provider->Cleanup();
      auto p2 = MakePose(); int before = host006.calls; h->TrackedDevicePoseUpdated(8, p2, sizeof p2);
      CHECK(host006.calls == before + 1, "после Cleanup vrserver работает как раньше");
      HANDLE m2 = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, RW_SHM_NAME);
      // наше собственное отображение ещё держит объект - закрываем его
      UnmapViewOfFile(shm); CloseHandle(m);
      if (m2) CloseHandle(m2);
      HANDLE m3 = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, RW_SHM_NAME);
      CHECK(m3 == nullptr, "общая память освобождена"); }

    printf("\nИтого: %s (%d ошибок)\n", g_fail ? "ЕСТЬ ОШИБКИ" : "всё в порядке", g_fail);
    return g_fail ? 1 : 0;
}
