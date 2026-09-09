#pragma once

#include "config.h"

#include "common.h"
#include "graphic/graphic.h"

#include <d2d1.h>
#include <dwrite.h>
#include <winrt/base.h>

#include <mutex>

using namespace std;
using namespace tex;

namespace tex
{

    /**
     * Process-wide DirectWrite/Direct2D singletons.
     *
     * Thread safety: all methods are safe to call from any thread. The returned
     * IDWriteFactory and ID2D1Factory are themselves free-threaded (DWrite's
     * shared factory and a D2D multi-threaded factory respectively).
     *
     * If the host application already owns factories, call setFactories() once at
     * startup before any Font_dwrite / Graphics2D_dwrite is constructed.
     */
    class DWriteEnv
    {
    public:
        static IDWriteFactory* dwrite();
        static ID2D1Factory* d2d();

        // Inject existing factories. Must be called before first use of dwrite()/d2d().
        // The D2D factory should be multi-threaded if you want concurrent rendering.
        static void setFactories(IDWriteFactory* dw, ID2D1Factory* d2);

    private:
        static void ensureInitialized();

        static std::once_flag _initFlag;
        static winrt::com_ptr<IDWriteFactory> _dwrite;
        static winrt::com_ptr<ID2D1Factory> _d2d;
    };

    /**************************************************************************************************/

    /**
     * Immutable after construction. Safe to share across threads.
     */
    class Font_dwrite : public Font
    {
    private:
        float _size;
        wstring _familyName;
        DWRITE_FONT_WEIGHT _weight;
        DWRITE_FONT_STYLE _slant;

        // Owns the family for private-file fonts. Immutable after ctor.
        winrt::com_ptr<IDWriteFontCollection> _privateCollection;

        Font_dwrite();

    public:
        int _style;  // PLAIN/BOLD/ITALIC/BOLDITALIC, kept for parity with caller code
        winrt::com_ptr<IDWriteTextFormat> _textFormat;
        winrt::com_ptr<IDWriteFontFace> _fontFace;  // for metrics

        Font_dwrite(const string& name, int style, float size);
        Font_dwrite(const string& file, float size);

        virtual float getSize() const override;
        virtual sptr<Font> deriveFont(int style) const override;
        virtual bool operator==(const Font& f) const override;
        virtual bool operator!=(const Font& f) const override;
        virtual ~Font_dwrite();

        // Ascent in DIPs at the current size. Thread-safe (reads immutable face metrics).
        float ascent() const;

        static void convertStyle(int style, DWRITE_FONT_WEIGHT& w, DWRITE_FONT_STYLE& s);
        static int packStyle(DWRITE_FONT_WEIGHT w, DWRITE_FONT_STYLE s);

    private:
        void resolveSystemFontFace();
    };

    /**************************************************************************************************/

    /**
     * Immutable after construction. The underlying IDWriteTextLayout is built once
     * in the ctor and only read via GetMetrics() / DrawTextLayout afterwards, which
     * DirectWrite documents as safe for concurrent readers.
     */
    class TextLayout_dwrite : public TextLayout
    {
    private:
        sptr<Font_dwrite> _font;
        wstring _txt;
        winrt::com_ptr<IDWriteTextLayout> _layout;

    public:
        TextLayout_dwrite(const wstring& src, const sptr<Font_dwrite>& font);

        virtual void getBounds(Rect& bounds) override;
        virtual void draw(Graphics2D& g2, float x, float y) override;

        IDWriteTextLayout* raw() const { return _layout.get(); }
    };

    /**************************************************************************************************/

    /**
     * NOT thread-safe by itself. Each Graphics2D_dwrite instance must be used from
     * a single thread at a time (the same contract as ID2D1RenderTarget). Multiple
     * instances on different threads are fine as long as the D2D factory is
     * multi-threaded (which DWriteEnv ensures by default).
     */
    class Graphics2D_dwrite : public Graphics2D
    {
    private:
        static const Font* defaultFont();

        color _color;
        const Font* _font;
        Stroke _stroke;

        // Borrowed; not released by us.
        ID2D1RenderTarget* _rt;

        winrt::com_ptr<ID2D1SolidColorBrush> _brush;
        winrt::com_ptr<ID2D1StrokeStyle> _strokeStyle;

        D2D1::Matrix3x2F _xform;
        float _sx, _sy;

        void rebuildStrokeStyle();
        void applyTransform();

    public:
        explicit Graphics2D_dwrite(ID2D1RenderTarget* rt);
        ~Graphics2D_dwrite();

        virtual void setColor(color c) override;
        virtual color getColor() const override;

        virtual void setStroke(const Stroke& s) override;
        virtual const Stroke& getStroke() const override;
        virtual void setStrokeWidth(float w) override;

        virtual const Font* getFont() const override;
        virtual void setFont(const Font* font) override;

        virtual void translate(float dx, float dy) override;
        virtual void scale(float sx, float sy) override;
        virtual void rotate(float angle) override;
        virtual void rotate(float angle, float px, float py) override;
        virtual void reset() override;

        virtual float sx() const override;
        virtual float sy() const override;

        virtual void drawChar(wchar_t c, float x, float y) override;
        virtual void drawText(const wstring& c, float x, float y) override;
        virtual void drawLine(float x1, float y1, float x2, float y2) override;
        virtual void drawRect(float x, float y, float w, float h) override;
        virtual void fillRect(float x, float y, float w, float h) override;
        virtual void drawRoundRect(float x, float y, float w, float h, float rx, float ry) override;
        virtual void fillRoundRect(float x, float y, float w, float h, float rx, float ry) override;

        ID2D1RenderTarget* rt() const { return _rt; }
        ID2D1SolidColorBrush* brush() const { return _brush.get(); }
    };

}  // namespace tex
