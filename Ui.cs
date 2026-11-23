using System.Text.Json;

/// <summary>Интерфейс настроек для панели SteamVR. Рисуется программно в текстуру 1280x896.</summary>
sealed class Ui
{
    public const int W = 1280, H = 896;
    // Если в VR кнопки срабатывают "зеркально" по вертикали - поменяй на false
    const bool FlipMouseY = true;

    // ---- палитра ----
    internal static readonly Col BgTop = Col.Hex(0x0D0F1E), BgBot = Col.Hex(0x171A35);
    internal static readonly Col Card = Col.Hex(0x1B1F3D), CardBorder = Col.Hex(0x2C3260);
    internal static readonly Col Txt = Col.Hex(0xF3F5FF), Txt2 = Col.Hex(0xA3A9D3), Muted = Col.Hex(0x666C99);
    internal static readonly Col AccA = Col.Hex(0x7C5CFF), AccB = Col.Hex(0x21D4FD);
    internal static readonly Col Track = Col.Hex(0x2B3159), Well = Col.Hex(0x121529), WellBorder = Col.Hex(0x272C55);
    internal static readonly Col Green = Col.Hex(0x3DDC97), Amber = Col.Hex(0xFFB547), Red = Col.Hex(0xFF5C7A), White = Col.Hex(0xFFFFFF);
    internal static readonly Col Btn2 = Col.Hex(0x262B55), Btn2Border = Col.Hex(0x3A4080);

    internal static Face F(int size, bool bold = false) => Fonts.Get(size, bold);

    public readonly Canvas Canvas = new(W, H);
    public bool Dirty = true;
    internal float Mx, My;

    readonly Settings s;
    readonly Action onReset;
    readonly Func<bool> onBindings;
    Settings saved;
    int tab; // 0 - вращение, 1 - плавание, 2 - кнопки
    readonly List<Widget> widgets = new();
    readonly List<Lbl> labels = new();
    Widget hover, pressed;
    string toast; Col toastCol; DateTime toastUntil;
    readonly byte[][] statics = new byte[3][];

    // Tab у виджета/надписи: -1 - на всех вкладках, -2 - только на вкладках режимов (0 и 1)
    bool OnTab(int t) => t == -1 || t == tab || (t == -2 && tab < 2);

    public int Tab { get => tab; set { tab = value; Dirty = true; } }

    int driverState; // 0 - не найден, 1 - подключён, 2 - хук не активен
    public int DriverState { get => driverState; set { if (driverState != value) { driverState = value; Dirty = true; } } }

    bool pathsReady; // SteamVR принимает полную матрицу пространства (6DOF без драйвера)
    public bool PathsReady { get => pathsReady; set { if (pathsReady != value) { pathsReady = value; Dirty = true; } } }

    readonly record struct Lbl(float X, float Base, string Text, int Size, bool Bold, Func<bool> On, bool Hint, int Tab, int Align = 0);

    public Ui(Settings settings, Action resetNow, Func<bool> openBindings)
    {
        s = settings; onReset = resetNow; onBindings = openBindings; saved = s.Clone();
        tab = s.SwimEnabled ? 1 : 0;
        Build();
    }

    // ---------------- раскладка ----------------
    const float CardY = 196, CardH = 568, Lx = 48, Rx = 660, Cw = 572, Pad = 36;
    static readonly Func<bool> Always = () => true;

    void AddSegRow(int t, float x, float top, string label, string hint, string[] opts, Func<int> get, Action<int> set, Func<bool> en)
    {
        labels.Add(new Lbl(x + Pad, top + 44, label, 28, false, en, false, t));
        labels.Add(new Lbl(x + Pad, top + 74, hint, 20, false, en, true, t));
        widgets.Add(new Segmented { Tab = t, X = x + Cw - Pad - 260, Y = top + 22, W = 260, H = 52, Opts = opts, Get = get, Set = set, Enabled = en });
    }

    void AddSliderRow(int t, float x, float top, string label, string hint, float min, float max, float step,
                      Func<float> get, Action<float> set, Func<string> fmt)
    {
        float sx = x + Pad, sw = Cw - 2 * Pad;
        labels.Add(new Lbl(sx, top + 32, label, 28, false, Always, false, t));
        widgets.Add(new Slider { Tab = t, X = sx, Y = top + 46, W = sw, H = 48, Min = min, Max = max, Step = step,
            Get = get, Set = v => set(MathF.Round(v * 100f) / 100f), Value = fmt, Enabled = Always });
        labels.Add(new Lbl(sx, top + 112, hint, 20, false, Always, true, t));
    }

    void AddToggleRow(int t, float x, float top, string label, string hint, Func<bool> get, Action<bool> set)
    {
        labels.Add(new Lbl(x + Pad, top + 44, label, 28, false, Always, false, t));
        labels.Add(new Lbl(x + Pad, top + 74, hint, 20, false, Always, true, t));
        widgets.Add(new Toggle { Tab = t, X = x + Cw - Pad - 76, Y = top + 30, W = 76, H = 40, Get = get, Set = set, Enabled = Always });
    }

    void Build()
    {
        // ---- вкладки и переключатель режима ----
        widgets.Add(new TabButton { X = 48, Y = 124, W = 196, H = 52, Text = "Вращение", Enabled = Always,
            IsActive = () => tab == 0, ModeOn = () => s.RollEnabled, Click = () => Tab = 0 });
        widgets.Add(new TabButton { X = 256, Y = 124, W = 196, H = 52, Text = "Плавание", Enabled = Always,
            IsActive = () => tab == 1, ModeOn = () => s.SwimEnabled, Click = () => Tab = 1 });
        widgets.Add(new TabButton { X = 464, Y = 124, W = 170, H = 52, Text = "Кнопки", Enabled = Always, NoDot = true,
            IsActive = () => tab == 2, ModeOn = () => false, Click = () => Tab = 2 });
        labels.Add(new Lbl(W - 48 - 76 - 20, 158, "Режим включён", 26, false, Always, false, -2, 2));
        widgets.Add(new Toggle { Tab = -2, X = W - 48 - 76, Y = 130, W = 76, H = 40, Enabled = Always,
            Get = () => tab == 0 ? s.RollEnabled : s.SwimEnabled, Set = v => SetModeEnabled(tab, v) });

        // ---- вкладка "Вращение" ----
        Func<bool> axisOn = () => s.AxisMode;
        float y0 = CardY + 72;
        const float RowH = 96; // четыре строки слева
        AddSegRow(0, Lx, y0 + 0 * RowH, "Контроллер", "Чья рука вращает", new[] { "Левый", "Правый" },
            () => s.UseLeftHand ? 0 : 1, v => s.UseLeftHand = v == 0, Always);
        AddSegRow(0, Lx, y0 + 1 * RowH, "Режим", "Ось или всё сразу", new[] { "По оси", "Полный" },
            () => s.AxisMode ? 0 : 1, v => s.AxisMode = v == 0, Always);
        AddSegRow(0, Lx, y0 + 2 * RowH, "Ось", "Вокруг чего", new[] { "Рука", "Взгляд" },
            () => s.AxisFromHmd ? 1 : 0, v => s.AxisFromHmd = v == 1, axisOn);
        AddSegRow(0, Lx, y0 + 3 * RowH, "Вращается", "Кто крутится", new[] { "Я", "Мир" },
            () => s.WorldFollowsHand ? 1 : 0, v => s.WorldFollowsHand = v == 1, Always);
        AddSliderRow(0, Rx, y0, "Чувствительность", "1× — угол в угол", 0.25f, 3f, 0.05f,
            () => s.Multiplier, v => s.Multiplier = v, () => s.Multiplier.ToString("0.00") + "×");
        AddToggleRow(0, Rx, y0 + 120, "Инвертировать", "Обратное направление вращения", () => s.Invert, v => s.Invert = v);

        // ---- вкладка "Плавание" ----
        AddSliderRow(1, Lx, y0, "Сила гребка", "Тяга от взмаха руки", 0.5f, 10f, 0.5f,
            () => s.SwimStrength, v => s.SwimStrength = v, () => s.SwimStrength.ToString("0.0"));
        AddSliderRow(1, Lx, y0 + 120, "Сопротивление воды", "Больше — быстрее останавливаешься", 0.3f, 4f, 0.1f,
            () => s.SwimDrag, v => s.SwimDrag = v, () => s.SwimDrag.ToString("0.0"));
        AddSliderRow(1, Lx, y0 + 240, "Макс. скорость", "Предел скорости", 1f, 8f, 0.5f,
            () => s.SwimMaxSpeed, v => s.SwimMaxSpeed = v, () => s.SwimMaxSpeed.ToString("0.0") + " м/с");
        AddToggleRow(1, Lx, y0 + 360, "Прятать границы зоны", "Иначе SteamVR будет мигать сеткой", () => s.HideBounds, v => s.HideBounds = v);

        AddSliderRow(1, Rx, y0, "Поворот от гребка", "Одна рука или разные взмахи — вращение", 0f, 3f, 0.1f,
            () => s.SwimTurn, v => s.SwimTurn = v, () => s.SwimTurn.ToString("0.0") + "×");
        AddSliderRow(1, Rx, y0 + 120, "Затухание вращения", "Больше — быстрее перестаёшь крутиться", 0.3f, 4f, 0.1f,
            () => s.SwimAngDrag, v => s.SwimAngDrag = v, () => s.SwimAngDrag.ToString("0.0"));
        AddSliderRow(1, Rx, y0 + 240, "Руль взглядом", "Скорость плавно поворачивает за взглядом", 0f, 1f, 0.05f,
            () => s.SwimGaze, v => s.SwimGaze = v, () => (int)MathF.Round(s.SwimGaze * 100) + "%");
        AddToggleRow(1, Rx, y0 + 360, "Инвертировать поворот", "Крутиться в другую сторону от гребка", () => s.SwimInvertTurn, v => s.SwimInvertTurn = v);

        // ---- вкладка "Кнопки": шпаргалка + настройки управления ----
        var help = new (string key, string what)[]
        {
            ("Оба грипа — 2 раза быстро", "Включить / выключить режим"),
            ("Держи клик левого стика", "Вращение: крути контроллер"),
            ("Держи грипы и греби", "Плавание: взмахи руками"),
            ("Клик правого стика", "Сбросить положение"),
            ("Перетаскивание — назначь сам", "Держи кнопку и тяни себя рукой"),
            ("F9 · F10 · F8 на клавиатуре", "Захват · сброс · самопроверка"),
        };
        for (int i = 0; i < help.Length; i++)
        {
            float ty = y0 - 6 + i * 76;
            labels.Add(new Lbl(Lx + Pad, ty + 30, help[i].key, 26, true, Always, false, 2));
            labels.Add(new Lbl(Lx + Pad, ty + 58, help[i].what, 22, false, Always, true, 2));
        }

        AddToggleRow(2, Rx, y0 - 14, "Двойное нажатие", "Оба грипа ×2 — вкл/выкл режима", () => s.DoubleTapToggle, v => s.DoubleTapToggle = v);
        AddToggleRow(2, Rx, y0 + 66, "Вибрация", "Подтверждение в контроллерах", () => s.Haptics, v => s.Haptics = v);
        AddSliderRow(2, Rx, y0 + 150, "Скорость перетаскивания", "1× — рука как будто держит мир", 0.25f, 5f, 0.25f,
            () => s.DragSpeed, v => s.DragSpeed = v, () => s.DragSpeed.ToString("0.00") + "×");
        AddToggleRow(2, Rx, y0 + 262, "Только вверх-вниз", "Перетаскивание лишь по высоте", () => s.DragVerticalOnly, v => s.DragVerticalOnly = v);
        widgets.Add(new Button { Tab = 2, X = Rx + Pad, Y = y0 + 356, W = Cw - 2 * Pad, H = 60, Text = "Настроить кнопки", Enabled = Always,
            Click = () =>
            {
                if (onBindings()) Toast("Открыто окно привязок SteamVR", AccB, 3);
                else Toast("Не удалось открыть привязки", Red, 3);
            } });
        labels.Add(new Lbl(Rx + Pad, y0 + 446, "Перетаскивание: «Drag yourself» в привязках", 20, false, Always, true, 2));

        // ---- нижняя панель ----
        widgets.Add(new Button { X = 48, Y = 792, W = 320, H = 60, Text = "Сбросить положение", Enabled = Always,
            Click = () => { onReset(); Toast("Положение сброшено", AccB); } });
        widgets.Add(new Button { X = 384, Y = 792, W = 220, H = 60, Text = "По умолчанию", Enabled = Always,
            Click = () =>
            {
                if (tab == 0) s.ResetRoll(); else if (tab == 1) s.ResetSwim(); else s.ResetControls();
                Toast("Значения по умолчанию — не забудь сохранить", Amber, 3);
            } });
        labels.Add(new Lbl(626, 830, "Сброс при выходе", 22, false, Always, false, -1));
        widgets.Add(new Toggle { X = 852, Y = 802, W = 76, H = 40, Enabled = Always, Get = () => s.ResetOnExit, Set = v => s.ResetOnExit = v });
        widgets.Add(new Button { X = W - 48 - 280, Y = 792, W = 280, H = 60, Text = "Сохранить", Primary = true, Enabled = Always, Click = DoSave });
    }

    /// <summary>Режимы взаимно исключают друг друга: включаешь один - другой выключается.</summary>
    void SetModeEnabled(int t, bool on)
    {
        if (t == 0)
        {
            s.RollEnabled = on;
            if (on && s.SwimEnabled) { s.SwimEnabled = false; Toast("Вращение вкл., плавание выкл.", Green, 3); }
        }
        else
        {
            s.SwimEnabled = on;
            if (on && s.RollEnabled) { s.RollEnabled = false; Toast("Плавание вкл., вращение выкл.", Green, 3); }
        }
    }

    /// <summary>Режим переключили двойным нажатием грипов - это не "несохранённое изменение" панели.</summary>
    public void AcceptModeChange()
    {
        saved.RollEnabled = s.RollEnabled; saved.SwimEnabled = s.SwimEnabled;
        Dirty = true;
    }

    void DoSave()
    {
        if (s.Save()) { saved = s.Clone(); Toast("Сохранено", Green); }
        else Toast("Ошибка сохранения", Red, 3);
    }

    public void Toast(string text, Col c, double seconds = 2)
    {
        toast = text; toastCol = c; toastUntil = DateTime.UtcNow.AddSeconds(seconds); Dirty = true;
    }

    public void Tick()
    {
        if (toast != null && DateTime.UtcNow > toastUntil) { toast = null; Dirty = true; }
    }

    // ---------------- мышь (лазер контроллера) ----------------
    void Map(float rx, float ry) { Mx = rx; My = FlipMouseY ? H - ry : ry; }

    bool Visible(Widget w) => OnTab(w.Tab);

    Widget HitTest()
    {
        for (int i = widgets.Count - 1; i >= 0; i--)
            if (Visible(widgets[i]) && widgets[i].Enabled() && widgets[i].Contains(Mx, My)) return widgets[i];
        return null;
    }

    public void Move(float rx, float ry)
    {
        Map(rx, ry);
        if (pressed != null) { pressed.Drag(this, Mx, My); Dirty = true; }
        var h = HitTest();
        if (h != hover) { hover = h; Dirty = true; }
        else if (hover != null) Dirty = true; // подсветка сегмента под курсором
    }

    public void Down(float rx, float ry)
    {
        Map(rx, ry);
        hover = pressed = HitTest();
        if (pressed != null) pressed.Down(this, Mx, My);
        Dirty = true;
    }

    public void Up(float rx, float ry)
    {
        Map(rx, ry);
        if (pressed != null) { pressed.Up(this, Mx, My, pressed.Contains(Mx, My)); pressed = null; }
        hover = HitTest();
        Dirty = true;
    }

    public void Leave() { hover = null; pressed = null; Dirty = true; }

    // ---------------- отрисовка ----------------
    void DrawCard(float x, float y, float w, float h, string title)
    {
        var c = Canvas;
        c.Shadow(x, y + 10, w, h, 28, 40, new Col(0, 0, 0, 0.45f));
        c.RoundRect(x, y, w, h, 28, Card);
        c.RoundRectBorder(x, y, w, h, 28, 1.5f, CardBorder);
        c.RoundRect(x + 36, y + 32, 6, 20, 3, AccA, AccB);
        c.Text(F(22, true), title, x + 54, y + 50, Txt2);
    }

    // Неподвижная часть (фон, шапка, карточки) рисуется один раз на вкладку и потом просто копируется
    void BuildStatic(int t)
    {
        var c = Canvas;
        c.FillVertical(BgTop, BgBot);
        c.Glow(180, 110, 560, AccA.Alpha(0.24f));
        c.Glow(1130, H - 70, 600, AccB.Alpha(0.15f));

        c.Shadow(48, 34, 68, 68, 20, 26, AccA.Alpha(0.55f));
        c.RoundRect(48, 34, 68, 68, 20, AccA, AccB);
        c.Text(F(44, true), "A", 82, 84, White, 1);
        c.Text(F(44, true), Program.AppName, 138, 72, Txt);
        c.Text(F(22), t == 0 ? "Держи клик стика (F9) и крути · 2× оба грипа — вкл/выкл"
                    : t == 1 ? "Зажми грипы и греби (F9) · 2× оба грипа — вкл/выкл"
                             : "Какие кнопки что делают и как их поменять", 138, 102, Txt2);

        if (t == 0) { DrawCard(Lx, CardY, Cw, CardH, "УПРАВЛЕНИЕ"); DrawCard(Rx, CardY, Cw, CardH, "ПАРАМЕТРЫ"); }
        else if (t == 1) { DrawCard(Lx, CardY, Cw, CardH, "ГРЕБОК"); DrawCard(Rx, CardY, Cw, CardH, "ВРАЩЕНИЕ В ВОДЕ"); }
        else { DrawCard(Lx, CardY, Cw, CardH, "КНОПКИ ПО УМОЛЧАНИЮ"); DrawCard(Rx, CardY, Cw, CardH, "НАСТРОЙКИ"); }
        statics[t] = (byte[])c.Px.Clone();
    }

    public void Render()
    {
        var c = Canvas;
        if (statics[tab] == null) BuildStatic(tab);
        else Buffer.BlockCopy(statics[tab], 0, c.Px, 0, statics[tab].Length);

        // статус справа
        string st; Col sc;
        if (toast != null) { st = toast; sc = toastCol; }
        else if (!s.SameAs(saved)) { st = "Есть несохранённые изменения"; sc = Amber; }
        else { st = "Сохранено"; sc = Green; }
        float pw = c.Measure(F(24), st) + 84, px = W - 48 - pw;
        c.RoundRect(px, 44, pw, 52, 26, sc.Alpha(0.14f));
        c.RoundRectBorder(px, 44, pw, 52, 26, 1.5f, sc.Alpha(0.55f));
        c.Circle(px + 28, 70, 6, sc);
        c.Text(F(24), st, px + 46, 78, sc);

        // состояние драйвера
        string dt; Col dc;
        if (pathsReady) { dt = "6DOF: SteamVR"; dc = Green; }
        else
        {
            dt = driverState == 1 ? "Драйвер подключён" : driverState == 2 ? "Драйвер: хуки выкл" : "Без наклона";
            dc = driverState == 1 ? Green : driverState == 2 ? Red : Amber;
        }
        float dw = c.Measure(F(22), dt) + 62;
        const float PillX = 650;
        c.RoundRect(PillX, 128, dw, 44, 22, dc.Alpha(0.14f));
        c.RoundRectBorder(PillX, 128, dw, 44, 22, 1.5f, dc.Alpha(0.55f));
        c.Circle(PillX + 26, 150, 5.5f, dc);
        c.Text(F(22), dt, PillX + 42, 158, dc);

        foreach (var l in labels)
        {
            if (!OnTab(l.Tab)) continue;
            bool on = l.On();
            Col col = l.Hint ? (on ? Muted : Muted.Fade(0.5f)) : (on ? Txt : Txt.Fade(0.35f));
            c.Text(F(l.Size, l.Bold), l.Text, l.X, l.Base, col, l.Align);
        }
        foreach (var w in widgets)
            if (Visible(w)) w.Draw(this, c, w == hover, w == pressed);
        Dirty = false;
        lastSig = Signature();
    }

    // ---- защита от мигания: SetOverlayRaw при каждой загрузке кадра на миг "гасит" панель,
    // поэтому загружаем картинку только если она реально изменилась ----
    int lastSig;

    int Signature()
    {
        var h = new HashCode();
        h.Add(tab);
        h.Add(widgets.IndexOf(hover));
        h.Add(widgets.IndexOf(pressed));
        h.Add(hover is Segmented sg ? sg.HoverIndex(Mx) : -1);
        h.Add(toast);
        h.Add(JsonSerializer.Serialize(s));
        h.Add(s.SameAs(saved));
        h.Add(driverState);
        h.Add(pathsReady);
        return h.ToHashCode();
    }

    /// <summary>true, если картинка отличается от последней нарисованной (и её надо загрузить в SteamVR)</summary>
    public bool ChangedSinceRender()
    {
        Dirty = false;
        return Signature() != lastSig;
    }

    public void SavePreview(string path) { Render(); Canvas.SavePng(path); }

    public static byte[] Thumbnail(int size)
    {
        var c = new Canvas(size, size);
        c.FillVertical(BgTop, BgBot);
        c.Glow(size * 0.3f, size * 0.25f, size * 0.9f, AccA.Alpha(0.35f));
        float m = size * 0.1f;
        c.RoundRect(m, m, size - 2 * m, size - 2 * m, size * 0.22f, AccA, AccB);
        float cx = size / 2f, cy = size / 2f, r = size * 0.26f;
        c.Circle(cx, cy, r, White);
        c.Circle(cx, cy, r * 0.68f, Col.Lerp(AccA, AccB, 0.5f));
        c.Circle(cx + r * 0.72f, cy - r * 0.72f, size * 0.07f, White);
        return c.Px;
    }
}

// ================= виджеты =================
abstract class Widget
{
    public float X, Y, W, H;
    public int Tab = -1; // -1: на всех вкладках
    public Func<bool> Enabled = () => true;
    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;
    public abstract void Draw(Ui ui, Canvas c, bool hover, bool pressed);
    public virtual void Down(Ui ui, float px, float py) { }
    public virtual void Drag(Ui ui, float px, float py) { }
    public virtual void Up(Ui ui, float px, float py, bool inside) { }
}

sealed class TabButton : Widget
{
    public string Text = "";
    public Func<bool> IsActive = () => false;
    public Func<bool> ModeOn = () => false;
    public bool NoDot; // вкладка не режим - без индикатора вкл/выкл
    public Action Click = () => { };

    public override void Draw(Ui ui, Canvas c, bool hover, bool pressed)
    {
        bool act = IsActive(), on = ModeOn();
        if (act)
        {
            c.Shadow(X, Y + 4, W, H, 26, 20, Ui.AccA.Alpha(0.38f));
            c.RoundRect(X, Y, W, H, 26, Ui.AccA, Ui.AccB, true);
        }
        else
        {
            c.RoundRect(X, Y, W, H, 26, hover ? Ui.Btn2.Mul(1.3f) : Ui.Well);
            c.RoundRectBorder(X, Y, W, H, 26, 1.5f, hover ? Ui.AccA.Alpha(0.9f) : Ui.WellBorder);
        }
        float cy = Y + H / 2;
        if (NoDot)
        {
            c.Text(Ui.F(26, true), Text, X + W / 2, cy + 9, act ? Ui.White : (hover ? Ui.Txt : Ui.Txt2), 1);
            return;
        }
        if (on)
        {
            c.Circle(X + 30, cy, 10, act ? Ui.White : Ui.Green.Alpha(0.35f));
            c.Circle(X + 30, cy, 6.5f, Ui.Green);
        }
        else c.Circle(X + 30, cy, 6.5f, act ? Ui.White.Alpha(0.4f) : Ui.Muted);
        c.Text(Ui.F(26, true), Text, X + 54, cy + 9, act ? Ui.White : (hover ? Ui.Txt : Ui.Txt2));
    }

    public override void Up(Ui ui, float px, float py, bool inside)
    {
        if (inside) Click();
    }
}

sealed class Segmented : Widget
{
    public string[] Opts = Array.Empty<string>();
    public Func<int> Get = () => 0;
    public Action<int> Set = _ => { };

    int IndexAt(float px) => Math.Clamp((int)((px - X - 4) / ((W - 8) / Opts.Length)), 0, Opts.Length - 1);
    public int HoverIndex(float px) => IndexAt(px);

    public override void Draw(Ui ui, Canvas c, bool hover, bool pressed)
    {
        float fade = Enabled() ? 1f : 0.35f;
        c.RoundRect(X, Y, W, H, H / 2, Ui.Well.Fade(fade));
        c.RoundRectBorder(X, Y, W, H, H / 2, 1.5f, Ui.WellBorder.Fade(fade));
        int n = Opts.Length, sel = Get();
        float sw = (W - 8) / n, ih = H - 8;
        c.Shadow(X + 4 + sel * sw, Y + 4, sw, ih, ih / 2, 16, Ui.AccA.Alpha(0.35f * fade));
        c.RoundRect(X + 4 + sel * sw, Y + 4, sw, ih, ih / 2, Ui.AccA.Fade(fade), Ui.AccB.Fade(fade), true);
        int hi = hover ? IndexAt(ui.Mx) : -1;
        for (int i = 0; i < n; i++)
        {
            Col col = i == sel ? Ui.White : (i == hi ? Ui.Txt : Ui.Txt2);
            c.Text(Ui.F(24, true), Opts[i], X + 4 + i * sw + sw / 2, Y + H / 2 + 8, col.Fade(fade), 1);
        }
    }

    public override void Up(Ui ui, float px, float py, bool inside)
    {
        if (inside) Set(IndexAt(px));
    }
}

sealed class Toggle : Widget
{
    public Func<bool> Get = () => false;
    public Action<bool> Set = _ => { };

    public override void Draw(Ui ui, Canvas c, bool hover, bool pressed)
    {
        bool on = Get();
        if (on)
        {
            c.Shadow(X, Y, W, H, H / 2, 16, Ui.AccA.Alpha(0.35f));
            c.RoundRect(X, Y, W, H, H / 2, Ui.AccA, Ui.AccB, true);
        }
        else
        {
            c.RoundRect(X, Y, W, H, H / 2, hover ? Ui.Track.Mul(1.25f) : Ui.Track);
        }
        float kx = on ? X + W - 20 : X + 20, ky = Y + H / 2;
        c.Shadow(kx - 16, ky - 12, 32, 32, 16, 10, new Col(0, 0, 0, 0.35f));
        c.Circle(kx, ky, 16, Ui.White);
    }

    public override void Up(Ui ui, float px, float py, bool inside)
    {
        if (inside) Set(!Get());
    }
}

sealed class Slider : Widget
{
    public float Min, Max, Step;
    public Func<float> Get = () => 0;
    public Action<float> Set = _ => { };
    public Func<string> Value = () => "";
    const float Pad = 16;

    void SetFromX(float px)
    {
        float t = Math.Clamp((px - (X + Pad)) / (W - 2 * Pad), 0f, 1f);
        float v = Min + t * (Max - Min);
        v = MathF.Round(v / Step) * Step;
        Set(Math.Clamp(v, Min, Max));
    }

    public override void Down(Ui ui, float px, float py) => SetFromX(px);
    public override void Drag(Ui ui, float px, float py) => SetFromX(px);

    public override void Draw(Ui ui, Canvas c, bool hover, bool pressed)
    {
        float x0 = X + Pad, x1 = X + W - Pad, ty = Y + H / 2;
        float t = Math.Clamp((Get() - Min) / (Max - Min), 0f, 1f);
        float kx = x0 + t * (x1 - x0);
        c.Text(Ui.F(28, true), Value(), X + W, Y - 14, Ui.Txt, 2);
        c.RoundRect(x0, ty - 5, x1 - x0, 10, 5, Ui.Track);
        if (kx - x0 > 1) c.RoundRect(x0, ty - 5, kx - x0, 10, 5, Ui.AccA, Ui.AccB, true);
        bool hot = hover || pressed;
        c.Shadow(kx - 16, ty - 16, 32, 32, 16, hot ? 30 : 18, Ui.AccA.Alpha(hot ? 0.7f : 0.4f));
        c.Circle(kx, ty, hot ? 18 : 16, Ui.White);
        c.Circle(kx, ty, 7, Col.Lerp(Ui.AccA, Ui.AccB, t));
    }
}

sealed class Button : Widget
{
    public string Text = "";
    public bool Primary;
    public Action Click = () => { };

    public override void Draw(Ui ui, Canvas c, bool hover, bool pressed)
    {
        float dy = pressed ? 2 : 0;
        if (Primary)
        {
            float k = pressed ? 0.85f : hover ? 1.12f : 1f;
            c.Shadow(X, Y + 6 + dy, W, H, 20, hover ? 34 : 24, Ui.AccA.Alpha(hover ? 0.6f : 0.42f));
            c.RoundRect(X, Y + dy, W, H, 20, Ui.AccA.Mul(k), Ui.AccB.Mul(k), true);
            c.Text(Ui.F(26, true), Text, X + W / 2, Y + dy + H / 2 + 9, Ui.White, 1);
        }
        else
        {
            var fill = pressed ? Ui.Btn2.Mul(0.85f) : hover ? Ui.Btn2.Mul(1.3f) : Ui.Btn2;
            c.RoundRect(X, Y + dy, W, H, 20, fill);
            c.RoundRectBorder(X, Y + dy, W, H, 20, 1.5f, hover ? Ui.AccA.Alpha(0.9f) : Ui.Btn2Border);
            c.Text(Ui.F(26, true), Text, X + W / 2, Y + dy + H / 2 + 9, Ui.Txt, 1);
        }
    }

    public override void Up(Ui ui, float px, float py, bool inside)
    {
        if (inside) Click();
    }
}
