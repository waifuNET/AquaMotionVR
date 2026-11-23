#pragma once
// Математика трансформации поз. Без зависимостей от Windows - можно тестировать где угодно.
#include <cmath>
#include <openvr_driver.h>

namespace rw
{
    struct Transform
    {
        bool enabled = false;
        vr::HmdQuaternion_t q{ 1, 0, 0, 0 }; // w, x, y, z
        double t[3] = { 0, 0, 0 };
        bool hmdKeepOrientation = false; // шлем: ориентацию не трогаем, только позицию
        bool hmdZeroVelocity = false;    // шлем: скорости и ускорения = 0
    };

    inline vr::HmdQuaternion_t Mul(const vr::HmdQuaternion_t& a, const vr::HmdQuaternion_t& b)
    {
        return {
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w
        };
    }

    inline double Len2(const vr::HmdQuaternion_t& q) { return q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z; }

    inline vr::HmdQuaternion_t Normalized(const vr::HmdQuaternion_t& q)
    {
        double l = std::sqrt(Len2(q));
        if (l < 1e-9) return { 1, 0, 0, 0 };
        return { q.w / l, q.x / l, q.y / l, q.z / l };
    }

    inline vr::HmdQuaternion_t Conj(const vr::HmdQuaternion_t& q) { return { q.w, -q.x, -q.y, -q.z }; }

    /** v' = q * v * q^-1 (q - единичный) */
    inline void Rotate(const vr::HmdQuaternion_t& q, const double v[3], double out[3])
    {
        // t = 2 * cross(u, v); v' = v + w*t + cross(u, t)
        double tx = 2 * (q.y * v[2] - q.z * v[1]);
        double ty = 2 * (q.z * v[0] - q.x * v[2]);
        double tz = 2 * (q.x * v[1] - q.y * v[0]);
        out[0] = v[0] + q.w * tx + (q.y * tz - q.z * ty);
        out[1] = v[1] + q.w * ty + (q.z * tx - q.x * tz);
        out[2] = v[2] + q.w * tz + (q.x * ty - q.y * tx);
    }

    /**
     * Применяет трансформацию игрока ко всему устройству: pose_world' = X(pose_world).
     * Делается через поля qWorldFromDriverRotation / vecWorldFromDriverTranslation - их SteamVR
     * применяет к позе и к скоростям (так же работают Space Calibrator и Motion Compensation).
     */
    inline void Apply(const Transform& tf, vr::DriverPose_t& pose)
    {
        // некоторые драйверы оставляют этот кватернион нулевым (w = 0) - считаем его единичным
        if (Len2(pose.qWorldFromDriverRotation) < 1e-9) pose.qWorldFromDriverRotation = { 1, 0, 0, 0 };
        pose.qWorldFromDriverRotation = Normalized(Mul(tf.q, pose.qWorldFromDriverRotation));

        double r[3];
        Rotate(tf.q, pose.vecWorldFromDriverTranslation, r);
        for (int i = 0; i < 3; i++) pose.vecWorldFromDriverTranslation[i] = r[i] + tf.t[i];
    }

    /**
     * Шлем в режиме "ровный вид": мировая ориентация остаётся как есть (камера игры не крутится, поэтому
     * реприекция шлема - Oculus, ALVR, Virtual Desktop - не видит разницы с настоящим положением головы и
     * не рисует чёрные края), а позиция проходит через ту же трансформацию X, что и остальные устройства:
     *   мир_поз = Rw*pos + tw;   мир_поз' = Rx*мир_поз + tx;   pos' = Rw^-1 * (мир_поз' - tw)
     * Линейная скорость и ускорение (они в осях драйвера) поворачиваются так же, как позиция.
     */
    inline void ApplyHmdKeepOrientation(const Transform& tf, vr::DriverPose_t& pose)
    {
        if (Len2(pose.qWorldFromDriverRotation) < 1e-9) pose.qWorldFromDriverRotation = { 1, 0, 0, 0 };
        const vr::HmdQuaternion_t rw = Normalized(pose.qWorldFromDriverRotation);
        const vr::HmdQuaternion_t rwInv = Conj(rw);
        pose.qWorldFromDriverRotation = rw;

        double w[3], x[3], d[3];
        Rotate(rw, pose.vecPosition, w);
        for (int i = 0; i < 3; i++) w[i] += pose.vecWorldFromDriverTranslation[i];
        Rotate(tf.q, w, x);
        for (int i = 0; i < 3; i++) d[i] = x[i] + tf.t[i] - pose.vecWorldFromDriverTranslation[i];
        Rotate(rwInv, d, pose.vecPosition);

        auto rotVec = [&](double v[3])
        {
            double a[3], b[3], c[3];
            Rotate(rw, v, a); Rotate(tf.q, a, b); Rotate(rwInv, b, c);
            v[0] = c[0]; v[1] = c[1]; v[2] = c[2];
        };
        rotVec(pose.vecVelocity);
        rotVec(pose.vecAcceleration);
    }

    inline void ZeroMotion(vr::DriverPose_t& pose)
    {
        for (int i = 0; i < 3; i++)
        {
            pose.vecVelocity[i] = 0; pose.vecAcceleration[i] = 0;
            pose.vecAngularVelocity[i] = 0; pose.vecAngularAcceleration[i] = 0;
        }
    }

    /** Применяет трансформацию к позе устройства с учётом настроек вида для шлема (устройство 0). */
    inline void ApplyToDevice(const Transform& tf, vr::DriverPose_t& pose, bool isHmd)
    {
        if (isHmd && tf.hmdKeepOrientation) ApplyHmdKeepOrientation(tf, pose);
        else Apply(tf, pose);
        if (isHmd && tf.hmdZeroVelocity) ZeroMotion(pose);
    }
}
