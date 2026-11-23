using System.Text.Json;

/// <summary>Все настройки. Хранятся в %APPDATA%\AquaMotionVR\settings.json</summary>
sealed class Settings
{
    // ---- какой режим включён (одновременно только один) ----
    public bool RollEnabled { get; set; } = true;
    public bool SwimEnabled { get; set; } = false;

    // ---- общие ----
    public bool ResetOnExit { get; set; } = true;        // вернуть пространство при выходе

    // ---- вращение ----
    public bool UseLeftHand { get; set; } = true;        // какой контроллер вращает мир
    public bool AxisMode { get; set; } = true;           // true: вокруг одной оси, false: всеми осями как рука
    public bool AxisFromHmd { get; set; } = false;       // ось: false = "вперёд" контроллера, true = взгляд
    public bool WorldFollowsHand { get; set; } = false;  // false: ты вращаешься за рукой, true: мир крутится как рука
    public float Multiplier { get; set; } = 1.0f;        // множитель угла
    public bool Invert { get; set; } = false;            // обратное направление

    // ---- плавание ----
    public float SwimStrength { get; set; } = 2.5f;       // сила гребка
    public float SwimDrag { get; set; } = 1.2f;          // сопротивление воды (линейное затухание, 1/с)
    public float SwimMaxSpeed { get; set; } = 4f;        // предел скорости, м/с
    public float SwimTurn { get; set; } = 1f;            // сила поворота от гребка
    public float SwimAngDrag { get; set; } = 1.5f;       // затухание вращения, 1/с
    public float SwimGaze { get; set; } = 0.5f;         // руль взглядом 0..1
    public bool SwimInvertTurn { get; set; } = false;    // обратный поворот от гребка
    public bool HideBounds { get; set; } = true;         // прятать границы игровой зоны, пока включено плавание (нужен драйвер)

    // ---- управление ----
    public bool DoubleTapToggle { get; set; } = true;    // двойное нажатие обоих грипов включает/выключает режим
    public float DoubleTapWindow { get; set; } = 0.5f;   // сколько секунд между двумя нажатиями
    public bool Haptics { get; set; } = true;            // вибрация-подтверждение
    public float DragSpeed { get; set; } = 1f;           // перетаскивание: 1 - рука "прилипает" к миру, 2 - вдвое быстрее
    public bool DragVerticalOnly { get; set; } = false;  // перетаскивание только вверх-вниз

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string Dir => Path.Combine(AppData, "AquaMotionVR");
    public static string FilePath => Path.Combine(Dir, "settings.json");
    static string OldFilePath => Path.Combine(AppData, "RollWorld", "settings.json"); // до переименования
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            // программа раньше называлась RollWorld - забираем старые настройки один раз
            if (!File.Exists(FilePath) && File.Exists(OldFilePath))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(OldFilePath, FilePath);
                Console.WriteLine("Настройки перенесены из RollWorld.");
            }
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null) { s.Clamp(); return s; }
            }
        }
        catch (Exception e) { Console.WriteLine("Не удалось прочитать настройки, беру по умолчанию: " + e.Message); }
        return new Settings();
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt));
            return true;
        }
        catch (Exception e) { Console.WriteLine("Не удалось сохранить настройки: " + e.Message); return false; }
    }

    static float R2(float v, float lo, float hi) => MathF.Round(Math.Clamp(v, lo, hi) * 100f) / 100f;

    public void Clamp()
    {
        Multiplier = R2(Multiplier, 0.25f, 3f);
        SwimStrength = R2(SwimStrength, 0.5f, 10f);
        SwimDrag = R2(SwimDrag, 0.3f, 4f);
        SwimMaxSpeed = R2(SwimMaxSpeed, 1f, 8f);
        SwimTurn = R2(SwimTurn, 0f, 3f);
        SwimAngDrag = R2(SwimAngDrag, 0.3f, 4f);
        SwimGaze = R2(SwimGaze, 0f, 1f);
        DoubleTapWindow = R2(DoubleTapWindow, 0.3f, 0.9f);
        DragSpeed = R2(DragSpeed, 0.25f, 5f);
        if (RollEnabled && SwimEnabled) SwimEnabled = false; // одновременно нельзя
    }

    public float EffectiveMultiplier => Invert ? -Multiplier : Multiplier;

    public void ResetRoll()
    {
        var d = new Settings();
        UseLeftHand = d.UseLeftHand; AxisMode = d.AxisMode; AxisFromHmd = d.AxisFromHmd; WorldFollowsHand = d.WorldFollowsHand;
        Multiplier = d.Multiplier; Invert = d.Invert;
    }

    public void ResetSwim()
    {
        var d = new Settings();
        SwimStrength = d.SwimStrength; SwimDrag = d.SwimDrag; SwimMaxSpeed = d.SwimMaxSpeed; SwimTurn = d.SwimTurn;
        SwimAngDrag = d.SwimAngDrag; SwimGaze = d.SwimGaze; SwimInvertTurn = d.SwimInvertTurn; HideBounds = d.HideBounds;
    }

    public void ResetControls()
    {
        var d = new Settings();
        DoubleTapToggle = d.DoubleTapToggle; DoubleTapWindow = d.DoubleTapWindow; Haptics = d.Haptics;
        DragSpeed = d.DragSpeed; DragVerticalOnly = d.DragVerticalOnly;
    }

    public Settings Clone() => (Settings)MemberwiseClone();
    public bool SameAs(Settings o) => JsonSerializer.Serialize(this) == JsonSerializer.Serialize(o);
}
