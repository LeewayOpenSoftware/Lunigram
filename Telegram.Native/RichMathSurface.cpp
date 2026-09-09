#include "pch.h"
#include "RichMathSurface.h"
#if __has_include("RichMathSurface.g.cpp")
#include "RichMathSurface.g.cpp"
#endif

namespace winrt::Telegram::Native::implementation
{
    winrt::com_ptr<IWICImagingFactory2> RichMathSurface::m_wicFactory = nullptr;
    std::once_flag RichMathSurface::m_init;

    RichMathSurface::RichMathSurface(hstring formula)
    {
        tex::LaTeX::initBundled();
        m_render = tex::LaTeX::parse(
            formula.c_str(),
            600 - 0 * 2,
            18,
            18 * 0.25f,
            0xff424242);

        m_pixelWidth = m_render->getWidth();
        m_pixelHeight = m_render->getHeight();
        m_baseline = m_render->getBaseline();
    }

    void RichMathSurface::Init()
    {
        std::call_once(m_init, [] {
            CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&m_wicFactory));
            });
    }

    int32_t RichMathSurface::PixelWidth()
    {
        return m_pixelWidth;
    }

    int32_t RichMathSurface::PixelHeight()
    {
        return m_pixelHeight;
    }

    float RichMathSurface::Baseline()
    {
        return m_baseline;
    }

    void RichMathSurface::RenderSync(winrt::Windows::Storage::Streams::IBuffer buffer, double rasterizationScale, winrt::Windows::UI::Color foreground)
    {
        Init();

        int width = m_pixelWidth * rasterizationScale;
        int height = m_pixelHeight * rasterizationScale;

        winrt::com_ptr<IWICBitmap> wicBitmap;
        m_wicFactory->CreateBitmap(width, height, GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad, wicBitmap.put());

        wicBitmap->SetResolution(96.f /** rasterizationScale*/, 96.f /** rasterizationScale*/);

        UINT test;
        UINT test2;
        wicBitmap->GetSize(&test, &test2);

        D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
            96.f * rasterizationScale, 96.f * rasterizationScale);

        winrt::com_ptr<ID2D1RenderTarget> renderTarget;
        tex::DWriteEnv::d2d()->CreateWicBitmapRenderTarget(wicBitmap.get(), props, renderTarget.put());

        renderTarget->BeginDraw();
        renderTarget->Clear(D2D1::ColorF(D2D1::ColorF::White, 0));

        tex::Graphics2D_dwrite g2(renderTarget.get());
        m_render->setForeground((foreground.A << 24) | (foreground.R << 16) | (foreground.G << 8) | foreground.B);
        m_render->draw(g2, 0, 0);

        renderTarget->EndDraw();

        const UINT stride = width * 4;
        const UINT size = stride * height;
        wicBitmap->CopyPixels(nullptr, stride, size, buffer.data());
    }
}
