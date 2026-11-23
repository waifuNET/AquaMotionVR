using System.Runtime.InteropServices;
using Valve.VR;

/// <summary>
/// Кадр панели через текстуру D3D11 (SetOverlayTexture) вместо SetOverlayRaw - без мигания.
///
/// SetOverlayRaw грузит картинку в SteamVR асинхронно: пока грузится новый кадр, панель на мгновение
/// пропадает (мигание при каждой смене подсветки), а при частых кадрах загрузчик SteamVR может вообще
/// заклинить до перезапуска SteamVR. С текстурой SteamVR сразу берёт готовую картинку из видеопамяти.
///
/// Рецепт перенесён из HornyLink (tools/hl-vr-overlay/d3d.go, режим "d3d2", проверен на Quest 3 + VD):
/// ОДНА постоянная текстура; UpdateSubresource -> Flush -> ждём GPU (query EVENT) -> SetOverlayTexture ->
/// Flush ещё раз (SteamVR копирует текстуру командой в НАШ контекст; без второго Flush в шлеме был прошлый кадр).
/// Если D3D11 не поднялся - false, вызывающий остаётся на SetOverlayRaw.
/// </summary>
static unsafe class OverlayTexture
{
    const uint D3D_DRIVER_TYPE_HARDWARE = 1;
    const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    const uint D3D11_SDK_VERSION = 7;
    const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28; // Canvas.Px - RGBA, как и для SetOverlayRaw
    const uint D3D11_BIND_SHADER_RESOURCE = 0x8;

    // индексы виртуальных функций
    const int VtRelease = 2;
    const int VtDevCreateQuery = 24, VtDevCreateTexture2D = 5;
    const int VtCtxEnd = 28, VtCtxGetData = 29, VtCtxUpdateSubresource = 48, VtCtxFlush = 111;

    [StructLayout(LayoutKind.Sequential)]
    struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint numLevels, uint sdkVersion, out IntPtr device, out uint level, out IntPtr context);

    static IntPtr device, ctx, tex, query;
    static int texW, texH;
    static bool failed;

    public static bool Available => !failed;

    static void* Fn(IntPtr obj, int idx) => (*(void***)obj)[idx];

    static bool Init()
    {
        if (device != IntPtr.Zero) return true;
        if (failed) return false;
        try
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out _, out ctx);
            if (hr < 0 || device == IntPtr.Zero || ctx == IntPtr.Zero) return Fail($"D3D11CreateDevice 0x{hr:X8}");

            uint* qdesc = stackalloc uint[2]; qdesc[0] = 0; qdesc[1] = 0; // D3D11_QUERY_EVENT
            IntPtr q;
            hr = ((delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)Fn(device, VtDevCreateQuery))(device, qdesc, &q);
            query = hr >= 0 ? q : IntPtr.Zero;
            Console.WriteLine("Панель: кадры через текстуру D3D11 (без мигания).");
            return true;
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    static bool Fail(string why)
    {
        failed = true;
        Console.WriteLine("Панель: текстура D3D11 недоступна (" + why + ") - остаюсь на SetOverlayRaw (возможно мигание).");
        return false;
    }

    /// <summary>Показать кадр RGBA. false - не вышло, пусть идёт через SetOverlayRaw.</summary>
    public static bool Show(ulong overlay, byte[] rgba, int w, int h)
    {
        if (!Init()) return false;
        try
        {
            if (tex == IntPtr.Zero || texW != w || texH != h)
            {
                if (tex != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, uint>)Fn(tex, VtRelease))(tex); tex = IntPtr.Zero; }
                var desc = new Texture2DDesc
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleCount = 1, BindFlags = D3D11_BIND_SHADER_RESOURCE,
                };
                IntPtr t;
                int hr = ((delegate* unmanaged<IntPtr, Texture2DDesc*, void*, IntPtr*, int>)Fn(device, VtDevCreateTexture2D))(device, &desc, null, &t);
                if (hr < 0 || t == IntPtr.Zero) return Fail($"CreateTexture2D 0x{hr:X8}");
                tex = t; texW = w; texH = h;
            }

            fixed (byte* p = rgba)
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Fn(ctx, VtCtxUpdateSubresource))
                    (ctx, tex, 0, null, p, (uint)(w * 4), (uint)(w * h * 4));
            Flush();
            GpuWait();

            var vt = new Texture_t { handle = tex, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
            var err = OpenVR.Overlay.SetOverlayTexture(overlay, ref vt);
            if (err != EVROverlayError.None) return Fail("SetOverlayTexture " + err);
            Flush(); // SteamVR копирует текстуру командой в наш контекст - без этого в шлеме прошлый кадр
            return true;
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    static void Flush() => ((delegate* unmanaged<IntPtr, void>)Fn(ctx, VtCtxFlush))(ctx);

    /// <summary>Ждём, пока видеокарта реально зальёт пиксели (иначе SteamVR мог скопировать старое).</summary>
    static void GpuWait()
    {
        if (query == IntPtr.Zero) return;
        ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, VtCtxEnd))(ctx, query);
        int done;
        var getData = (delegate* unmanaged<IntPtr, IntPtr, void*, uint, uint, int>)Fn(ctx, VtCtxGetData);
        for (int i = 0; i < 200; i++) // до ~100-200 мс
        {
            if (getData(ctx, query, &done, 4, 0) == 0) return; // S_OK - готово
            Thread.Sleep(0);
            Thread.SpinWait(2000);
        }
    }
}
