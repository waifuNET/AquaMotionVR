using System;
using System.Numerics;
using Valve.VR;

/// <summary>Конвертация матриц OpenVR (3x4, вектор-столбец) и System.Numerics (4x4, вектор-строка).</summary>
static class Conv
{
    public static Matrix4x4 ToMatrix(HmdMatrix34_t m) => new Matrix4x4(
        m.m0, m.m4, m.m8,  0,
        m.m1, m.m5, m.m9,  0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1);

    public static HmdMatrix34_t ToHmd(Matrix4x4 m) => new HmdMatrix34_t
    {
        m0 = m.M11, m1 = m.M21, m2  = m.M31, m3  = m.M41,
        m4 = m.M12, m5 = m.M22, m6  = m.M32, m7  = m.M42,
        m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = m.M43
    };

    public static bool IsIdentity(Matrix4x4 m, float eps = 1e-5f)
    {
        var d = m - Matrix4x4.Identity;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M21) + MathF.Abs(d.M22) + MathF.Abs(d.M23) +
               MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) + MathF.Abs(d.M41) + MathF.Abs(d.M42) + MathF.Abs(d.M43) < eps;
    }
}

/// <summary>
/// Куда отправлять трансформацию (новая_поза = поза * total):
///  - DriverOutput: в драйвер RollWorld - двигает сырые позы устройств целиком, нужен для наклона/переворота;
///  - ChaperoneOutput: через настройки пространства SteamVR - сдвиг и поворот вокруг вертикали, без драйвера.
/// В рабочем режиме используются оба сразу - см. HybridOutput ниже, он и раскладывает total между ними.
/// </summary>
abstract class Output
{
    public abstract string Name { get; }
    /// <summary>true - позы, которые читает OpenVR, уже содержат нашу трансформацию (её надо вычитать)</summary>
    public abstract bool TransformsPoses { get; }
    /// <summary>что сейчас реально применено к позам</summary>
    public Matrix4x4 Applied = Matrix4x4.Identity;
    public bool Healthy = true;
    /// <summary>флаги вида для драйвера (DriverLink.Flag*); другие выводы их игнорируют</summary>
    public uint Flags;

    public abstract void Set(Matrix4x4 total);
    public virtual void Flush() { }
    public abstract void Reset();
    public virtual void Tick() { }
    /// <summary>Вызывается примерно раз в 1 мс между итерациями главного цикла - для того, что нельзя упустить ни на кадр.</summary>
    public virtual void FastTick() { }
}

sealed class DriverOutput : Output
{
    readonly DriverLink link;
    bool enabled;
    DateTime enabledSince;
    ulong appliedAtEnable, posesAtEnable;

    public DriverOutput(DriverLink l) { link = l; }
    public override string Name => "запасной драйвер (полный наклон и переворот)";
    public override bool TransformsPoses => true;

    public override void Set(Matrix4x4 total)
    {
        bool en = !Conv.IsIdentity(total);
        if (en && !enabled) { enabledSince = DateTime.UtcNow; appliedAtEnable = link.AppliedCount; posesAtEnable = link.PoseCount; }
        enabled = en;
        link.Write(en, total, Flags);
        Applied = en ? total : Matrix4x4.Identity;
    }

    public override void Reset()
    {
        enabled = false;
        link.Write(false, Matrix4x4.Identity, Flags);
        Applied = Matrix4x4.Identity;
    }

    /// <summary>Проверка: если позы идут, а драйвер ничего не применяет - он неисправен, надо уйти на запасной вариант.</summary>
    public override void Tick()
    {
        if (!enabled || !Healthy) return;
        if ((DateTime.UtcNow - enabledSince).TotalSeconds < 1.5) return;
        bool posesFlow = link.PoseCount > posesAtEnable + 20;
        if (posesFlow && link.AppliedCount == appliedAtEnable) Healthy = false;
    }
}

/// <summary>
/// Гибрид: сдвиг и поворот вокруг вертикали (Y) идут через ChaperoneOutput (SteamVR сам двигает
/// пространство, поэтому компоновщик шлема Oculus/VD/ALVR не видит расхождения и не рисует чёрные
/// края), а наклон/переворот (то, что chaperone-пространство физически не умеет хранить) - через
/// DriverOutput на устройство целиком. Раздельно эти два канала не расходятся, потому что drv - чистый
/// поворот без сдвига, а его "видят" ровно те же сырые позы (TrackingUniverseRawAndUncalibrated),
/// на которых считается corr в PushTotal.
/// </summary>
sealed class HybridOutput : Output
{
    readonly ChaperoneOutput chap;
    DriverOutput drv; // null, если драйвера нет/выключен - тогда наклон/переворот просто не передаётся
    readonly Action<string> onDriverLost;

    public bool HasDriver => drv != null;

    public HybridOutput(DriverOutput driver, ChaperoneOutput chaperone, Action<string> onDriverLost = null)
    {
        drv = driver; chap = chaperone; this.onDriverLost = onDriverLost;
    }

    public override string Name => HasDriver
        ? "гибрид: chaperone (сдвиг+рыск) + драйвер (наклон/переворот)"
        : "только chaperone (сдвиг+рыск; наклон/переворот недоступен - нет драйвера)";

    public override void Set(Matrix4x4 total)
    {
        var (universe, tilt) = Split(total);
        chap.Flags = Flags; chap.Set(universe);
        if (drv != null) { drv.Flags = Flags; drv.Set(tilt); Applied = tilt; }
        else Applied = Matrix4x4.Identity; // сырые позы (TrackingUniverseRawAndUncalibrated) никто не трогает
    }

    /// <summary>true - Applied (наклон/переворот) действительно применён к сырым позам устройств.</summary>
    public override bool TransformsPoses => HasDriver;

    public override void Flush() { chap.Flush(); drv?.Flush(); }

    public override void Reset()
    {
        chap.Reset();
        drv?.Reset();
        Applied = Matrix4x4.Identity;
    }

    public override void Tick()
    {
        chap.Tick();
        if (drv == null) return;
        drv.Tick();
        if (!drv.Healthy)
        {
            // наклон/переворот не применяется - деградируем мягко: остаётся сдвиг+рыск через chaperone,
            // не сбрасываем весь вывод и не уходим в forceChaperone навсегда.
            drv.Reset();
            drv = null;
            Applied = Matrix4x4.Identity;
            onDriverLost?.Invoke("Драйвер перестал применять наклон/переворот - остался сдвиг и повороты влево-вправо.");
        }
    }

    /// <summary>
    /// Раскладывает накопленную трансформацию игрока total = (R, t) на:
    ///  - universe: чистый поворот вокруг Y (рыск) + весь сдвиг t - это то, что chaperone-пространство
    ///    SteamVR способно хранить (standing zero pose всегда выровнена по гравитации);
    ///  - tilt: чистый поворот без сдвига - остаток R после вычитания рыска, идёт в драйвер и
    ///    крутит сырые позы устройств целиком (наклон/переворот).
    /// Раскладка точна: применение сначала tilt, потом universe, к любой позе даёт тот же результат,
    /// что и total (see driver_main.cpp: сырые позы получают tilt; SteamVR потом сам добавляет universe
    /// при пересчёте в TrackingUniverseStanding/Seated).
    /// </summary>
    public static (Matrix4x4 universe, Matrix4x4 tilt) Split(Matrix4x4 total)
    {
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(total));

        // swing-twist: "twist" вокруг Y - это и есть рыск. Вырождается только если поворот уже почти
        // строго на 90° по тангажу/крену (q.Y и q.W оба к нулю) - в этом случае просто не трогаем рыск.
        var qYaw = new Quaternion(0, q.Y, 0, q.W);
        float len = qYaw.Length();
        qYaw = len > 1e-4f ? qYaw * (1f / len) : Quaternion.Identity;

        var universe = Matrix4x4.CreateFromQuaternion(qYaw);
        universe.Translation = total.Translation;

        var rot = total; rot.Translation = Vector3.Zero;                    // только поворот из total
        var yawInv = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(qYaw));
        var tilt = rot * yawInv;                                            // R * Ryaw^-1: остаток без рыска
        tilt.Translation = Vector3.Zero;                                    // защита от накопления float-погрешности

        return (universe, tilt);
    }
}

/// <summary>
/// Вывод через пространство SteamVR (как Space Drag / Space Turn / Space Orient в OVR Advanced Settings).
///  - FullRotation = false: только сдвиг + рыск (публичный API chaperone), наклон отбрасывается - для гибрида с драйвером.
///  - FullRotation = true:  полные 6DOF - рыск+сдвиг идут в рабочую копию chaperone, а полная матрица с наклоном
///    дописывается поверх через IVRPaths (см. VrPaths). Драйвер не нужен, сырые позы не трогаются.
/// </summary>
sealed class ChaperoneOutput : Output
{
    const int CommitHz = 60; // как часто коммитить сдвиг/рыск в запасном режиме (с драйвером)
    readonly Settings s;
    readonly bool full;
    readonly Action<string> onFullLost;
    Matrix4x4 zBase;
    bool haveBase, pending;
    Matrix4x4 pendingZ;
    DateTime lastCommit = DateTime.MinValue;

    // ---- только для FullRotation ----
    Matrix4x4 zFull = Matrix4x4.Identity; // полная (с наклоном) standing zero pose -> raw
    Matrix4x4 desired = Matrix4x4.Identity; // то, что должно лежать в /chaperone/standing/transform (= zFull^-1)
    bool dirty;                           // рабочая копия chaperone отстаёт от zFull (закоммитим в Flush)
    public int Restores;                  // сколько раз SteamVR/кто-то перезаписал матрицу и мы её вернули

    public ChaperoneOutput(Settings settings, bool fullRotation = false, Action<string> onFullLost = null)
    {
        s = settings; full = fullRotation; this.onFullLost = onFullLost;
    }

    public bool FullRotation => full;
    public override string Name => full
        ? "пространство SteamVR, полные 6DOF (сдвиг + рыск + наклон/переворот, как OVRAS Space Orient; драйвер не нужен)"
        : "настройки пространства SteamVR (сдвиг + поворот влево-вправо)";
    public override bool TransformsPoses => false; // сырые позы не меняются никогда

    bool EnsureBase()
    {
        if (haveBase) return true;
        var cs = OpenVR.ChaperoneSetup;
        cs.RevertWorkingCopy(); // актуальное живое состояние (учитывает OVR Advanced Settings и др.)
        var z = new HmdMatrix34_t();
        if (!cs.GetWorkingStandingZeroPoseToRawTrackingPose(ref z)) return false;
        zBase = Conv.ToMatrix(z);
        // если в базе остался чужой наклон (например, от OVRAS Space Orient) - берём только рыск+сдвиг
        zBase = YawOnly(zBase);
        haveBase = true;
        return true;
    }

    public override void Set(Matrix4x4 total)
    {
        if (!EnsureBase()) return;
        if (!Matrix4x4.Invert(total, out var inv)) return;
        Applied = total;
        var zNew = zBase * inv;

        if (full)
        {
            // Во время движения рабочую копию chaperone НЕ трогаем: любое её изменение (превью/коммит) заставляет
            // сервер асинхронно пересобрать матрицу из рабочей копии (только рыск) - и если пересборка приходит
            // после нашей записи, на кадр пропадает наклон и тело "улетает". Полная матрица и так содержит всё:
            // сдвиг, рыск и наклон. Рабочую копию обновляем один раз в Flush (конец захвата/заплыва).
            zFull = zNew;
            if (!Matrix4x4.Invert(zFull, out desired)) return;
            dirty = true;
            WriteFull();
            return;
        }

        if ((DateTime.UtcNow - lastCommit).TotalMilliseconds >= 1000.0 / CommitHz) { Apply(zNew); pending = false; lastCommit = DateTime.UtcNow; }
        else { pendingZ = zNew; pending = true; }
    }

    public override void Flush()
    {
        if (full)
        {
            if (dirty) { Apply(YawOnly(zFull)); dirty = false; lastCommit = DateTime.UtcNow; }
            WriteFull(); // коммит пересобирает матрицу на сервере из рабочей копии - наклон надо вернуть
            return;
        }
        if (pending) { Apply(pendingZ); pending = false; lastCommit = DateTime.UtcNow; }
    }

    public override void Tick() => Enforce();
    public override void FastTick() => Enforce();

    /// <summary>
    /// Сторож: сервер пересобирает матрицу при любом изменении chaperone (наш коммит, OVRAS, сам SteamVR/Quest),
    /// причём асинхронно. Проверяем ~раз в 1 мс и сразу возвращаем нужную - окно, в которое игра может
    /// увидеть неправильную позу, сжимается с ~10 мс (почти кадр) до ~1 мс.
    /// </summary>
    void Enforce()
    {
        if (!full || !haveBase || !Healthy) return;
        if (!VrPaths.ReadStanding(out var cur)) { WriteFull(); return; }
        if (VrPaths.Near(cur, desired, 1e-4f)) return;
        Restores++;
        WriteFull();
    }

    public override void Reset()
    {
        if (haveBase)
        {
            Apply(zBase);
            if (full && Matrix4x4.Invert(zBase, out var inv)) VrPaths.WriteStanding(inv);
        }
        haveBase = false; pending = false; dirty = false;
        zFull = Matrix4x4.Identity; desired = Matrix4x4.Identity;
        Applied = Matrix4x4.Identity;
    }

    void WriteFull()
    {
        if (!full || !Healthy) return;
        // путь хранит "сырое -> standing", то есть обратную к zero pose
        if (VrPaths.WriteStanding(desired)) return;
        if (!VrPaths.Ready)
        {
            Healthy = false;
            onFullLost?.Invoke("SteamVR перестал принимать наклон (6DOF) - сбросил положение.");
        }
    }

    static void Apply(Matrix4x4 z)
    {
        var cs = OpenVR.ChaperoneSetup;
        var m = Conv.ToHmd(z);
        cs.SetWorkingStandingZeroPoseToRawTrackingPose(ref m);
        cs.CommitWorkingCopy(EChaperoneConfigFile.Live);
    }

    /// <summary>
    /// Оставляет от позы только рыск (поворот вокруг вертикали) и сдвиг - то, что умеет хранить рабочая копия
    /// chaperone. Рыск берётся по локальной оси Z (как quaternion::yawOnly в OVRAS), чтобы наклон в него не протекал.
    /// </summary>
    public static Matrix4x4 YawOnly(Matrix4x4 m)
    {
        // M31/M33 - это m[0][2]/m[2][2] матрицы OpenVR (см. Conv.ToMatrix)
        float yaw = (MathF.Abs(m.M31) + MathF.Abs(m.M33) > 1e-4f)
            ? MathF.Atan2(m.M31, m.M33)
            : MathF.Atan2(-m.M13, m.M11); // ось Z вертикальна (наклон ровно 90°) - берём рыск по оси X
        var r = Matrix4x4.CreateRotationY(yaw);
        r.Translation = m.Translation;
        return r;
    }

    static bool RotNear(Matrix4x4 a, Matrix4x4 b, float eps)
    {
        var d = a - b;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M21) + MathF.Abs(d.M22) +
               MathF.Abs(d.M23) + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) < eps * 9;
    }
}
