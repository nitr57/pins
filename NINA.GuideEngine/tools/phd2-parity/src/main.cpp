// SPDX-License-Identifier: MPL-2.0
//
// phd2-parity: runs PHD2's own Star::Find / GuideStar::AutoFind (compiled from the unmodified
// star.cpp) on raw 16-bit frames and prints one JSON object per request.
//
// Usage: phd2-parity <jobfile>      (or "-" for stdin)
//
// Job file, one command per line (blank lines and '#' comments ignored):
//   image <path.raw> <width> <height> <bpp> <pedestal> [<sfx> <sfy> <sfw> <sfh>]
//       loads a little-endian uint16 row-major frame; optional subframe
//   find <searchRegion> <x> <y> <mode:0=centroid,1=peak> <minHFD> <maxHFD> <maxADU>
//   autofind <edge> <searchRegion> <roiX> <roiY> <roiW> <roiH> <maxStars> <downsample> <pixelScale>
//            <minHFD> <maxHFD> <minSNR> <satByADU:0|1> <satADU>

#include "phd.h"

#include <fstream>
#include <iostream>
#include <sstream>

DebugLog Debug;
static StubFrame s_frame;
static StubCamera s_camera;
StubFrame *pFrame = &s_frame;
StubCamera *pCamera = &s_camera;

// JSON number, or a string for non-finite values ("NaN", "Infinity", "-Infinity")
static std::string Num(double v)
{
    if (std::isnan(v))
        return "\"NaN\"";
    if (std::isinf(v))
        return v > 0 ? "\"Infinity\"" : "\"-Infinity\"";
    char buf[64];
    snprintf(buf, sizeof(buf), "%.17g", v);
    return buf;
}

static void PrintStar(std::ostream& os, const Star& s)
{
    os << "\"result\":" << (int) s.GetError() << ",\"x\":" << Num(s.X) << ",\"y\":" << Num(s.Y) << ",\"mass\":" << Num(s.Mass)
       << ",\"snr\":" << Num(s.SNR) << ",\"hfd\":" << Num(s.HFD) << ",\"peak\":" << (unsigned) s.PeakVal;
}

int main(int argc, char **argv)
{
    if (argc < 2)
    {
        std::cerr << "usage: phd2-parity <jobfile|->\n";
        return 2;
    }

    std::ifstream file;
    std::istream *in = &std::cin;
    if (std::string(argv[1]) != "-")
    {
        file.open(argv[1]);
        if (!file)
        {
            std::cerr << "cannot open " << argv[1] << "\n";
            return 2;
        }
        in = &file;
    }

    usImage img;
    std::string line;
    int lineNo = 0;
    while (std::getline(*in, line))
    {
        ++lineNo;
        if (line.empty() || line[0] == '#')
            continue;
        std::istringstream ls(line);
        std::string cmd;
        ls >> cmd;
        if (cmd == "image")
        {
            std::string path;
            int w, h, bpp, ped;
            ls >> path >> w >> h >> bpp >> ped;
            img.Init(w, h);
            std::ifstream raw(path, std::ios::binary);
            raw.read(reinterpret_cast<char *>(img.ImageData), (std::streamsize) img.NPixels * 2);
            if (!raw)
            {
                std::cerr << "line " << lineNo << ": cannot read " << path << "\n";
                return 1;
            }
            img.BitsPerPixel = (wxByte) bpp;
            img.Pedestal = (unsigned short) ped;
            int sx, sy, sw, sh;
            if (ls >> sx >> sy >> sw >> sh)
                img.Subframe = wxRect(sx, sy, sw, sh);
            else
                img.Subframe = wxRect(0, 0, 0, 0);
            std::cout << "{\"cmd\":\"image\",\"ok\":true}\n";
        }
        else if (cmd == "find")
        {
            int sr, x, y, mode;
            double minHfd, maxHfd;
            unsigned int maxAdu;
            ls >> sr >> x >> y >> mode >> minHfd >> maxHfd >> maxAdu;
            Star s;
            bool found = s.Find(&img, sr, x, y, mode == 1 ? Star::FIND_PEAK : Star::FIND_CENTROID, minHfd, maxHfd,
                                (unsigned short) maxAdu, Star::FIND_LOGGING_MINIMAL);
            std::cout << "{\"cmd\":\"find\",\"found\":" << (found ? "true" : "false") << ",";
            PrintStar(std::cout, s);
            std::cout << "}\n";
        }
        else if (cmd == "autofind")
        {
            int edge, sr, rx, ry, rw, rh, maxStars, ds, satByAdu;
            double scale, minHfd, maxHfd, minSnr;
            unsigned int satAdu;
            ls >> edge >> sr >> rx >> ry >> rw >> rh >> maxStars >> ds >> scale >> minHfd >> maxHfd >> minSnr >> satByAdu >> satAdu;
            s_frame.guider.autoSelDownsample = ds;
            s_frame.guider.minStarHFD = minHfd;
            s_frame.guider.maxStarHFD = maxHfd;
            s_frame.guider.afMinStarSNR = minSnr;
            s_frame.pixelScale = scale;
            s_camera.SetSaturationByADU(satByAdu != 0, (unsigned short) satAdu);

            GuideStar gs;
            std::vector<GuideStar> stars;
            bool found = gs.AutoFind(img, edge, sr, wxRect(rx, ry, rw, rh), stars, maxStars);
            std::cout << "{\"cmd\":\"autofind\",\"found\":" << (found ? "true" : "false") << ",\"x\":" << Num(gs.X) << ",\"y\":" << Num(gs.Y)
                      << ",\"stars\":[";
            for (size_t i = 0; i < stars.size(); i++)
            {
                if (i)
                    std::cout << ",";
                std::cout << "{";
                PrintStar(std::cout, stars[i]);
                std::cout << ",\"ofsx\":" << Num(stars[i].offsetFromPrimary.X) << ",\"ofsy\":" << Num(stars[i].offsetFromPrimary.Y) << "}";
            }
            std::cout << "]}\n";
        }
        else
        {
            std::cerr << "line " << lineNo << ": unknown command " << cmd << "\n";
            return 1;
        }
    }

    return 0;
}
