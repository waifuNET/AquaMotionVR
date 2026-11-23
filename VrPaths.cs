using System.Numerics;
using System.Runtime.InteropServices;
using Valve.VR;

/// <summary>
/// Недокументированный интерфейс SteamVR IVRPaths_002 - тот же способ, которым OVR Advanced Settings 6.0 (beta,
/// "Space Orient") делает наклон и переворот пространства без драйвера.
///
/// Публичный API chaperone (SetWorkingStandingZeroPoseToRawTrackingPose) хранит только рыск + сдвиг. Но сам
/// vrserver держит готовую матрицу "сырое -> standing" по пути /chaperone/standing/transform, и её можно
/// перезаписать полной (с тангажом/креном). Сырые позы устройств при этом НЕ меняются - меняется только
/// пересчёт в standing-пространство, как и при обычном Space Drag, поэтому компоновщики Quest (Link/AirLink/
/// Virtual Desktop/ALVR) не видят расхождения и не рисуют чёрных краёв.
///
/// Сервер пересобирает эту матрицу из рабочей копии chaperone при каждом её изменении, поэтому её нужно
/// записывать заново после каждого коммита/превью и периодически, пока есть наклон.
///
/// Интерфейс восстановлен реверс-инжинирингом (см. OpenVR-AdvancedSettings, src/openvr/ovr_paths_wrapper.cpp)
/// и может сломаться с обновлением SteamVR - на этот случай перед использованием делается проверка, что путь
/// содержит ровно то, что рантайм отдаёт через GetRawZeroPoseToStandingAbsoluteTrackingPose.
/// </summary>
static unsafe class VrPaths
{
    public enum PathsState { NotChecked, Unavailable, Mismatch, Ready }

    const string InterfaceVersion = "IVRPaths_002";
    const ulong ChaperoneRoot = 0x0000000600000003UL; // корневой контейнер, который vrclient передаёт для путей chaperone
    const string StandingPath = "/chaperone/standing/transform";
    const float CheckTolerance = 1e-3f;

    [StructLayout(LayoutKind.Sequential)]
    struct PathRead
    {
        public ulong ulPath;
        public void* pvBuffer;
        public uint unBufferSize;
        public uint unTag;
        public uint unRequiredBufferSize;
        public int eError;
        public byte* pszPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PathWrite
    {
        public ulong ulPath;
        public int writeType;      // 0 = Set
        public int eSetError;
        public void* pvBuffer;
        public uint unBufferSize;
        public uint unTag;
        public int eError;
        public byte* pszPath;
        public byte bPostEvents;   // C++ bool
        public byte bValueChanged;
    }

    static IntPtr self;          // C++-объект интерфейса (первое поле - указатель на vtable)
    static ulong hStanding;
    static IntPtr pszStanding;   // строка пути (ANSI, живёт всё время работы программы)
    static int writeFails;

    public static PathsState State { get; private set; } = PathsState.NotChecked;
    public static bool Ready => State == PathsState.Ready;

    // vtable: 0 ReadPathBatch, 1 WritePathBatch, 2 StringToHandle, 3 HandleToString
    static void** Vtbl => *(void***)self;

    /// <summary>Вызывать после каждого OpenVR.Init: указатель на интерфейс от прошлой сессии недействителен.</summary>
    public static void Reset()
    {
        self = IntPtr.Zero; hStanding = 0; writeFails = 0;
        State = PathsState.NotChecked;
    }

    /// <summary>
    /// Находит интерфейс и проверяет, что путь действительно хранит то, чем пользуется рантайм.
    /// Делать, пока RollWorld ещё ничего не наклонил (сразу после подключения к SteamVR).
    /// </summary>
    public static PathsState Probe()
    {
        if (State != PathsState.NotChecked) return State;
        State = PathsState.Unavailable;
        try
        {
            var err = EVRInitError.None;
            IntPtr p = OpenVR.GetGenericInterface(InterfaceVersion, ref err);
            if (p == IntPtr.Zero || err != EVRInitError.None)
            {
                Console.WriteLine($"6DOF через SteamVR: {InterfaceVersion} недоступен ({err}) - наклон только через драйвер.");
                return State;
            }
            self = p;
            if (pszStanding == IntPtr.Zero) pszStanding = Marshal.StringToHGlobalAnsi(StandingPath);

            ulong h = 0;
            var stringToHandle = (delegate* unmanaged<IntPtr, ulong*, byte*, int>)Vtbl[2];
            int e = stringToHandle(self, &h, (byte*)pszStanding);
            if (e != 0 || h == 0)
            {
                self = IntPtr.Zero;
                Console.WriteLine($"6DOF через SteamVR: не удалось найти путь {StandingPath} (ошибка {e}).");
                return State;
            }
            hStanding = h;

            if (!ReadStanding(out var cur))
            {
                self = IntPtr.Zero;
                Console.WriteLine("6DOF через SteamVR: путь не читается.");
                return State;
            }
            var expect = Conv.ToMatrix(OpenVR.System.GetRawZeroPoseToStandingAbsoluteTrackingPose());
            if (!Near(cur, expect, CheckTolerance))
            {
                State = PathsState.Mismatch;
                self = IntPtr.Zero;
                Console.WriteLine("6DOF через SteamVR: путь не совпадает с тем, что использует рантайм (другая версия SteamVR?) - наклон только через драйвер.");
                return State;
            }
            State = PathsState.Ready;
            Console.WriteLine("6DOF через SteamVR: доступен (как Space Orient в OVR Advanced Settings 6.0).");
        }
        catch (Exception ex)
        {
            self = IntPtr.Zero;
            State = PathsState.Unavailable;
            Console.WriteLine("6DOF через SteamVR: ошибка проверки - " + ex.Message);
        }
        return State;
    }

    /// <summary>Читает текущую матрицу "сырое -> standing" (вектор-строка, как везде в программе).</summary>
    public static bool ReadStanding(out Matrix4x4 rawToStanding)
    {
        rawToStanding = Matrix4x4.Identity;
        if (self == IntPtr.Zero) return false;
        var m = new HmdMatrix34_t();
        var r = new PathRead
        {
            ulPath = hStanding,
            pvBuffer = &m,
            unBufferSize = (uint)sizeof(HmdMatrix34_t),
            pszPath = (byte*)pszStanding,
        };
        var fn = (delegate* unmanaged<IntPtr, ulong, PathRead*, uint, int>)Vtbl[0];
        int e = fn(self, ChaperoneRoot, &r, 1);
        if (e != 0 || r.eError != 0 || r.unTag != OpenVR.k_unHmdMatrix34PropertyTag || r.unRequiredBufferSize != sizeof(HmdMatrix34_t))
            return false;
        rawToStanding = Conv.ToMatrix(m);
        return true;
    }

    /// <summary>Записывает полную (с наклоном) матрицу "сырое -> standing". false - запись не прошла.</summary>
    public static bool WriteStanding(Matrix4x4 rawToStanding)
    {
        if (!Ready) return false;
        var m = Conv.ToHmd(rawToStanding);
        var w = new PathWrite
        {
            ulPath = hStanding,
            writeType = 0,
            pvBuffer = &m,
            unBufferSize = (uint)sizeof(HmdMatrix34_t),
            unTag = OpenVR.k_unHmdMatrix34PropertyTag,
            pszPath = (byte*)pszStanding,
            bPostEvents = 0,
        };
        var fn = (delegate* unmanaged<IntPtr, ulong, PathWrite*, uint, int>)Vtbl[1];
        int e = fn(self, ChaperoneRoot, &w, 1);
        if (e == 0 && w.eError == 0) { writeFails = 0; return true; }

        if (++writeFails == 1) Console.WriteLine($"6DOF через SteamVR: запись не прошла (ошибка {e}/{w.eError}).");
        if (writeFails > 30)
        {
            State = PathsState.Unavailable;
            Console.WriteLine("6DOF через SteamVR: запись постоянно не проходит - отключаю.");
        }
        return false;
    }

    public static bool Near(Matrix4x4 a, Matrix4x4 b, float tol)
    {
        var d = a - b;
        return MathF.Abs(d.M11) <= tol && MathF.Abs(d.M12) <= tol && MathF.Abs(d.M13) <= tol &&
               MathF.Abs(d.M21) <= tol && MathF.Abs(d.M22) <= tol && MathF.Abs(d.M23) <= tol &&
               MathF.Abs(d.M31) <= tol && MathF.Abs(d.M32) <= tol && MathF.Abs(d.M33) <= tol &&
               MathF.Abs(d.M41) <= tol && MathF.Abs(d.M42) <= tol && MathF.Abs(d.M43) <= tol;
    }
}
