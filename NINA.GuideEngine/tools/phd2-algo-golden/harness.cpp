// SPDX-License-Identifier: MPL-2.0
// Runs the verbatim PHD2 algorithm sources on deterministic input sequences and writes the golden
// results as JSON (stdout). See run.sh.

#include "phd.h"
#include "backlash_comp_decl.h"

#include <cstdint>
#include <functional>
#include <memory>
#include <sstream>

DebugStub Debug;
static ConfigStub s_config;
ConfigStub *pConfig = &s_config;
static FrameStub s_frame;
FrameStub *pFrame = &s_frame;
long g_currentTime = 1000000;

// ---------------------------------------------------------------- deterministic random numbers
struct Rng
{
    uint64_t s;
    explicit Rng(uint64_t seed) : s(seed) { }
    double uniform()
    {
        s = s * 6364136223846793005ULL + 1442695040888963407ULL;
        return (double) (s >> 11) / 9007199254740992.0;
    }
    double gauss()
    {
        double u1 = uniform(), u2 = uniform();
        if (u1 < 1e-300)
            u1 = 1e-300;
        return sqrt(-2.0 * log(u1)) * cos(2.0 * M_PI * u2);
    }
};

// ---------------------------------------------------------------- minimal JSON writer
static std::string num(double v)
{
    if (std::isnan(v))
        return "\"NaN\"";
    if (std::isinf(v))
        return v > 0 ? "\"Infinity\"" : "\"-Infinity\"";
    char buf[64];
    snprintf(buf, sizeof buf, "%.17g", v);
    return buf;
}

struct Obj
{
    std::vector<std::string> parts;
    Obj& add(const std::string& k, const std::string& raw)
    {
        parts.push_back("\"" + k + "\":" + raw);
        return *this;
    }
    Obj& d(const std::string& k, double v) { return add(k, num(v)); }
    Obj& i(const std::string& k, long v) { return add(k, std::to_string(v)); }
    Obj& b(const std::string& k, bool v) { return add(k, v ? "true" : "false"); }
    Obj& s(const std::string& k, const std::string& v) { return add(k, "\"" + v + "\""); }
    std::string str() const
    {
        std::string r = "{";
        for (size_t n = 0; n < parts.size(); n++)
            r += (n ? "," : "") + parts[n];
        return r + "}";
    }
};

static std::string arr(const std::vector<std::string>& items, const char *sep = ",\n")
{
    std::string r = "[";
    for (size_t n = 0; n < items.size(); n++)
        r += (n ? sep : "") + items[n];
    return r + "]";
}

// ---------------------------------------------------------------- algorithm cases
static Mount s_mount;

static GuideAlgorithm *make(const std::string& kind)
{
    if (kind == "Identity")
        return new GuideAlgorithmIdentity(&s_mount, GUIDE_RA);
    if (kind == "Hysteresis")
        return new GuideAlgorithmHysteresis(&s_mount, GUIDE_RA);
    if (kind == "Lowpass")
        return new GuideAlgorithmLowpass(&s_mount, GUIDE_RA);
    if (kind == "Lowpass2")
        return new GuideAlgorithmLowpass2(&s_mount, GUIDE_RA);
    return new GuideAlgorithmResistSwitch(&s_mount, GUIDE_DEC);
}

struct Param
{
    std::string name;
    double value;
};

// Signal model for one case: drift + periodic error + seeing + occasional outliers, closed loop.
struct Signal
{
    double drift, perAmp, perPeriod, seeing, outlierProb, outlierAmp, gain;
};

static std::string runAlgoCase(const std::string& caseName, const std::string& kind, const std::vector<Param>& params,
                               const Signal& sig, int steps, uint64_t seed)
{
    std::unique_ptr<GuideAlgorithm> algo(make(kind));
    std::vector<std::string> ops;
    for (auto& p : params)
    {
        bool ok = algo->SetParam(p.name, p.value);
        ops.push_back(Obj().s("op", "setParam").s("name", p.name).d("value", p.value).b("ok", ok).str());
    }

    Rng rng(seed);
    double truePos = 0, corrSum = 0;
    for (int n = 0; n < steps; n++)
    {
        if (n == steps / 3)
        {
            algo->GuidingDithered(1.5);
            ops.push_back(Obj().s("op", "dithered").d("amount", 1.5).str());
        }
        if (n == steps / 2)
        {
            algo->GuidingResumed();
            ops.push_back(Obj().s("op", "resumed").str());
        }
        if (n == (2 * steps) / 3)
        {
            algo->GuidingDitherSettleDone(true);
            ops.push_back(Obj().s("op", "settleDone").b("success", true).str());
            // exercise an invalid parameter value and read back the result
            wxArrayString names;
            algo->GetParamNames(names);
            for (auto& nm : names)
            {
                double bad = nm == "fastSwitch" ? 0.0 : -1.0;
                bool ok = algo->SetParam(nm, bad);
                double back = 0;
                algo->GetParam(nm, &back);
                ops.push_back(Obj().s("op", "setParam").s("name", nm).d("value", bad).b("ok", ok).d("readBack", back).str());
            }
            // restore original params
            for (auto& p : params)
            {
                bool ok = algo->SetParam(p.name, p.value);
                ops.push_back(Obj().s("op", "setParam").s("name", p.name).d("value", p.value).b("ok", ok).str());
            }
        }
        truePos += sig.drift + sig.perAmp * (sin(2 * M_PI * (n + 1) / sig.perPeriod) - sin(2 * M_PI * n / sig.perPeriod));
        double seeing = sig.seeing * rng.gauss();
        double outlier = rng.uniform() < sig.outlierProb ? sig.outlierAmp * (rng.uniform() < 0.5 ? -1 : 1) : 0.0;
        double input = truePos - corrSum + seeing + outlier;
        double out = algo->result(input);
        corrSum += sig.gain * out;
        ops.push_back(Obj().s("op", "result").d("in", input).d("out", out).str());
    }

    wxArrayString names;
    algo->GetParamNames(names);
    std::vector<std::string> finals;
    for (auto& nm : names)
    {
        double v = 0;
        algo->GetParam(nm, &v);
        finals.push_back(Obj().s("name", nm).d("value", v).str());
    }

    return Obj()
        .s("case", caseName)
        .s("kind", kind)
        .add("finalParams", arr(finals, ","))
        .add("ops", arr(ops))
        .str();
}

static std::string algorithmCases()
{
    std::vector<std::string> cases;
    Signal typical { 0.02, 1.2, 60, 0.25, 0.02, 2.0, 1.0 };
    Signal quiet { 0.005, 0.2, 90, 0.08, 0.0, 0.0, 1.0 };
    Signal wild { -0.05, 3.0, 25, 0.6, 0.08, 4.0, 0.9 };
    Signal osc { 0.0, 0.0, 10, 0.3, 0.0, 0.0, 1.8 }; // over-corrected loop oscillates
    std::vector<std::pair<std::string, Signal>> signals = { { "typical", typical }, { "quiet", quiet }, { "wild", wild }, { "osc", osc } };

    struct Cfg
    {
        std::string kind;
        std::string label;
        std::vector<Param> params;
    };
    std::vector<Cfg> cfgs = {
        { "Identity", "default", {} },
        { "Hysteresis", "default", {} },
        { "Hysteresis", "tuned", { { "minMove", 0.15 }, { "hysteresis", 0.3 }, { "aggression", 1.0 } } },
        { "Hysteresis", "clip", { { "hysteresis", 1.5 }, { "aggression", 2.5 } } },
        { "Lowpass", "default", {} },
        { "Lowpass", "slope2", { { "minMove", 0.1 }, { "slopeWeight", 2.0 } } },
        { "Lowpass2", "default", {} },
        { "Lowpass2", "aggr100", { { "aggressiveness", 100.0 }, { "minMove", 0.1 } } },
        { "Lowpass2", "aggr120", { { "aggressiveness", 120.0 }, { "minMove", 0.3 } } },
        { "ResistSwitch", "default", {} },
        { "ResistSwitch", "noFast", { { "fastSwitch", 0.0 } } },
        { "ResistSwitch", "tuned", { { "minMove", 0.3 }, { "aggression", 0.5 } } },
    };

    uint64_t seed = 12345;
    for (auto& c : cfgs)
        for (auto& s : signals)
            cases.push_back(runAlgoCase(c.kind + "/" + c.label + "/" + s.first, c.kind, c.params, s.second, 300, seed++));
    return arr(cases);
}

// ---------------------------------------------------------------- backlash comp cases
struct GuideAlgoMinMove : public GuideAlgorithmIdentity
{
    double mm;
    GuideAlgoMinMove(double m) : GuideAlgorithmIdentity(&s_mount, GUIDE_DEC), mm(m) { }
    double GetMinMove() const override { return mm; }
};

struct BlcScenario
{
    std::string name;
    int pulse, floor, ceiling;
    int gearBacklashMs;
    double yRate, minMove, seeing, wander, wanderPeriod;
    int steps;
    uint64_t seed;
};

static std::string runBlc(const BlcScenario& sc)
{
    Scope scope;
    GuideAlgoMinMove yalgo(sc.minMove);
    scope.yAlgo = &yalgo;
    scope.cal.yRate = sc.yRate;
    BacklashComp blc(&scope);
    blc.SetBacklashPulseWidth(sc.pulse, sc.floor, sc.ceiling);
    blc.EnableBacklashComp(true);

    std::vector<std::string> ops;
    auto state = [&](Obj& o) -> Obj& {
        int pw, fl, ce;
        blc.GetBacklashCompSettings(&pw, &fl, &ce);
        return o.i("pulse", pw).i("floor", fl).i("ceiling", ce);
    };
    {
        Obj o;
        ops.push_back(state(o.s("op", "init")).str());
    }

    Rng rng(sc.seed);
    double star = 0.0; // dec offset in px (positive -> needs SOUTH)
    double play = sc.gearBacklashMs / 2.0; // gear play position 0..G
    for (int n = 0; n < sc.steps; n++)
    {
        g_currentTime += 2;
        star += sc.wander * (sin(2 * M_PI * (n + 1) / sc.wanderPeriod) - sin(2 * M_PI * n / sc.wanderPeriod));
        double yRaw = star + sc.seeing * rng.gauss();

        unsigned int opts = MOVEOPT_ALGO_RESULT | MOVEOPT_USE_BLC | MOVEOPT_GRAPH;
        std::string kind = "guide";
        if (n % 53 == 52)
        {
            opts = MOVEOPT_USE_BLC; // recovery move
            kind = "recovery";
        }
        if (n == sc.steps / 2)
        {
            opts = 0; // calibration-type move
            kind = "calibration";
        }

        blc.TrackBLCResults(opts, yRaw);
        {
            Obj o;
            ops.push_back(state(o.s("op", "track").i("opts", opts).d("y", yRaw)).str());
        }

        double y = yRaw;
        if (opts & MOVEOPT_ALGO_RESULT)
            y = fabs(yRaw) >= sc.minMove ? yRaw : 0.0; // stand-in for the Dec algorithm
        int req = ROUND(fabs(y / sc.yRate));
        int before = req;
        blc.ApplyBacklashComp(opts, y, &req);
        {
            Obj o;
            ops.push_back(state(o.s("op", "apply").i("opts", opts).d("y", y).i("req", before).i("outReq", req)).str());
        }

        // simulate the Dec gear with play
        if (req > 0)
        {
            double eff;
            if (y > 0) // SOUTH moves play toward G, star offset decreases
            {
                double slack = sc.gearBacklashMs - play;
                eff = std::max(0.0, req - slack);
                play = std::min((double) sc.gearBacklashMs, play + req);
                star -= eff * sc.yRate;
            }
            else
            {
                double slack = play;
                eff = std::max(0.0, req - slack);
                play = std::max(0.0, play - req);
                star += eff * sc.yRate;
            }
        }
        if (n == (3 * sc.steps) / 4)
        {
            blc.ResetBLCState();
            Obj o;
            ops.push_back(state(o.s("op", "reset")).str());
        }
    }

    return Obj()
        .s("case", sc.name)
        .i("pulse", sc.pulse)
        .i("floor", sc.floor)
        .i("ceiling", sc.ceiling)
        .d("yRate", sc.yRate)
        .d("minMove", sc.minMove)
        .add("ops", arr(ops))
        .str();
}

static std::string blcCases()
{
    std::vector<BlcScenario> scs = {
        { "undershoot", 300, 20, 1500, 900, 0.005, 0.15, 0.05, 2.0, 30, 400, 777 },
        { "overshoot", 1200, 20, 1300, 300, 0.005, 0.15, 0.05, 2.0, 30, 400, 778 },
        { "matched", 600, 100, 900, 600, 0.004, 0.2, 0.1, 1.5, 24, 400, 779 },
        { "fixed", 500, 495, 505, 800, 0.005, 0.15, 0.05, 2.0, 30, 200, 780 },
        { "defaultCeiling", 400, 0, 0, 700, 0.006, 0.12, 0.08, 1.8, 20, 400, 781 },
        { "noisy", 500, 20, 2000, 500, 0.005, 0.1, 0.3, 1.0, 16, 400, 782 },
    };
    std::vector<std::string> out;
    for (auto& s : scs)
        out.push_back(runBlc(s));
    return arr(out);
}

// ---------------------------------------------------------------- guiding_stats cases
static std::string axisStatsDump(const char *op, AxisStats& st)
{
    double slope, intercept, sigma;
    double r2 = st.GetLinearFitResults(&slope, &intercept, &sigma);
    return Obj()
        .s("op", op)
        .i("count", st.GetCount())
        .d("sum", st.GetSum())
        .d("mean", st.GetMean())
        .d("variance", st.GetVariance())
        .d("sigma", st.GetSigma())
        .d("popSigma", st.GetPopulationSigma())
        .d("median", st.GetMedian())
        .d("minDisp", st.GetMinDisplacement())
        .d("maxDisp", st.GetMaxDisplacement())
        .d("maxDelta", st.GetMaxDelta())
        .i("moves", st.GetMoveCount())
        .i("reversals", st.GetReversalCount())
        .d("slope", slope)
        .d("intercept", intercept)
        .d("fitSigma", sigma)
        .d("r2", r2)
        .str();
}

static std::string statsCases()
{
    std::vector<std::string> out;

    // windowed with auto size 7, then manual trims and resize
    {
        WindowedAxisStats w(7);
        Rng rng(99);
        std::vector<std::string> ops;
        for (int n = 0; n < 40; n++)
        {
            double t = n * 2.5;
            double pos = (n < 20 ? 0.5 : -0.8) + 0.4 * rng.gauss() + 0.02 * n;
            double guide = rng.uniform() < 0.3 ? 0.0 : (rng.uniform() < 0.5 ? -1 : 1) * (100 + 500 * rng.uniform());
            w.AddGuideInfo(t, pos, guide);
            Obj o;
            o.s("op", "add").d("t", t).d("pos", pos).d("guide", guide);
            ops.push_back(o.str());
            ops.push_back(axisStatsDump("state", w));
            if (n == 25)
            {
                w.ChangeWindowSize(4);
                ops.push_back(Obj().s("op", "window").i("size", 4).str());
                ops.push_back(axisStatsDump("state", w));
            }
            if (n == 30)
            {
                w.RemoveOldestEntry();
                ops.push_back(Obj().s("op", "removeOldest").str());
                ops.push_back(axisStatsDump("state", w));
            }
        }
        out.push_back(Obj().s("case", "windowed7").i("window", 7).add("ops", arr(ops)).str());
    }

    // non-windowed, all-negative data (exposes the DBL_MIN max quirk)
    {
        WindowedAxisStats w(0);
        Rng rng(7);
        std::vector<std::string> ops;
        for (int n = 0; n < 25; n++)
        {
            double pos = -1.0 - fabs(rng.gauss());
            w.AddGuideInfo(n, pos, n % 3 == 0 ? 0 : (n % 2 ? 1 : -1));
            ops.push_back(Obj().s("op", "add").d("t", n).d("pos", pos).d("guide", n % 3 == 0 ? 0 : (n % 2 ? 1 : -1)).str());
            ops.push_back(axisStatsDump("state", w));
        }
        out.push_back(Obj().s("case", "negative").i("window", 0).add("ops", arr(ops)).str());
    }

    // descriptive stats + filters
    {
        DescriptiveStats ds;
        HighPassFilter hpf(1.0, 2.0);
        LowPassFilter lpf(6.0, 2.0);
        HighPassFilter hpf2(10.0, 0.5);
        LowPassFilter lpf2(3.0, 0.5);
        Rng rng(5);
        std::vector<std::string> ops;
        for (int n = 0; n < 60; n++)
        {
            double v = 0.1 * n + sin(n / 5.0) + 0.3 * rng.gauss();
            ds.AddValue(v);
            ops.push_back(Obj()
                              .d("v", v)
                              .d("hpf", hpf.AddValue(v))
                              .d("lpf", lpf.AddValue(v))
                              .d("hpf2", hpf2.AddValue(v))
                              .d("lpf2", lpf2.AddValue(v))
                              .i("count", ds.GetCount())
                              .d("mean", ds.GetMean())
                              .d("sum", ds.GetSum())
                              .d("min", ds.GetMinimum())
                              .d("max", ds.GetMaximum())
                              .d("variance", ds.GetVariance())
                              .d("sigma", ds.GetSigma())
                              .d("popSigma", ds.GetPopulationSigma())
                              .d("maxDelta", ds.GetMaxDelta())
                              .d("last", ds.GetLastValue())
                              .str());
        }
        out.push_back(Obj().s("case", "descriptive").add("ops", arr(ops)).str());
    }
    return arr(out);
}

int main()
{
    printf("{\"source\":\"PHD2 a6c02722 compiled by tools/phd2-algo-golden\",\n\"algorithms\":%s,\n\"backlash\":%s,\n\"stats\":%s}\n",
           algorithmCases().c_str(), blcCases().c_str(), statsCases().c_str());
    return 0;
}
