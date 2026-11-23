using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;

enum DriverState { NotFound = 0, Connected = 1, HooksInactive = 2 }

/// <summary>
/// Связь с драйвером RollWorld через общую память. Раскладка совпадает с driver/src/shared.h.
/// Мы пишем трансформацию игрока (поворот + сдвиг) - драйвер применяет её ко всем позам в SteamVR.
/// </summary>
sealed unsafe class DriverLink : IDisposable
{
    public const string ShmName = "Local\\RollWorld_Shm_v1";
    const uint Magic = 0x444C5752, Version = 2;
    public const int OffMagic = 0, OffVersion = 4, OffSeq = 8, OffEnabled = 12, OffRot = 16, OffTrans = 48,
        OffHeartbeat = 72, OffPoseCount = 80, OffApplied = 88, OffHookMask = 96, OffPid = 100, OffFlags = 104,
        OffSeen0 = 112, OffSeen1 = 120, Size = 128;
    public const uint FlagHmdKeepOrientation = 1, FlagHmdZeroVelocity = 2;

    MemoryMappedFile mmf;
    MemoryMappedViewAccessor view;
    byte* p;

    public bool Connected => p != null;

    public DriverState State
    {
        get
        {
            if (p == null) return DriverState.NotFound;
            return HookMask == 0 ? DriverState.HooksInactive : DriverState.Connected;
        }
    }

    uint Read32(int off) => Volatile.Read(ref *(uint*)(p + off));
    ulong Read64(int off) => Volatile.Read(ref *(ulong*)(p + off));

    public uint HookMask => p == null ? 0 : Read32(OffHookMask);
    public uint DriverPid => p == null ? 0 : Read32(OffPid);
    public ulong PoseCount => p == null ? 0 : Read64(OffPoseCount);
    public ulong AppliedCount => p == null ? 0 : Read64(OffApplied);
    /// <summary>Бит i = устройство i хоть раз прошло через хук IVRServerDriverHost_005 (диагностика F8).</summary>
    public ulong SeenMask0 => p == null ? 0 : Read64(OffSeen0);
    /// <summary>Бит i = устройство i хоть раз прошло через хук IVRServerDriverHost_006 (диагностика F8).</summary>
    public ulong SeenMask1 => p == null ? 0 : Read64(OffSeen1);

    public bool TryConnect()
    {
        if (p != null) return true;
        if (!OperatingSystem.IsWindows()) return false; // именованная общая память есть только в Windows
        try
        {
            mmf = MemoryMappedFile.OpenExisting(ShmName, MemoryMappedFileRights.ReadWrite);
            view = mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            byte* ptr = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            p = ptr + view.PointerOffset;
            if (Read32(OffMagic) != Magic || Read32(OffVersion) != Version) { Disconnect(); return false; }
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    /// <summary>Только для тестов: подключиться к готовому буферу.</summary>
    public void Attach(byte* buffer) { p = buffer; }

    public void Disconnect()
    {
        if (view != null && p != null && mmf != null)
        {
            try { view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
        }
        p = null;
        view?.Dispose(); view = null;
        mmf?.Dispose(); mmf = null;
    }

    /// <summary>Процесс драйвера (vrserver) ещё жив? После перезапуска SteamVR старая память "мёртвая".</summary>
    public bool DriverAlive()
    {
        if (p == null) return false;
        uint pid = DriverPid;
        if (pid == 0) return false;
        try { using var pr = Process.GetProcessById((int)pid); return !pr.HasExited; }
        catch { return false; }
    }

    /// <summary>Сообщить драйверу, что мы живы (иначе через 5 с он отключит трансформацию).</summary>
    public void Beat()
    {
        if (p == null) return;
        Volatile.Write(ref *(ulong*)(p + OffHeartbeat), (ulong)Environment.TickCount64);
    }

    /// <summary>
    /// Записать трансформацию игрока. total - матрица System.Numerics (вектор-строка):
    /// новая_поза = поза * total. Драйвер применяет её к каждому устройству в осях трекинга.
    /// </summary>
    public void Write(bool enabled, Matrix4x4 total, uint flags = 0)
    {
        if (p == null) return;
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(total));
        Vector3 t = total.Translation;

        uint s = Read32(OffSeq);
        Volatile.Write(ref *(uint*)(p + OffSeq), s + 1); // нечётное: идёт запись
        Thread.MemoryBarrier();
        *(uint*)(p + OffEnabled) = enabled ? 1u : 0u;
        *(uint*)(p + OffFlags) = flags;
        double* r = (double*)(p + OffRot);
        r[0] = q.W; r[1] = q.X; r[2] = q.Y; r[3] = q.Z;
        double* tt = (double*)(p + OffTrans);
        tt[0] = t.X; tt[1] = t.Y; tt[2] = t.Z;
        Thread.MemoryBarrier();
        Volatile.Write(ref *(uint*)(p + OffSeq), s + 2);
        Beat();
    }

    public void Dispose() => Disconnect();
}