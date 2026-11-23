using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Valve.VR;

enum Mode { None, Roll, Swim }

internal static class Program
{
    // Запасные клавиши (глобальные, только Windows), работают без привязок кнопок:
    const int GrabKey = 0x78;   // F9  - захват (в плавании: обе руки схвачены)
    const int ResetKey = 0x79;  // F10 - сброс
    const int TestKey = 0x77;   // F8  - самопроверка: какие устройства реально получают трансформацию
    public const string AppName = "AquaMotionVR";
    const string AppKey = "user.aquamotionvr";
    const string OverlayKey = "user.aquamotionvr.dashboard";

    static Settings S = new();
    static Ui ui;
    static volatile bool quit;
    static bool steamVrClosed;

    // ---- куда отправляем трансформацию ----
    static readonly DriverLink link = new();
    static Output output;
    static bool forceChaperone;   // драйвер не заработал - используем только настройки пространства

    // Накопленная трансформация игрока (System.Numerics, вектор-строка: новая_поза = поза * X):
    static Matrix4x4 committed = Matrix4x4.Identity; // всё, что накоплено прошлыми сессиями
    static Matrix4x4 session = Matrix4x4.Identity;   // текущая сессия (захват или заплыв), в физических осях
    static Matrix4x4 corr = Matrix4x4.Identity;      // физ. поза = поза из OpenVR * corr

    // ---- вращение ----
    static bool grabbing;
    static Quaternion qGrab;
    static Vector3 pivot, axis;
    static float prevAngle, accAngle;

    // ---- плавание ----
    static readonly SwimBody body = new();
    static bool swimActive;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double swimLastT;

    // ---- границы игровой зоны ----
    static bool boundsHidden;
    static string BoundsBackupFile => Path.Combine(Settings.Dir, "bounds_backup.txt");

    static ulong rollSetH, rollGrabH, rollResetH, swimSetH, swimResetH, mainH, thumbH;
    static ulong commonSetH, gripLH, gripRH, hapticH, dragLH, dragRH;

    // ---- перетаскивание (как Space Drag в OVRAS, но своё): держишь кнопку и тянешь мир рукой ----
    static bool dragging, dragFromLeft;
    static uint dragHand;
    static Vector3 dragPrev; // всегда активный набор: грипы (плавание и двойное нажатие) + вибрация

    // ---- двойное нажатие обоих грипов: вкл/выкл режима ----
    const double TapMaxHold = 0.35;   // с: дольше держишь - это не "нажатие", а гребок/хват
    const double TapMaxSkew = 0.2;    // с: грипы должны нажиматься почти одновременно
    static bool prevGL, prevGR, bothDown, bothValid;
    static double tDownL, tDownR, tBothDown, tLastTap = -10;
    static Mode lastMode = Mode.Roll; // что включать по двойному нажатию, если сейчас всё выключено

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

    static Mode ActiveMode => S.RollEnabled ? Mode.Roll : S.SwimEnabled ? Mode.Swim : Mode.None;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!Fonts.Load(Path.Combine(AppContext.BaseDirectory, "fonts.bin")))
        {
            Console.WriteLine("Не найден или повреждён fonts.bin рядом с программой.");
            return;
        }
        S = Settings.Load();
        lastMode = S.SwimEnabled ? Mode.Swim : Mode.Roll;
        ui = new Ui(S, ResetFromUi, OpenBindings);

        if (args.Contains("--preview"))
        {
            ui.Tab = 0; ui.SavePreview(Path.Combine(AppContext.BaseDirectory, "preview.png"));
            ui.Tab = 1; ui.SavePreview(Path.Combine(AppContext.BaseDirectory, "preview_swim.png"));
            ui.Tab = 2; ui.SavePreview(Path.Combine(AppContext.BaseDirectory, "preview_controls.png"));
            Console.WriteLine("Картинки интерфейса: preview.png и preview_swim.png в " + AppContext.BaseDirectory);
            return;
        }

        // Тестовый ключ: не пользоваться драйвером, гнать всё через пространство SteamVR.
        // Нужен, чтобы проверить, не ломает ли картинку компоновщик шлема (Quest/Link/VD/ALVR).
        if (args.Contains("--chaperone"))
        {
            forceChaperone = true;
            Console.WriteLine("Режим проверки: драйвер не используется, трансформация идёт через пространство SteamVR.");
        }

        if (OperatingSystem.IsWindows()) timeBeginPeriod(1);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit = true; };
        Console.WriteLine(AppName + " запущен. Настройки: " + Settings.FilePath);
        Console.WriteLine("Открой панель SteamVR и найди " + AppName + ". F9 - захват, F10 - сброс, Ctrl+C - выход.");

        bool waitingShown = false;
        while (!quit)
        {
            steamVrClosed = false;
            if (!TryInit(out string err))
            {
                if (!waitingShown && !quit) { Console.WriteLine("Жду SteamVR... (" + err + ")"); waitingShown = true; }
                Thread.Sleep(2000);
                continue;
            }
            waitingShown = false;
            Console.WriteLine("Подключен к SteamVR.");
            try { Run(); }
            finally
            {
                try
                {
                    AbortMotion();
                    if (S.ResetOnExit && !steamVrClosed) DoReset();
                    SetBoundsHidden(false);
                }
                catch (Exception e) { Console.WriteLine("Ошибка при завершении: " + e.Message); }
                link.Disconnect();
                output = null; committed = Matrix4x4.Identity; session = Matrix4x4.Identity; corr = Matrix4x4.Identity;
                OpenVR.Shutdown();
            }
            if (steamVrClosed) Console.WriteLine("SteamVR закрыт, жду перезапуска...");
        }
    }

    static bool TryInit(out string error)
    {
        var err = EVRInitError.None;
        try { OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay); }
        catch (DllNotFoundException)
        {
            Console.WriteLine("Не найден openvr_api.dll рядом с программой (нужен Windows x64).");
            quit = true; error = "no dll"; return false;
        }
        error = err.ToString();
        if (err != EVRInitError.None) return false;

        string dir = AppContext.BaseDirectory;
        string exe = Path.Combine(dir, AppName + ".exe");
        string manifest = Path.Combine(dir, "aquamotionvr.vrmanifest");
        File.WriteAllText(manifest,
            "{\"source\":\"builtin\",\"applications\":[{\"app_key\":\"" + AppKey + "\",\"launch_type\":\"binary\"," +
            "\"binary_path_windows\":" + JsonSerializer.Serialize(exe) + ",\"is_dashboard_overlay\":true," +
            "\"strings\":{\"en_us\":{\"name\":\"" + AppName + "\"}}}]}");

        var inp = OpenVR.Input;
        var e1 = OpenVR.Applications.AddApplicationManifest(manifest, true);
        var e2 = OpenVR.Applications.IdentifyApplication((uint)Environment.ProcessId, AppKey);
        var e3 = inp.SetActionManifestPath(Path.Combine(dir, "actions.json"));
        var eh = new[]
        {
            inp.GetActionSetHandle("/actions/roll", ref rollSetH),
            inp.GetActionHandle("/actions/roll/in/grab", ref rollGrabH),
            inp.GetActionHandle("/actions/roll/in/reset", ref rollResetH),
            inp.GetActionSetHandle("/actions/swim", ref swimSetH),
            inp.GetActionHandle("/actions/swim/in/reset", ref swimResetH),
            inp.GetActionSetHandle("/actions/common", ref commonSetH),
            inp.GetActionHandle("/actions/common/in/grip_left", ref gripLH),
            inp.GetActionHandle("/actions/common/in/grip_right", ref gripRH),
            inp.GetActionHandle("/actions/common/out/haptic", ref hapticH),
            inp.GetActionHandle("/actions/common/in/drag_left", ref dragLH),
            inp.GetActionHandle("/actions/common/in/drag_right", ref dragRH),
        };
        Console.WriteLine($"manifest={e1} identify={e2} actions={e3} handles={string.Join(",", eh)}");

        // границы зоны могли остаться скрытыми после аварийного выхода - вернём
        RestoreBoundsFromBackup();

        // 6DOF через SteamVR (как OVRAS 6.0 Space Orient) - проверяем, пока мы ещё ничего не наклонили
        VrPaths.Reset();
        VrPaths.Probe();
        ui.PathsReady = VrPaths.Ready;

        // драйвер
        link.Disconnect();
        link.TryConnect();
        ui.DriverState = (int)link.State;
        Console.WriteLine("Запасной драйвер: " + link.State + (link.Connected ? $" (pid {link.DriverPid}, хуки: {link.HookMask})" : " - без него будут доступны сдвиг и повороты влево-вправо (через chaperone), но не наклон/переворот"));

        // панель в дашборде SteamVR
        var ov = OpenVR.Overlay;
        mainH = thumbH = 0;
        var e7 = ov.CreateDashboardOverlay(OverlayKey, AppName, ref mainH, ref thumbH);
        if (e7 == EVROverlayError.None)
        {
            ov.SetOverlayWidthInMeters(mainH, 2.4f);
            ov.SetOverlayInputMethod(mainH, VROverlayInputMethod.Mouse);
            var scale = new HmdVector2_t { v0 = Ui.W, v1 = Ui.H };
            ov.SetOverlayMouseScale(mainH, ref scale);
            Upload(thumbH, Ui.Thumbnail(256), 256, 256);
            ui.Render();
            UploadPanel();
        }
        Console.WriteLine($"dashboard={e7}");
        return true;
    }

    /// <summary>Кадр панели: текстурой D3D11 (без мигания), а если не вышло - по-старому через SetOverlayRaw.</summary>
    static void UploadPanel()
    {
        if (OverlayTexture.Show(mainH, ui.Canvas.Px, Ui.W, Ui.H)) return;
        Upload(mainH, ui.Canvas.Px, Ui.W, Ui.H);
    }

    static void Upload(ulong handle, byte[] px, int w, int h)
    {
        var gc = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { OpenVR.Overlay.SetOverlayRaw(handle, gc.AddrOfPinnedObject(), (uint)w, (uint)h, 4); }
        finally { gc.Free(); }
    }

    static void OnOverlayEvent(VREvent_t e)
    {
        var m = e.data.mouse;
        switch ((EVREventType)e.eventType)
        {
            case EVREventType.VREvent_MouseMove: ui.Move(m.x, m.y); break;
            case EVREventType.VREvent_MouseButtonDown: if (m.button == (uint)EVRMouseButton.Left) ui.Down(m.x, m.y); break;
            case EVREventType.VREvent_MouseButtonUp: if (m.button == (uint)EVRMouseButton.Left) ui.Up(m.x, m.y); break;
            case EVREventType.VREvent_FocusLeave: ui.Leave(); break;
        }
    }

    static VRActiveActionSet_t[] ActiveSets(params ulong[] sets) => sets.Select(set => new VRActiveActionSet_t
    {
        ulActionSet = set,
        ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
        nPriority = OpenVR.k_nActionSetOverlayGlobalPriorityMin + 1
    }).ToArray();

    static Vector3 Vel(TrackedDevicePose_t p) => new(p.vVelocity.v0, p.vVelocity.v1, p.vVelocity.v2);

    // Физическая поза (как если бы наша трансформация не применялась) и физическая скорость
    static Matrix4x4 PhysM(TrackedDevicePose_t p) => Conv.ToMatrix(p.mDeviceToAbsoluteTracking) * corr;
    static Vector3 PhysV(TrackedDevicePose_t p) => Vector3.TransformNormal(Vel(p), corr);

    static Matrix4x4 PhysHead(TrackedDevicePose_t p) => PhysM(p);

    // запасной режим с драйвером: шлем наклоняется вместе с миром, скорости не трогаем
    const uint DriverFlags = 0u;

    static void Run()
    {
        var sys = OpenVR.System;
        var ov = OpenVR.Overlay;
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        var rollSets = ActiveSets(commonSetH, rollSetH);
        var swimSets = ActiveSets(commonSetH, swimSetH);
        var idleSets = ActiveSets(commonSetH);
        uint setSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>();
        uint evSize = (uint)Marshal.SizeOf<VREvent_t>();
        bool prevReset = false, wasVisible = false, prevTest = false;
        var lastRender = DateTime.MinValue;
        var lastLinkCheck = DateTime.MinValue;
        var prevMode = Mode.None;
        swimLastT = clock.Elapsed.TotalSeconds;

        while (!quit)
        {
            var ev = new VREvent_t();
            while (sys.PollNextEvent(ref ev, evSize))
            {
                if (ev.eventType == (uint)EVREventType.VREvent_Quit)
                {
                    steamVrClosed = true;
                    sys.AcknowledgeQuit_Exiting();
                    return;
                }
            }

            // --- панель настроек ---
            if (mainH != 0)
            {
                var oe = new VREvent_t();
                while (ov.PollNextOverlayEvent(mainH, ref oe, evSize)) OnOverlayEvent(oe);
                ui.Tick();
                bool vis = ov.IsOverlayVisible(mainH) || ov.IsDashboardVisible();
                if (vis && !wasVisible) ui.Dirty = true;
                wasVisible = vis;
                if (vis && ui.Dirty && (DateTime.UtcNow - lastRender).TotalMilliseconds >= 40)
                {
                    lastRender = DateTime.UtcNow;
                    if (ui.ChangedSinceRender()) // загружаем кадр только при реальных изменениях (иначе панель мигает)
                    {
                        ui.Render();
                        UploadPanel();
                    }
                }
            }

            // --- связь с драйвером ---
            if ((DateTime.UtcNow - lastLinkCheck).TotalSeconds >= 1)
            {
                lastLinkCheck = DateTime.UtcNow;
                if (link.Connected && !link.DriverAlive()) { link.Disconnect(); DropDriverOutput(); }
                if (!link.Connected) link.TryConnect();
                ui.DriverState = (int)link.State;
            }
            if (link.Connected) link.Beat();
            // настройки вида поменяли в панели - отправляем сразу, даже если сейчас ничего не крутится
            // (сдвиг+рыск это не затрагивает - chaperone флаги игнорирует, но перепослать total дёшево)
            if (output != null && output.Flags != DriverFlags) PushTotal();
            output?.Tick(); // HybridOutput сам деградирует до "только chaperone", если наклон перестал применяться
            if (output is ChaperoneOutput fc && fc.FullRotation && !fc.Healthy)
            {
                // SteamVR перестал принимать полную матрицу - сбрасываемся, следующий захват выберет драйвер
                DoReset();
                output = null;
            }
            ui.PathsReady = VrPaths.Ready;

            // --- грипы читаем всегда: двойное нажатие обоих включает/выключает режим ---
            {
                var m0 = ActiveMode;
                OpenVR.Input.UpdateActionState(m0 == Mode.Roll ? rollSets : m0 == Mode.Swim ? swimSets : idleSets, setSize);
            }
            bool rawGL = ReadDigital(gripLH), rawGR = ReadDigital(gripRH);
            bool dragL = ReadDigital(dragLH), dragR = ReadDigital(dragRH);
            if (DoubleTap(rawGL, rawGR) && S.DoubleTapToggle) ToggleMode();

            // --- смена режима: старый выключаем ---
            var mode = ActiveMode;
            if (mode != Mode.None) lastMode = mode;
            if (mode != prevMode) { AbortMotion(); prevMode = mode; Console.WriteLine("Режим: " + mode); }
            // сдвиг во время плавания идёт через chaperone и не зависит от драйвера - прятать границы
            // можно всегда, когда включено плавание, а не только пока драйвер подключён
            SetBoundsHidden(mode == Mode.Swim && S.HideBounds);

            // --- ввод (активируем только набор действий включённого режима) ---
            bool wantGrab = false, wantReset = false, gripL = false, gripR = false;
            if (mode == Mode.Roll)
            {
                wantGrab = ReadDigital(rollGrabH) || KeyDown(GrabKey);
                wantReset = ReadDigital(rollResetH) || KeyDown(ResetKey);
            }
            else if (mode == Mode.Swim)
            {
                bool both = KeyDown(GrabKey);
                gripL = rawGL || both;
                gripR = rawGR || both;
                wantReset = ReadDigital(swimResetH) || KeyDown(ResetKey);
            }
            else wantReset = KeyDown(ResetKey);

            sys.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0f, poses);

            bool wantTest = KeyDown(TestKey);
            if (wantTest && !prevTest) SelfTest(sys);
            prevTest = wantTest;

            if (wantReset && !prevReset) DoReset();
            prevReset = wantReset;

            // перетаскивание себя рукой - работает в любом режиме (и когда оба выключены)
            UpdateDrag(sys, poses, dragL && !wantReset, dragR && !wantReset);

            if (mode == Mode.Roll)
            {
                uint hand = sys.GetTrackedDeviceIndexForControllerRole(
                    S.UseLeftHand ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
                bool poseOk = hand < poses.Length && poses[hand].bPoseIsValid && poses[0].bPoseIsValid;

                if (wantGrab && !grabbing && poseOk && !wantReset) StartGrab(poses[0], poses[hand]);
                else if (!wantGrab && grabbing) EndGrab();
                else if (grabbing && poseOk) UpdateGrab(poses[hand]);
            }
            else if (mode == Mode.Swim)
            {
                uint iL = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
                uint iR = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
                UpdateSwim(poses, iL, iR, gripL && !wantReset, gripR && !wantReset);
            }

            // вместо одного Sleep(10) - десять по 1 мс со сторожем матрицы 6DOF между ними
            // (timeBeginPeriod(1) уже включён, так что Sleep(1) действительно ~1 мс)
            for (int i = 0; i < 10 && !quit; i++)
            {
                Thread.Sleep(1);
                output?.FastTick();
            }
            if (output is ChaperoneOutput rc && rc.Restores > 0 && (DateTime.UtcNow - lastRestoreLog).TotalSeconds >= 5)
            {
                Console.WriteLine($"6DOF: SteamVR перезаписал матрицу пространства, возвращено {rc.Restores} раз(а) за последние секунды");
                rc.Restores = 0;
                lastRestoreLog = DateTime.UtcNow;
            }
        }
    }

    static DateTime lastRestoreLog = DateTime.MinValue;

    // ================= вывод трансформации =================
    // Сдвиг и повороты влево-вправо (рыск) всегда идут через chaperone-пространство SteamVR - для этого
    // драйвер не нужен. Наклон/переворот сверх этого возможен, только если драйвер подключён; без него
    // остаточная часть (tilt) в HybridOutput.Set() просто не находит получателя и никуда не передаётся.
    // Если SteamVR позволяет (VrPaths.Ready) и в настройках выбран наклон "SteamVR" - всё, включая наклон и
    // переворот, идёт через пространство SteamVR (как OVRAS 6.0 Space Orient), драйвер не нужен.
    static void PickOutput()
    {
        bool wantFull = VrPaths.Ready;
        if (wantFull)
        {
            if (output is ChaperoneOutput c0 && c0.FullRotation && c0.Healthy) return;
        }
        else
        {
            bool wantDriver = !forceChaperone && link.State == DriverState.Connected;
            if (output is HybridOutput h0 && h0.HasDriver == wantDriver) return;
        }
        output?.Reset();
        committed = Matrix4x4.Identity; session = Matrix4x4.Identity; corr = Matrix4x4.Identity;
        if (wantFull)
        {
            output = new ChaperoneOutput(S, true, msg => ui.Toast(msg, Ui.Red, 5));
        }
        else
        {
            bool wantDriver = !forceChaperone && link.State == DriverState.Connected;
            var chap = new ChaperoneOutput(S);
            var drv = wantDriver ? new DriverOutput(link) : null;
            output = new HybridOutput(drv, chap, msg => ui.Toast(msg, Ui.Red, 5));
        }
        output.Flags = DriverFlags;
        Console.WriteLine("Вывод: " + output.Name);
    }

    static void PushTotal()
    {
        output.Flags = DriverFlags;
        output.Set(session * committed); // сначала движение этой сессии (в комнате), потом накопленное
        corr = output.TransformsPoses && Matrix4x4.Invert(output.Applied, out var inv) ? inv : Matrix4x4.Identity;
    }

    /// <summary>Драйвер отвалился (процесс умер) - пересоберём вывод; PickOutput на следующем захвате/заплыве
    /// сам выберет "только chaperone", раз link.State уже не Connected.</summary>
    static void DropDriverOutput()
    {
        if (output is HybridOutput h && h.HasDriver)
        {
            AbortMotion();
            output = null; committed = Matrix4x4.Identity; session = Matrix4x4.Identity; corr = Matrix4x4.Identity;
        }
    }

    static void ResetFromUi() => DoReset();

    /// <summary>Открывает окно привязок кнопок SteamVR для этой программы (прямо в шлеме).</summary>
    static bool OpenBindings()
    {
        try
        {
            var e = OpenVR.Input.OpenBindingUI(AppKey, 0, 0, false);
            Console.WriteLine("Окно привязок SteamVR: " + e);
            return e == EVRInputError.None;
        }
        catch (Exception ex) { Console.WriteLine("Окно привязок: " + ex.Message); return false; }
    }

    static void AbortMotion()
    {
        if (dragging) EndDrag();
        if (grabbing) EndGrab();
        if (swimActive) EndSwim();
    }

    static void DoReset()
    {
        AbortMotion();
        committed = Matrix4x4.Identity; session = Matrix4x4.Identity;
        output?.Reset();
        corr = Matrix4x4.Identity;
        Console.WriteLine("Сброс");
    }

    // ================= ВРАЩЕНИЕ =================
    static void StartGrab(TrackedDevicePose_t head, TrackedDevicePose_t handPose)
    {
        PickOutput();
        var h = PhysM(handPose);
        var hd = PhysHead(head);
        qGrab = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(h));
        pivot = hd.Translation;
        axis = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, S.AxisFromHmd ? hd : h));
        prevAngle = 0; accAngle = 0;
        session = Matrix4x4.Identity;
        grabbing = true;
        Console.WriteLine("Захват");
    }

    static void UpdateGrab(TrackedDevicePose_t handPose)
    {
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(PhysM(handPose)));
        var d = Quaternion.Normalize(q * Quaternion.Conjugate(qGrab)); // поворот руки с момента захвата
        float mul = S.EffectiveMultiplier;

        Quaternion rot;
        if (S.AxisMode)
        {
            float sn = Vector3.Dot(new Vector3(d.X, d.Y, d.Z), axis);
            float ang = WrapPi(2f * MathF.Atan2(sn, d.W));
            accAngle += WrapPi(ang - prevAngle); // накопление: можно крутить больше 180°
            prevAngle = ang;
            rot = Quaternion.CreateFromAxisAngle(axis, accAngle * mul);
        }
        else
        {
            rot = Quaternion.Slerp(Quaternion.Identity, d, mul);
        }

        // rot - на сколько повернулась рука. "Я" - поворачиваешься ты, "Мир" - мир крутится как рука (обратный поворот игрока).
        var player = S.WorldFollowsHand ? Quaternion.Conjugate(rot) : rot;
        session = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(player) * Matrix4x4.CreateTranslation(pivot);
        PushTotal();
    }

    static void EndGrab()
    {
        grabbing = false;
        output?.Flush();
        committed = Rigid(session * committed);
        session = Matrix4x4.Identity;
        Console.WriteLine("Отпущено");
    }

    /// <summary>
    /// Отбрасывает накопившуюся за много перемножений матриц ошибку (масштаб/перекос) и оставляет
    /// чистый поворот + сдвиг. Без этого "committed" после многих захватов/заплывов плавания
    /// потихоньку перестаёт быть жёстким преобразованием, и виртуальные руки расходятся с реальными.
    /// </summary>
    static Matrix4x4 Rigid(Matrix4x4 m)
    {
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        var t = m.Translation;
        var r = Matrix4x4.CreateFromQuaternion(q);
        r.Translation = t;
        return r;
    }

    // ================= ПЕРЕТАСКИВАНИЕ =================
    /// <summary>
    /// Держишь кнопку и двигаешь руку - тебя сдвигает в обратную сторону (руку вниз - едешь вверх), как будто
    /// схватил мир. Скорость 1× - рука "прилипает" к миру. Без инерции: отпустил кнопку - сразу стоп.
    /// Складывается с вращением и плаванием, потому что идёт в ту же общую трансформацию.
    /// </summary>
    static void UpdateDrag(CVRSystem sys, TrackedDevicePose_t[] poses, bool l, bool r)
    {
        if (!dragging)
        {
            if (!(l || r)) return;
            uint h = sys.GetTrackedDeviceIndexForControllerRole(l ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
            if (h >= poses.Length || !poses[h].bPoseIsValid) return;
            PickOutput();
            dragHand = h; dragFromLeft = l;
            dragPrev = PhysM(poses[h]).Translation;
            dragging = true;
            Haptic(0.03f, 0.3f);
            return;
        }

        if (!(dragFromLeft ? l : r)) { EndDrag(); return; }
        if (dragHand >= poses.Length || !poses[dragHand].bPoseIsValid) return;

        var p = PhysM(poses[dragHand]).Translation;
        var d = p - dragPrev;
        dragPrev = p;
        if (S.DragVerticalOnly) d = new Vector3(0, d.Y, 0);
        if (d.LengthSquared() < 1e-12f) return;

        // хотим total' = T * total (сдвиг в физических осях комнаты поверх всего остального);
        // total = session * committed, поэтому committed' = session^-1 * T * session * committed -
        // так перетаскивание не ломает идущий сейчас захват/заплыв
        var T = Matrix4x4.CreateTranslation(-d * S.DragSpeed);
        if (!Matrix4x4.Invert(session, out var sInv)) return;
        committed = sInv * T * session * committed;
        PushTotal();
    }

    static void EndDrag()
    {
        dragging = false;
        committed = Rigid(committed);
        if (!grabbing && !swimActive) output?.Flush();
    }

    // ================= ПЛАВАНИЕ =================
    static void UpdateSwim(TrackedDevicePose_t[] poses, uint iL, uint iR, bool gl, bool gr)
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - swimLastT, 0.001, 0.05);
        swimLastT = now;
        if (!poses[0].bPoseIsValid) return;

        bool okL = iL < poses.Length && poses[iL].bPoseIsValid;
        bool okR = iR < poses.Length && poses[iR].bPoseIsValid;
        body.Smooth(dt, PhysV(poses[0]), okL ? PhysV(poses[iL]) : Vector3.Zero, okR ? PhysV(poses[iR]) : Vector3.Zero);

        bool useL = gl && okL, useR = gr && okR;
        if (!swimActive)
        {
            if (!(useL || useR)) return;
            PickOutput();
            body.Reset();
            session = Matrix4x4.Identity;
            swimActive = true;
            Console.WriteLine("Плавание: старт");
        }

        var head = PhysHead(poses[0]);
        var lm = okL ? PhysM(poses[iL]) : Matrix4x4.Identity;
        var rm = okR ? PhysM(poses[iR]) : Matrix4x4.Identity;

        if (body.Step(dt, S, head, useL, lm, useR, rm))
        {
            session = body.X;
            PushTotal();
        }

        // руки отпущены и всё остановилось - заканчиваем "заплыв"
        if (!(useL || useR) && body.Speed < 0.02f && body.Spin < 0.03f) EndSwim();
    }

    static void EndSwim()
    {
        if (!swimActive) return;
        swimActive = false;
        output?.Flush();
        committed = Rigid(body.X * committed);
        session = Matrix4x4.Identity;
        body.Reset();
        Console.WriteLine("Плавание: остановка");
    }

    // ================= ГРАНИЦЫ ЗОНЫ =================
    static void SetBoundsHidden(bool hide)
    {
        if (hide == boundsHidden) return;
        var st = OpenVR.Settings;
        var e = EVRSettingsError.None;
        const string sec = OpenVR.k_pch_CollisionBounds_Section, key = OpenVR.k_pch_CollisionBounds_Style_Int32;
        if (hide)
        {
            int old = st.GetInt32(sec, key, ref e);
            try { Directory.CreateDirectory(Settings.Dir); File.WriteAllText(BoundsBackupFile, old.ToString()); } catch { }
            st.SetInt32(sec, key, (int)ECollisionBoundsStyle.COLLISION_BOUNDS_STYLE_NONE, ref e);
            Console.WriteLine("Границы зоны скрыты (было: стиль " + old + ")");
        }
        else
        {
            RestoreBoundsFromBackup();
        }
        boundsHidden = hide;
    }

    static void RestoreBoundsFromBackup()
    {
        try
        {
            if (!File.Exists(BoundsBackupFile)) return;
            if (int.TryParse(File.ReadAllText(BoundsBackupFile).Trim(), out int old))
            {
                var e = EVRSettingsError.None;
                OpenVR.Settings.SetInt32(OpenVR.k_pch_CollisionBounds_Section, OpenVR.k_pch_CollisionBounds_Style_Int32, old, ref e);
                Console.WriteLine("Границы зоны возвращены (стиль " + old + ")");
            }
            File.Delete(BoundsBackupFile);
        }
        catch { }
    }

    // ================= САМОПРОВЕРКА (F8) =================
    /// <summary>
    /// Два независимых канала - проверяются отдельно.
    /// 1) Наклон/переворот (драйвер): включает известный чистый поворот без сдвига и смотрит, какие
    ///    устройства получили именно его - через сырые позы (TrackingUniverseRawAndUncalibrated).
    /// 2) Сдвиг/рыск (chaperone): включает известный сдвиг и проверяет, что SteamVR-овская zero pose
    ///    действительно сдвинулась на столько же - сырые позы её не видят, поэтому первым способом
    ///    не проверяется.
    /// </summary>
    static void SelfTest(CVRSystem sys)
    {
        AbortMotion();
        PickOutput();
        if (output == null) { Console.WriteLine("Самопроверка: вывод не выбран."); return; }

        Console.WriteLine();
        Console.WriteLine("=== Самопроверка " + AppName + ": стой неподвижно ~1 секунду ===");
        Console.WriteLine("Вывод: " + output.Name);
        Console.WriteLine($"Драйвер: {link.State}, хуки={link.HookMask}, позы через хук={link.PoseCount}, применено={link.AppliedCount}");

        Console.WriteLine($"6DOF через SteamVR: {VrPaths.State}");

        if (output is ChaperoneOutput fullOut && fullOut.FullRotation)
        {
            SelfTestFull();
            return;
        }

        bool hasDriver = output is HybridOutput ho && ho.HasDriver;
        Console.WriteLine();
        Console.WriteLine("--- Канал 1: наклон/переворот (драйвер) ---");
        if (!hasDriver)
        {
            Console.WriteLine("Драйвера нет - канал недоступен, все устройства покажут 'НЕ ПРИМЕНЕНО'. Это нормально: значит доступны только сдвиг и повороты влево-вправо.");
        }

        ulong appliedStart = link.AppliedCount;
        committed = Matrix4x4.Identity;
        session = Matrix4x4.Identity;
        PushTotal();
        Thread.Sleep(400);

        var a = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        sys.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0f, a);

        // пробный наклон: чистый поворот на 20° вокруг Z (не рыск - в chaperone не уйдёт), без сдвига,
        // чтобы проверять именно то, что реально идёт в драйвер (см. HybridOutput.Split)
        var probe = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f));
        session = probe;
        PushTotal();
        Thread.Sleep(400);

        var b = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        sys.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0f, b);

        session = Matrix4x4.Identity;
        PushTotal();

        int transformed = 0, untouched = 0;
        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            if (!a[i].bPoseIsValid || !b[i].bPoseIsValid) continue;
            var ma = Conv.ToMatrix(a[i].mDeviceToAbsoluteTracking);
            var mb = Conv.ToMatrix(b[i].mDeviceToAbsoluteTracking);

            // при нулевом сдвиге зонда сравниваем не позицию (она у большинства устройств далека от
            // центра поворота и её тяжело интерпретировать без учёта плеча), а сам поворот "вперёд"
            Vector3 fwdA = Vector3.TransformNormal(-Vector3.UnitZ, ma);
            Vector3 fwdB = Vector3.TransformNormal(-Vector3.UnitZ, mb);
            Vector3 fwdOnce = Vector3.TransformNormal(fwdA, probe);
            Vector3 fwdTwice = Vector3.TransformNormal(fwdOnce, probe);

            float eSame = (fwdB - fwdA).Length();
            float eOnce = (fwdB - fwdOnce).Length();
            float eTwice = (fwdB - fwdTwice).Length();

            string verdict;
            if (eOnce < 0.03f) { verdict = "ОК - наклон применён"; transformed++; }
            else if (eSame < 0.03f) { verdict = "НЕ ПРИМЕНЕНО - устройство прошло мимо драйвера"; untouched++; }
            else if (eTwice < 0.03f) { verdict = "ПРИМЕНЕНО ДВАЖДЫ - двойной перехват"; }
            else verdict = $"ИНАЧЕ - направление сменилось на {eSame:F2}, но не туда (ошибка {eOnce:F2})";

            Console.WriteLine($"  #{i,-2} {sys.GetTrackedDeviceClass(i),-24} " +
                              $"{Prop(sys, i, ETrackedDeviceProperty.Prop_TrackingSystemName_String)} / " +
                              $"{Prop(sys, i, ETrackedDeviceProperty.Prop_ModelNumber_String)} / " +
                              $"{Prop(sys, i, ETrackedDeviceProperty.Prop_SerialNumber_String)}");
            Console.WriteLine($"       -> {verdict}  [видено через хук: {SeenBy(i)}]");
        }
        Console.WriteLine($"Итого: наклон применён к {transformed} устройствам, прошли мимо {untouched}. Драйвер применил его {link.AppliedCount - appliedStart} раз за тест.");

        // --- Канал 2: сдвиг/рыск (chaperone) - сырые позы его не видят, проверяем саму zero pose ---
        Console.WriteLine();
        Console.WriteLine("--- Канал 2: сдвиг и повороты влево-вправо (chaperone) ---");
        var cs = OpenVR.ChaperoneSetup;
        cs.RevertWorkingCopy();
        var z0 = new HmdMatrix34_t();
        bool haveZ0 = cs.GetWorkingStandingZeroPoseToRawTrackingPose(ref z0);

        const float probeShift = 0.30f;
        committed = Matrix4x4.Identity;
        session = Matrix4x4.CreateTranslation(probeShift, 0, 0);
        PushTotal();
        output.Flush();
        Thread.Sleep(300);

        cs.RevertWorkingCopy();
        var z1 = new HmdMatrix34_t();
        bool haveZ1 = cs.GetWorkingStandingZeroPoseToRawTrackingPose(ref z1);

        session = Matrix4x4.Identity;
        PushTotal();
        output.Flush();

        if (haveZ0 && haveZ1)
        {
            float moved = new Vector3(z1.m3 - z0.m3, z1.m7 - z0.m7, z1.m11 - z0.m11).Length();
            Console.WriteLine(moved > probeShift * 0.5f
                ? $"ОК - zero pose сдвинулась на {moved:F2} м (ожидалось ~{probeShift:F2} м)"
                : $"НЕ ПРИМЕНЕНО - zero pose сдвинулась всего на {moved:F2} м. Проверь: SteamVR Settings > Developer (права на запись chaperone), не мешает ли OVR Advanced Settings или другой инструмент, тоже трогающий пространство.");
        }
        else Console.WriteLine("Не удалось прочитать zero pose (GetWorkingStandingZeroPoseToRawTrackingPose вернул false).");

        Console.WriteLine("=== Конец самопроверки ===");
        Console.WriteLine();
    }

    /// <summary>
    /// Самопроверка режима "6DOF через SteamVR": сырые позы тут не меняются, поэтому проверяем саму матрицу
    /// "сырое -> standing", которую рантайм отдаёт приложениям (GetRawZeroPoseToStandingAbsoluteTrackingPose).
    /// Ожидается: после пробной трансформации T она равна T * (матрица до).
    /// </summary>
    static void SelfTestFull()
    {
        var sys = OpenVR.System;
        committed = Matrix4x4.Identity;
        session = Matrix4x4.Identity;
        PushTotal();
        output.Flush();
        Thread.Sleep(300);
        var before = Conv.ToMatrix(sys.GetRawZeroPoseToStandingAbsoluteTrackingPose());

        bool allOk = true;
        void Check(string what, Matrix4x4 probe)
        {
            session = probe;
            PushTotal();
            Thread.Sleep(300);
            output.Tick();
            var after = Conv.ToMatrix(sys.GetRawZeroPoseToStandingAbsoluteTrackingPose());
            var expect = probe * before;
            float eRot = (Vector3.TransformNormal(-Vector3.UnitZ, after) - Vector3.TransformNormal(-Vector3.UnitZ, expect)).Length()
                       + (Vector3.TransformNormal(Vector3.UnitY, after) - Vector3.TransformNormal(Vector3.UnitY, expect)).Length();
            float ePos = (after.Translation - expect.Translation).Length();
            float eSame = (Vector3.TransformNormal(Vector3.UnitY, after) - Vector3.TransformNormal(Vector3.UnitY, before)).Length()
                        + (after.Translation - before.Translation).Length();
            string verdict = eRot < 0.03f && ePos < 0.02f ? "ОК"
                           : eSame < 0.01f ? "НЕ ПРИМЕНЕНО - SteamVR не принял матрицу"
                           : $"ИНАЧЕ - ошибка поворота {eRot:F3}, сдвига {ePos:F3} м";
            if (!verdict.StartsWith("ОК")) allOk = false;
            Console.WriteLine($"  {what,-34} -> {verdict}");
        }

        Console.WriteLine();
        Console.WriteLine("--- 6DOF через SteamVR (как OVRAS Space Orient) ---");
        Check("наклон 20° (крен, вокруг Z)", Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, 0.35f));
        Check("наклон 20° (тангаж, вокруг X)", Matrix4x4.CreateFromAxisAngle(Vector3.UnitX, 0.35f));
        Check("поворот 30° влево-вправо", Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, 0.52f));
        Check("сдвиг 0.3 м + переворот 180°",
            Matrix4x4.CreateFromAxisAngle(Vector3.UnitX, MathF.PI) * Matrix4x4.CreateTranslation(0.3f, 0, 0));

        session = Matrix4x4.Identity;
        PushTotal();
        output.Flush();
        Console.WriteLine(allOk
            ? "Итого: всё применяется через SteamVR, драйвер для наклона не нужен."
            : "Итого: есть ошибки. Проверь, не включён ли одновременно Space Drag/Space Orient в OVR Advanced Settings.");
        Console.WriteLine("=== Конец самопроверки ===");
        Console.WriteLine();
    }

    static string SeenBy(uint dev)
    {
        if (dev >= 64) return "?";
        bool s0 = (link.SeenMask0 & (1UL << (int)dev)) != 0;
        bool s1 = (link.SeenMask1 & (1UL << (int)dev)) != 0;
        if (s0 && s1) return "_005 и _006";
        if (s0) return "только _005";
        if (s1) return "только _006";
        return "НИ РАЗУ (вызов до хука не дошёл)";
    }

    static string Prop(CVRSystem sys, uint dev, ETrackedDeviceProperty p)
    {
        var e = ETrackedPropertyError.TrackedProp_Success;
        var sb = new StringBuilder(128);
        sys.GetStringTrackedDeviceProperty(dev, p, sb, 128, ref e);
        return e == ETrackedPropertyError.TrackedProp_Success && sb.Length > 0 ? sb.ToString() : "-";
    }

    // ================= ДВОЙНОЕ НАЖАТИЕ ГРИПОВ =================
    /// <summary>
    /// "Нажатие" = оба грипа сжаты почти одновременно (разница до 0.2 с) и отпущены быстро (до 0.35 с).
    /// Два таких нажатия подряд (до 0.5 с между ними) - true. Долгие хваты и гребки сюда не попадают.
    /// </summary>
    static bool DoubleTap(bool gl, bool gr)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (gl && !prevGL) tDownL = now;
        if (gr && !prevGR) tDownR = now;
        prevGL = gl; prevGR = gr;

        bool both = gl && gr;
        bool fired = false;
        if (both && !bothDown)
        {
            tBothDown = now;
            bothValid = Math.Abs(tDownL - tDownR) <= TapMaxSkew;
        }
        else if (!both && bothDown)
        {
            if (bothValid && now - tBothDown <= TapMaxHold)
            {
                if (now - tLastTap <= S.DoubleTapWindow) { fired = true; tLastTap = -10; }
                else tLastTap = now;
            }
            else tLastTap = -10;
        }
        bothDown = both;
        return fired;
    }

    static void ToggleMode()
    {
        var m = ActiveMode;
        if (m != Mode.None)
        {
            lastMode = m;
            S.RollEnabled = false; S.SwimEnabled = false;
            ui.Toast((m == Mode.Swim ? "Плавание" : "Вращение") + " выключено", Ui.Amber, 2);
            Haptic(0.06f, 0.5f); Haptic(0.06f, 0.5f, 0.15f); // два коротких - выкл
        }
        else
        {
            if (lastMode == Mode.Swim) S.SwimEnabled = true; else S.RollEnabled = true;
            ui.Toast((lastMode == Mode.Swim ? "Плавание" : "Вращение") + " включено", Ui.Green, 2);
            Haptic(0.25f, 0.9f); // один длинный - вкл
        }
        if (ui.Tab < 2) ui.Tab = S.SwimEnabled ? 1 : S.RollEnabled ? 0 : ui.Tab;
        ui.AcceptModeChange();
        Console.WriteLine("Двойное нажатие грипов: " + ActiveMode);
    }

    static void Haptic(float duration, float amplitude, float delay = 0f)
    {
        if (!S.Haptics) return;
        try { OpenVR.Input.TriggerHapticVibrationAction(hapticH, delay, duration, 160f, amplitude, OpenVR.k_ulInvalidInputValueHandle); }
        catch { }
    }

    // ---------- вспомогательное ----------
    static bool ReadDigital(ulong h)
    {
        var d = new InputDigitalActionData_t();
        var e = OpenVR.Input.GetDigitalActionData(h, ref d, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), OpenVR.k_ulInvalidInputValueHandle);
        return e == EVRInputError.None && d.bActive && d.bState;
    }

    static bool KeyDown(int vk) => OperatingSystem.IsWindows() && (GetAsyncKeyState(vk) & 0x8000) != 0;

    static float WrapPi(float a) => a - MathF.Tau * MathF.Floor((a + MathF.PI) / MathF.Tau);
}