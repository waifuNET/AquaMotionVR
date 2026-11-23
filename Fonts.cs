using System.Text;

/// <summary>Готовые растровые глифы (Inter) из fonts.bin. Размеры: 20, 22, 24, 26, 28, 32, 44.</summary>
struct Glyph { public float Adv; public short OffX, OffY; public ushort W, H; public int Off; }

sealed class Face
{
    public int Size, Ascent, Descent;
    public Dictionary<int, Glyph> G = new();
    public byte[] Blob = Array.Empty<byte>();
}

static class Fonts
{
    static readonly Dictionary<(int, bool), Face> faces = new();

    public static bool Load(string path)
    {
        try
        {
            using var br = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RWF1") return false;
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int bold = br.ReadInt32(), size = br.ReadInt32();
                var f = new Face { Size = size, Ascent = br.ReadInt32(), Descent = br.ReadInt32() };
                int gc = br.ReadInt32();
                for (int k = 0; k < gc; k++)
                {
                    int cp = br.ReadInt32();
                    f.G[cp] = new Glyph
                    {
                        Adv = br.ReadSingle(), OffX = br.ReadInt16(), OffY = br.ReadInt16(),
                        W = br.ReadUInt16(), H = br.ReadUInt16(), Off = br.ReadInt32()
                    };
                }
                f.Blob = br.ReadBytes(br.ReadInt32());
                faces[(size, bold != 0)] = f;
            }
            return faces.Count > 0;
        }
        catch { return false; }
    }

    public static Face Get(int size, bool bold = false) => faces[(size, bold)];
}
