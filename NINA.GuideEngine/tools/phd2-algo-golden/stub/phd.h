// SPDX-License-Identifier: MPL-2.0
// Minimal stand-ins for the wxWidgets / PHD2 environment so that the algorithmic PHD2 sources
// (extracted verbatim by extract.py) compile and run headless. Only what those sources touch.
#pragma once

#include <algorithm>
#include <cassert>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <limits>
#include <string>
#include <vector>

// PHD2 calls unqualified abs() on doubles (backlash_comp.cpp). In the real build the wx headers pull in
// <math.h>/<stdlib.h>, whose C++ wrappers put the floating-point std::abs overloads into the global
// namespace; without this the int overload would silently truncate.
#include <math.h>
#include <stdlib.h>
using std::abs;

struct wxString : std::string
{
    wxString() { }
    wxString(const char *s) : std::string(s) { }
    wxString(const std::string& s) : std::string(s) { }

    template<typename T> static T fmtArg(const T& t) { return t; }
    static const char *fmtArg(const wxString& s) { return s.c_str(); }
    static const char *fmtArg(const std::string& s) { return s.c_str(); }

    template<typename... A> static wxString Format(const char *fmt, A... a)
    {
        char buf[4096];
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wformat-security"
        snprintf(buf, sizeof buf, fmt, fmtArg(a)...);
#pragma GCC diagnostic pop
        return wxString(buf);
    }
};

#define _(x) x
#define _T(x) x
#define wxT(x) x
#define wxTRANSLATE(x) x
#define ERROR_INFO(x) wxString(x)
#define THROW_INFO(x) wxString(x)
#define POSSIBLY_UNUSED(x) (void) (x)
#define ROUND(x) (int) floor((x) + 0.5)

template<typename T1, typename T2> inline auto wxMax(T1 a, T2 b) -> decltype(a + b) { return a > b ? a : b; }
template<typename T1, typename T2> inline auto wxMin(T1 a, T2 b) -> decltype(a + b) { return a < b ? a : b; }
template<typename T1, typename T2, typename T3> inline T1 wxClip(T1 a, T2 b, T3 c) { return (a < b) ? b : ((a > c) ? c : a); }

typedef std::vector<wxString> wxArrayString;

struct ArrayOfDbl : std::vector<double>
{
    void Add(double d) { push_back(d); }
    void RemoveAt(size_t i) { erase(begin() + i); }
    void Empty() { clear(); }
    size_t GetCount() const { return size(); }
};

struct DebugStub
{
    bool enabled = getenv("GOLDEN_DEBUG") != nullptr;
    void Write(const wxString& s)
    {
        if (enabled)
            fputs(s.c_str(), stderr);
    }
};
extern DebugStub Debug;

struct ProfileStub
{
    double GetDouble(const wxString&, double d) { return d; }
    void SetDouble(const wxString&, double) { }
    bool GetBoolean(const wxString&, bool d) { return d; }
    void SetBoolean(const wxString&, bool) { }
    int GetInt(const wxString&, int d) { return d; }
    void SetInt(const wxString&, int) { }
};
struct ConfigStub
{
    ProfileStub Profile;
};
extern ConfigStub *pConfig;

struct FrameStub
{
    template<typename T> void NotifyGuidingParam(const wxString&, T) { }
};
extern FrameStub *pFrame;

extern long g_currentTime;
inline long wxGetCurrentTime() { return g_currentTime; }

// ---- mount.h subset (verbatim enums) ----
enum GUIDE_DIRECTION
{
    NONE = -1,
    UP = 0,
    NORTH = UP, // Dec + for eq mounts
    DOWN,
    SOUTH = DOWN, // Dec-
    RIGHT,
    EAST = RIGHT, // RA-
    LEFT,
    WEST = LEFT // RA+
};

enum MountMoveOptionBits
{
    MOVEOPT_ALGO_RESULT = (1 << 0), // filter move through guide algorithm
    MOVEOPT_ALGO_DEDUCE = (1 << 1), // use guide algorithm to deduce the move amount (when paused or star lost)
    MOVEOPT_USE_BLC = (1 << 2), // use backlash comp for this move
    MOVEOPT_GRAPH = (1 << 3), // display the move on the graphs
    MOVEOPT_MANUAL = (1 << 4), // manual move - allow even when guiding disabled
};

enum GUIDE_ALGORITHM
{
    GUIDE_ALGORITHM_NONE = -1,
    GUIDE_ALGORITHM_IDENTITY,
    GUIDE_ALGORITHM_HYSTERESIS,
    GUIDE_ALGORITHM_LOWPASS,
    GUIDE_ALGORITHM_LOWPASS2,
    GUIDE_ALGORITHM_RESIST_SWITCH,
    GUIDE_ALGORITHM_GAUSSIAN_PROCESS,
    GUIDE_ALGORITHM_ZFILTER,
};

enum GuideAxis
{
    GUIDE_RA,
    GUIDE_X = GUIDE_RA,
    GUIDE_DEC,
    GUIDE_Y = GUIDE_DEC,
};

class Mount
{
public:
    wxString GetMountClassName() const { return "scope"; }
};

// ---- guide_algorithm.h / .cpp subset (GUI members removed) ----
class GuideAlgorithm
{
protected:
    Mount *m_pMount;
    GuideAxis m_guideAxis;

public:
    GuideAlgorithm(Mount *pMount, GuideAxis axis) : m_pMount(pMount), m_guideAxis(axis) { }
    virtual ~GuideAlgorithm() { }
    virtual GUIDE_ALGORITHM Algorithm() const = 0;
    virtual void reset() = 0;
    virtual double result(double input) = 0;
    virtual double deduceResult() { return 0.0; }
    virtual void GuidingStarted() { }
    virtual void GuidingStopped() { reset(); }
    virtual void GuidingPaused() { }
    virtual void GuidingResumed() { reset(); }
    virtual void GuidingDithered(double amt) { reset(); }
    virtual void GuidingDitherSettleDone(bool success) { }
    virtual void DirectMoveApplied(double amt) { }
    virtual void GuidingEnabled() { reset(); }
    virtual void GuidingDisabled() { }
    virtual wxString GetSettingsSummary() const { return wxString(); }
    virtual void GetParamNames(wxArrayString& names) const { }
    virtual bool GetParam(const wxString& name, double *val) const { return false; }
    virtual bool SetParam(const wxString& name, double val) { return false; }
    virtual double GetMinMove() const { return -1.0; }
    virtual bool SetMinMove(double minMove) { return true; }
    wxString GetConfigPath() const { return "/scope/GuideAlgorithm/X/algo"; }
};

#include "guiding_stats.h"

// ---- Scope stub for backlash_comp.cpp ----
struct CalibrationStub
{
    double yRate;
};

class Scope : public Mount
{
public:
    GuideAlgorithm *yAlgo = nullptr;
    CalibrationStub cal { 0.01 };
    int maxDecDuration = 2500;
    GuideAlgorithm *GetYGuideAlgorithm() const { return yAlgo; }
    const CalibrationStub& MountCal() const { return cal; }
    int GetMaxDecDuration() const { return maxDecDuration; }
    bool SetMaxDecDuration(int d)
    {
        maxDecDuration = d;
        return false;
    }
    const char *DirectionStr(GUIDE_DIRECTION d) const { return "dir"; }
};

// ---- algorithm class declarations (member layout as in the PHD2 headers, GUI panes removed) ----
class GuideAlgorithmIdentity : public GuideAlgorithm
{
public:
    GuideAlgorithmIdentity(Mount *pMount, GuideAxis axis);
    ~GuideAlgorithmIdentity();
    GUIDE_ALGORITHM Algorithm() const override;
    void reset() override;
    double result(double input) override;
};

class GuideAlgorithmHysteresis : public GuideAlgorithm
{
    double m_minMove;
    double m_hysteresis;
    double m_aggression;
    double m_lastMove;

public:
    GuideAlgorithmHysteresis(Mount *pMount, GuideAxis axis);
    ~GuideAlgorithmHysteresis();
    GUIDE_ALGORITHM Algorithm() const override;
    void reset() override;
    double result(double input) override;
    double GetMinMove() const override { return m_minMove; }
    bool SetMinMove(double minMove) override;
    double GetHysteresis() const { return m_hysteresis; }
    bool SetHysteresis(double hysteresis);
    double GetAggression() const { return m_aggression; }
    bool SetAggression(double aggression);
    wxString GetSettingsSummary() const override;
    void GetParamNames(wxArrayString& names) const override;
    bool GetParam(const wxString& name, double *val) const override;
    bool SetParam(const wxString& name, double val) override;
};

class GuideAlgorithmLowpass : public GuideAlgorithm
{
    static const unsigned int HISTORY_SIZE = 10;
    double m_slopeWeight;
    double m_minMove;
    WindowedAxisStats m_axisStats;
    int m_timeBase;

public:
    GuideAlgorithmLowpass(Mount *pMount, GuideAxis axis);
    ~GuideAlgorithmLowpass();
    GUIDE_ALGORITHM Algorithm() const override;
    void reset() override;
    double result(double input) override;
    double GetMinMove() const override { return m_minMove; }
    bool SetMinMove(double minMove) override;
    double GetSlopeWeight() const { return m_slopeWeight; }
    bool SetSlopeWeight(double slopeWeight);
    wxString GetSettingsSummary() const override;
    void GetParamNames(wxArrayString& names) const override;
    bool GetParam(const wxString& name, double *val) const override;
    bool SetParam(const wxString& name, double val) override;
};

class GuideAlgorithmLowpass2 : public GuideAlgorithm
{
    static const int HISTORY_SIZE = 10;
    double m_aggressiveness;
    double m_minMove;
    int m_rejects;
    WindowedAxisStats m_axisStats;
    int m_timeBase;

public:
    GuideAlgorithmLowpass2(Mount *pMount, GuideAxis axis);
    ~GuideAlgorithmLowpass2();
    GUIDE_ALGORITHM Algorithm() const override;
    void reset() override;
    double result(double input) override;
    double GetMinMove() const override { return m_minMove; }
    bool SetMinMove(double minMove) override;
    double GetAggressiveness() const { return m_aggressiveness; }
    bool SetAggressiveness(double aggressiveness);
    wxString GetSettingsSummary() const override;
    void GetParamNames(wxArrayString& names) const override;
    bool GetParam(const wxString& name, double *val) const override;
    bool SetParam(const wxString& name, double val) override;
};

class GuideAlgorithmResistSwitch : public GuideAlgorithm
{
    static const unsigned int HISTORY_SIZE = 10;
    ArrayOfDbl m_history;
    double m_minMove;
    double m_aggression;
    bool m_fastSwitchEnabled;
    int m_currentSide;

public:
    GuideAlgorithmResistSwitch(Mount *pMount, GuideAxis axis);
    ~GuideAlgorithmResistSwitch();
    GUIDE_ALGORITHM Algorithm() const override;
    void reset() override;
    double result(double input) override;
    double GetMinMove() const override { return m_minMove; }
    bool SetMinMove(double minMove) override;
    double GetAggression() const { return m_aggression; }
    bool SetAggression(double aggr);
    bool GetFastSwitchEnabled() const { return m_fastSwitchEnabled; }
    void SetFastSwitchEnabled(bool enable);
    wxString GetSettingsSummary() const override;
    void GetParamNames(wxArrayString& names) const override;
    bool GetParam(const wxString& name, double *val) const override;
    bool SetParam(const wxString& name, double val) override;
};
