using System.IO.Compression;

readonly struct Col
{
    public readonly float R, G, B, A; // 0..1
    public Col(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
    public static Col Hex(uint rgb, float a = 1f) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    public Col Alpha(float a) => new(R, G, B, a);
    public Col Fade(float f) => new(R, G, B, A * f);
    public Col Mul(float f) => new(MathF.Min(1, R * f), MathF.Min(1, G * f), MathF.Min(1, B * f), A);
    public static Col Lerp(Col a, Col b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
}

/// <summary>Мини-растеризатор: скруглённые прямоугольники, круги, свечение, тексты. RGBA, без внешних библиотек.</summary>
sealed class Canvas
{
    public readonly int W, H;
    public readonly byte[] Px;
    public Canvas(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    void Blend(int x, int y, in Col c, float cov)
    {
        float a = c.A * cov;
        if (a <= 0.002f) return;
        int i = (y * W + x) * 4;
        Px[i] = (byte)(Px[i] + (c.R * 255f - Px[i]) * a + 0.5f);
        Px[i + 1] = (byte)(Px[i + 1] + (c.G * 255f - Px[i + 1]) * a + 0.5f);
        Px[i + 2] = (byte)(Px[i + 2] + (c.B * 255f - Px[i + 2]) * a + 0.5f);
        Px[i + 3] = 255;
    }

    public void FillVertical(Col top, Col bottom)
    {
        for (int y = 0; y < H; y++)
        {
            var c = Col.Lerp(top, bottom, y / (float)(H - 1));
            for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; Px[i] = (byte)(c.R * 255); Px[i + 1] = (byte)(c.G * 255); Px[i + 2] = (byte)(c.B * 255); Px[i + 3] = 255; }
        }
    }

    static float SdRoundRect(float px, float py, float cx, float cy, float hx, float hy, float r)
    {
        float dx = MathF.Abs(px - cx) - (hx - r), dy = MathF.Abs(py - cy) - (hy - r);
        float ox = MathF.Max(dx, 0), oy = MathF.Max(dy, 0);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(dx, dy), 0) - r;
    }

    public void RoundRect(float x, float y, float w, float h, float r, Col c0, Col? c1 = null, bool horizontal = false)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(x) - 1), x1 = Math.Min(W - 1, (int)MathF.Ceiling(x + w) + 1);
        int y0 = Math.Max(0, (int)MathF.Floor(y) - 1), y1 = Math.Min(H - 1, (int)MathF.Ceiling(y + h) + 1);
        float cx = x + w / 2, cy = y + h / 2, hx = w / 2, hy = h / 2;
        r = MathF.Min(r, MathF.Min(hx, hy));
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                float d = SdRoundRect(px + 0.5f, py + 0.5f, cx, cy, hx, hy, r);
                float cov = Clamp01(0.5f - d);
                if (cov <= 0) continue;
                Col c = c1.HasValue ? Col.Lerp(c0, c1.Value, Clamp01(horizontal ? (px + 0.5f - x) / w : (py + 0.5f - y) / h)) : c0;
                Blend(px, py, c, cov);
            }
    }

    public void RoundRectBorder(float x, float y, float w, float h, float r, float t, Col c)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(x) - 1), x1 = Math.Min(W - 1, (int)MathF.Ceiling(x + w) + 1);
        int y0 = Math.Max(0, (int)MathF.Floor(y) - 1), y1 = Math.Min(H - 1, (int)MathF.Ceiling(y + h) + 1);
        float cx = x + w / 2, cy = y + h / 2, hx = w / 2, hy = h / 2;
        r = MathF.Min(r, MathF.Min(hx, hy));
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                float d = SdRoundRect(px + 0.5f, py + 0.5f, cx, cy, hx, hy, r);
                float cov = Clamp01(0.5f - d) * Clamp01(d + t + 0.5f);
                if (cov > 0) Blend(px, py, c, cov);
            }
    }

    public void Circle(float cx, float cy, float r, Col c) => RoundRect(cx - r, cy - r, 2 * r, 2 * r, r, c);

    /// <summary>Мягкая тень / свечение вокруг скруглённого прямоугольника.</summary>
    public void Shadow(float x, float y, float w, float h, float r, float blur, Col c)
    {
        int x0 = Math.Max(0, (int)(x - blur) - 1), x1 = Math.Min(W - 1, (int)(x + w + blur) + 1);
        int y0 = Math.Max(0, (int)(y - blur) - 1), y1 = Math.Min(H - 1, (int)(y + h + blur) + 1);
        float cx = x + w / 2, cy = y + h / 2, hx = w / 2, hy = h / 2;
        r = MathF.Min(r, MathF.Min(hx, hy));
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                float d = SdRoundRect(px + 0.5f, py + 0.5f, cx, cy, hx, hy, r);
                float t = Clamp01(1f - (d + blur * 0.35f) / blur);
                float cov = t * t * (3 - 2 * t);
                if (cov > 0) Blend(px, py, c, cov);
            }
    }

    /// <summary>Большое мягкое цветное пятно (для фона).</summary>
    public void Glow(float cx, float cy, float radius, Col c)
    {
        int x0 = Math.Max(0, (int)(cx - radius)), x1 = Math.Min(W - 1, (int)(cx + radius));
        int y0 = Math.Max(0, (int)(cy - radius)), y1 = Math.Min(H - 1, (int)(cy + radius));
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                float dx = px - cx, dy = py - cy;
                float t = 1f - MathF.Sqrt(dx * dx + dy * dy) / radius;
                if (t <= 0) continue;
                Blend(px, py, c, t * t);
            }
    }

    // ----- текст -----
    public float Measure(Face f, string s)
    {
        float w = 0;
        foreach (char ch in s) if (f.G.TryGetValue(ch, out var g)) w += g.Adv;
        return w;
    }

    /// <param name="align">0 - слева, 1 - по центру, 2 - справа (от точки x)</param>
    public void Text(Face f, string s, float x, float baseline, Col c, int align = 0)
    {
        float w = Measure(f, s);
        float pen = align == 1 ? x - w / 2 : align == 2 ? x - w : x;
        int by = (int)MathF.Round(baseline);
        foreach (char ch in s)
        {
            if (!f.G.TryGetValue(ch, out var g)) continue;
            int gx = (int)MathF.Round(pen) + g.OffX, gy = by + g.OffY;
            for (int r = 0; r < g.H; r++)
            {
                int py = gy + r; if (py < 0 || py >= H) continue;
                for (int q = 0; q < g.W; q++)
                {
                    int px = gx + q; if (px < 0 || px >= W) continue;
                    byte a = f.Blob[g.Off + r * g.W + q];
                    if (a != 0) Blend(px, py, c, a / 255f);
                }
            }
            pen += g.Adv;
        }
    }

    // ----- PNG (для --preview) -----
    public void SavePng(string path)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < H; y++) { raw.WriteByte(0); raw.Write(Px, y * W * 4, W * 4); }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw.GetBuffer(), 0, (int)raw.Length);
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, W); WriteBE(ihdr, 4, H); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr); Chunk(fs, "IDAT", z.ToArray()); Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in t) crc = CrcStep(crc, b);
        foreach (var b in data) crc = CrcStep(crc, b);
        crc ^= 0xFFFFFFFF;
        var c = new byte[4]; WriteBE(c, 0, (int)crc); s.Write(c);
    }

    static uint CrcStep(uint crc, byte b)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
