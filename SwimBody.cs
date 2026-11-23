using System.Numerics;

/// <summary>
/// Физика плавания: тело в вязкой жидкости, руки - вёсла.
/// Всё считается в осях трекинга. X - накопленное смещение игрока (сырые координаты -> "мир").
/// Vel / Omega - скорость и угловая скорость в мировых осях, поэтому инерция сохраняется, даже если ты перевернулся.
/// </summary>
sealed class SwimBody
{
    public Matrix4x4 X = Matrix4x4.Identity;
    public Vector3 Vel, Omega;

    Vector3 hvHead, hvL, hvR; // сглаженные скорости головы и рук (трекинг-пространство)

    const float DeadZone = 0.12f;   // м/с: дрожание рук не считается гребком
    const float BodyOffset = 0.25f; // центр тела ниже головы, м
    const float MaxOmega = 4f;      // предел вращения, рад/с (~230°/с)

    public float Speed => Vel.Length();
    public float Spin => Omega.Length();

    public void Reset() { X = Matrix4x4.Identity; Vel = Vector3.Zero; Omega = Vector3.Zero; }

    /// <summary>Вызывать каждый кадр: сглаживает скорости из позы.</summary>
    public void Smooth(float dt, Vector3 headVel, Vector3 leftVel, Vector3 rightVel)
    {
        float a = 1f - MathF.Exp(-dt / 0.03f);
        hvHead = Vector3.Lerp(hvHead, headVel, a);
        hvL = Vector3.Lerp(hvL, leftVel, a);
        hvR = Vector3.Lerp(hvR, rightVel, a);
    }

    /// <returns>true, если положение изменилось и его надо отправить в SteamVR</returns>
    public bool Step(float dt, Settings s, Matrix4x4 head, bool useL, Matrix4x4 lm, bool useR, Matrix4x4 rm)
    {
        Vector3 center = head.Translation + Vector3.TransformNormal(-Vector3.UnitY, head) * BodyOffset;

        Vector3 acc = Vector3.Zero, tor = Vector3.Zero;
        if (useL) AddHand(hvL, lm, center, s, ref acc, ref tor);
        if (useR) AddHand(hvR, rm, center, s, ref acc, ref tor);

        Vel += (acc - Vel * s.SwimDrag) * dt;
        Omega += (tor * s.SwimTurn * (s.SwimInvertTurn ? -1f : 1f) - Omega * s.SwimAngDrag) * dt;

        // руль взглядом: скорость плавно поворачивает вслед за направлением взгляда
        float sp = Vel.Length();
        if (s.SwimGaze > 0.001f && sp > 0.15f)
        {
            Vector3 fwd = Vector3.Normalize(Vector3.TransformNormal(Vector3.TransformNormal(-Vector3.UnitZ, head), X));
            if (Vector3.Dot(Vel, fwd) < 0) fwd = -fwd; // плывёшь спиной вперёд - остаёшься спиной вперёд
            float k = 1f - MathF.Exp(-s.SwimGaze * 3f * dt);
            Vector3 d = Vector3.Lerp(Vel / sp, fwd, k);
            float dl = d.Length();
            if (dl > 1e-4f) Vel = d / dl * sp;
        }

        sp = Vel.Length();
        if (sp > s.SwimMaxSpeed) { Vel *= s.SwimMaxSpeed / sp; sp = s.SwimMaxSpeed; }
        float om = Omega.Length();
        if (om > MaxOmega) { Omega *= MaxOmega / om; om = MaxOmega; }
        if (sp < 0.002f && om < 0.002f) return false;

        // движение игрока в мировых осях: поворот вокруг центра тела + сдвиг
        Quaternion dq = om > 1e-5f ? Quaternion.CreateFromAxisAngle(Omega / om, om * dt) : Quaternion.Identity;
        Vector3 cv = Vector3.Transform(center, X);
        var step = Matrix4x4.CreateTranslation(-cv) * Matrix4x4.CreateFromQuaternion(dq) * Matrix4x4.CreateTranslation(cv + Vel * dt);
        X = X * step;

        // чтобы матрица не "расползалась" за долгое время
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(X));
        var t = X.Translation;
        X = Matrix4x4.CreateFromQuaternion(q);
        X.Translation = t;
        return true;
    }

    void AddHand(Vector3 hv, Matrix4x4 hm, Vector3 center, Settings s, ref Vector3 acc, ref Vector3 tor)
    {
        Vector3 vrel = hv - hvHead;            // скорость руки относительно тела
        float sp = vrel.Length();
        if (sp <= DeadZone) return;
        Vector3 veff = vrel * ((sp - DeadZone) / sp);
        Vector3 f = Vector3.TransformNormal(-veff * s.SwimStrength, X);                 // тяга: против движения руки
        Vector3 r = Vector3.TransformNormal(hm.Translation - center, X);                // плечо
        acc += f;
        tor += Vector3.Cross(r, f);
    }
}
