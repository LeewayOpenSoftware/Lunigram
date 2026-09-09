#pragma once

#include "RichMathSurface.g.h"

#include "latex.h"
#include "graphic_dwrite.h"

#include <wincodec.h>

namespace winrt::Telegram::Native::implementation
{
    struct RichMathSurface : RichMathSurfaceT<RichMathSurface>
    {
        RichMathSurface(hstring formula);

        int32_t PixelWidth();
        int32_t PixelHeight();
        float Baseline();

        void RenderSync(winrt::Windows::Storage::Streams::IBuffer buffer, double rasterizationScale, winrt::Windows::UI::Color foreground);

    private:
        static winrt::com_ptr<IWICImagingFactory2> m_wicFactory;

        static std::once_flag m_init;
        static void Init();

        tex::TeXRender* m_render;
        int32_t m_pixelWidth;
        int32_t m_pixelHeight;
        float m_baseline;
    };
}

namespace winrt::Telegram::Native::factory_implementation
{
    struct RichMathSurface : RichMathSurfaceT<RichMathSurface, implementation::RichMathSurface>
    {
    };
}
