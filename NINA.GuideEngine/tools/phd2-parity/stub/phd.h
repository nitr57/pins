// SPDX-License-Identifier: MPL-2.0
//
// Minimal stand-in for PHD2's phd.h so that PHD2's unmodified star.cpp (and the Median3 part of
// image_math.cpp) compile without wxWidgets. Only what star.cpp touches is provided: a tiny subset of
// wx types, a no-op debug log, a minimal usImage and the pFrame / pCamera configuration hooks, whose
// values the parity CLI sets per request.

#ifndef PHD_PARITY_STUB_H
#define PHD_PARITY_STUB_H

#include <algorithm>
#include <cassert>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <set>
#include <string>
#include <vector>

typedef unsigned char wxByte;
typedef long long wxLongLong_t;

// ---- wxString (only used for logging and as exception type) ----
class wxString : public std::string
{
public:
    wxString() { }
    wxString(const char *s) : std::string(s) { }
    wxString(const std::string& s) : std::string(s) { }
    static wxString Format(const char *fmt, ...)
    {
        char buf[1024];
        va_list ap;
        va_start(ap, fmt);
        vsnprintf(buf, sizeof(buf), fmt, ap);
        va_end(ap);
        return wxString(buf);
    }
};

struct DebugLog
{
    void Write(const wxString&) { }
    void AddLine(const wxString&) { }
};
extern DebugLog Debug;

#define ERROR_INFO(s) (wxString(s))
#define THROW_INFO(s) (wxString(s))
#define POSSIBLY_UNUSED(x) (void) (x)
#define wxMax(a, b) (((a) > (b)) ? (a) : (b))
#define wxMin(a, b) (((a) < (b)) ? (a) : (b))
#define ROUND(x) (int) floor((x) + 0.5)

struct wxLongLongStub
{
    wxLongLong_t v;
    wxLongLong_t GetValue() const { return v; }
};
inline wxLongLongStub wxGetUTCTimeMillis() { return wxLongLongStub{ 0 }; }

struct wxBusyCursor
{
};

// ---- geometry ----
struct wxSize
{
    int x, y;
    wxSize() : x(0), y(0) { }
    wxSize(int w, int h) : x(w), y(h) { }
    int GetWidth() const { return x; }
    int GetHeight() const { return y; }
    int GetX() const { return x; }
    int GetY() const { return y; }
    bool operator==(const wxSize& o) const { return x == o.x && y == o.y; }
};

struct wxPoint
{
    int x, y;
    wxPoint() : x(0), y(0) { }
    wxPoint(int x_, int y_) : x(x_), y(y_) { }
};

struct wxRect
{
    int x, y, width, height;
    wxRect() : x(0), y(0), width(0), height(0) { }
    wxRect(int x_, int y_, int w, int h) : x(x_), y(y_), width(w), height(h) { }
    explicit wxRect(const wxSize& s) : x(0), y(0), width(s.x), height(s.y) { }
    bool IsEmpty() const { return width <= 0 || height <= 0; }
    int GetX() const { return x; }
    int GetY() const { return y; }
    int GetWidth() const { return width; }
    int GetHeight() const { return height; }
    int GetLeft() const { return x; }
    int GetTop() const { return y; }
    int GetRight() const { return x + width - 1; }
    int GetBottom() const { return y + height - 1; }
    // wxRect::Intersect semantics (empty rect when there is no overlap)
    wxRect& Intersect(const wxRect& r)
    {
        int x2 = GetRight(), y2 = GetBottom();
        if (x < r.x) x = r.x;
        if (y < r.y) y = r.y;
        if (x2 > r.GetRight()) x2 = r.GetRight();
        if (y2 > r.GetBottom()) y2 = r.GetBottom();
        width = x2 - x + 1;
        height = y2 - y + 1;
        if (width <= 0 || height <= 0)
        {
            width = 0;
            height = 0;
        }
        return *this;
    }
};

// ---- usImage (subset) ----
class usImage
{
public:
    unsigned short *ImageData;
    wxSize Size;
    wxRect Subframe;
    unsigned int NPixels;
    wxByte BitsPerPixel;
    unsigned short Pedestal;
    unsigned int FrameNum;

    usImage() : ImageData(nullptr), NPixels(0), BitsPerPixel(16), Pedestal(0), FrameNum(0) { }
    ~usImage() { delete[] ImageData; }
    bool Init(const wxSize& size)
    {
        unsigned int prev = NPixels;
        NPixels = size.GetWidth() * size.GetHeight();
        Size = size;
        Subframe = wxRect(0, 0, 0, 0);
        if (NPixels != prev)
        {
            delete[] ImageData;
            ImageData = NPixels ? new unsigned short[NPixels] : nullptr;
        }
        return false;
    }
    bool Init(int w, int h) { return Init(wxSize(w, h)); }
    void SwapImageData(usImage& other) { std::swap(ImageData, other.ImageData); }
    void Clear() { memset(ImageData, 0, NPixels * sizeof(unsigned short)); }
    bool CopyFrom(const usImage& src)
    {
        Init(src.Size);
        memcpy(ImageData, src.ImageData, NPixels * sizeof(unsigned short));
        Subframe = src.Subframe;
        BitsPerPixel = src.BitsPerPixel;
        Pedestal = src.Pedestal;
        FrameNum = src.FrameNum;
        return false;
    }
    unsigned short& Pixel(int x, int y) { return ImageData[y * Size.x + x]; }
    const unsigned short& Pixel(int x, int y) const { return ImageData[y * Size.x + x]; }
};

extern bool Median3(usImage& img);
extern void Median3(unsigned short *dst, const unsigned short *src, const wxSize& size, const wxRect& rect);

// ---- configuration hooks used by star.cpp ----
struct StubGuider
{
    int autoSelDownsample = 0;
    double minStarHFD = 1.5;
    double maxStarHFD = 20.0;
    double afMinStarSNR = 6.0;
    int GetAutoSelDownsample() const { return autoSelDownsample; }
    double GetMinStarHFD() const { return minStarHFD; }
    double GetMaxStarHFD() const { return maxStarHFD; }
    double GetAFMinStarSNR() const { return afMinStarSNR; }
};

struct StubFrame
{
    StubGuider guider;
    StubGuider *pGuider = &guider;
    double pixelScale = 1.0;
    double GetCameraPixelScale() const { return pixelScale; }
};

struct StubCamera
{
    bool saturationByADU = false;
    unsigned short saturationADU = 0;
    bool IsSaturationByADU() const { return saturationByADU; }
    unsigned short GetSaturationADU() const { return saturationByADU ? saturationADU : 0; }
    void SetSaturationByADU(bool byADU, unsigned short adu)
    {
        saturationByADU = byADU;
        saturationADU = adu;
    }
};

extern StubFrame *pFrame;
extern StubCamera *pCamera;

#include "star.h"

#endif
