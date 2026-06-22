#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Runtime.InteropServices;

namespace NINA.Equipment.Equipment.MyGuider.PHD2.Native {

    /// <summary>
    /// P/Invoke bindings for libphd2core's C ABI (phd2core/phd2_capi.h), the headless,
    /// wxWidgets-free PHD2 guiding engine. The native library is built separately from the
    /// phd2 fork (libphd2core) and deployed as libphd2core.so / phd2core.dll.
    ///
    /// These are raw bindings; use <see cref="Phd2CoreEngine"/> for a managed, event-driven
    /// wrapper. The struct layouts mirror the C structs field-for-field (LayoutKind.Sequential
    /// with the runtime's natural alignment, which matches the C ABI on the same platform).
    /// </summary>
    internal static class Phd2CoreInterop {
        internal const string DllName = "phd2core";

        internal const double UnknownDeclination = 997.0;

        internal enum Phd2Direction {
            None = 0,
            North = 1, // Dec +
            South = 2, // Dec -
            East = 3,  // RA -
            West = 4,  // RA +
        }

        internal enum Phd2State {
            Stopped = 0,
            Selecting = 1,
            Selected = 2,
            Calibrating = 3,
            Guiding = 4,
        }

        // Values match PHD2's GUIDE_ALGORITHM enum.
        internal enum Phd2Algorithm {
            None = -1,
            Identity = 0,
            Hysteresis = 1,
            Lowpass = 2,
            Lowpass2 = 3,
            ResistSwitch = 4,
            GaussianProcess = 5,
            ZFilter = 6,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Phd2Config {
            public int SearchRegion;
            public double MinStarHFD;
            public double MaxStarHFD;
            public double MinStarSNR;
            public double PixelScale;
            public int SaturationByADU; // bool
            public ushort SaturationADU;
            public Phd2Algorithm RaAlgorithm;
            public Phd2Algorithm DecAlgorithm;
            public double DitherScale;
            public int CalibrationDurationMs;
            public double CalibrationDistancePx;
            public double CalibrationDeclination;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Phd2Calibration {
            public double XRate;  // px per ms, RA
            public double YRate;  // px per ms, Dec
            public double XAngle; // radians
            public double YAngle; // radians
            public double Declination;
            public double RotatorAngle;
            public ushort Binning;
            public int PierSide;       // -1 unknown, 0 east, 1 west
            public int RaGuideParity;  // 1 even, -1 odd, 0 unknown
            public int DecGuideParity;
            public int IsValid; // bool
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Phd2CoreSettle {
            public double TolerancePx;
            public double SettleTimeSec;
            public double TimeoutSec;
            public int Frames;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Phd2Pulse {
            public Phd2Direction RaDirection;
            public int RaDurationMs;
            public Phd2Direction DecDirection;
            public int DecDurationMs;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Phd2GuideStep {
            public int StarFound; // bool
            public double Dx;
            public double Dy;
            public double RaRaw;
            public double DecRaw;
            public double RaDistance;
            public double DecDistance;
            public int RaDurationMs;
            public int DecDurationMs;
            public Phd2Direction RaDirection;
            public Phd2Direction DecDirection;
            public double Snr;
            public double Hfd;
            public double Mass;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Phd2PulseCallback(ref Phd2Pulse pulse, IntPtr user);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Phd2GuideStepCallback(ref Phd2GuideStep step, IntPtr user);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Phd2StateCallback(Phd2State state, IntPtr user);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Phd2StarLostCallback(IntPtr user);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Phd2StatusCallback(int success, IntPtr user);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Phd2Callbacks {
            public Phd2PulseCallback OnPulse;
            public Phd2GuideStepCallback OnGuideStep;
            public Phd2StateCallback OnStateChange;
            public Phd2StarLostCallback OnStarLost;
            public Phd2StatusCallback OnSettleDone;
            public Phd2StatusCallback OnCalibrationDone;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_default_config(out Phd2Config cfg);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr phd_create(ref Phd2Config cfg, ref Phd2Callbacks cb, IntPtr user);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern void phd_set_param(IntPtr handle, string key, string value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_auto_select_star(IntPtr handle, ushort[] px, int w, int h);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_select_star(IntPtr handle, ushort[] px, int w, int h, double x, double y);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_set_calibration(IntPtr handle, ref Phd2Calibration cal);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_get_calibration(IntPtr handle, out Phd2Calibration outCal);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_is_calibrated(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_start_calibration(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_set_calibration_declination(IntPtr handle, double declination);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_adjust_for_declination(IntPtr handle, double declination);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_start_guiding(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_stop_guiding(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_dither(IntPtr handle, double pixels, int raOnly, ref Phd2CoreSettle settle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_start_settling(IntPtr handle, ref Phd2CoreSettle settle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int phd_is_settling(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_set_dither_seed(IntPtr handle, uint seed);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_push_frame(IntPtr handle, ushort[] px, int w, int h, double exposureSeconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Phd2State phd_get_state(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_get_lock_position(IntPtr handle, out double x, out double y);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void phd_get_current_position(IntPtr handle, out double x, out double y);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double phd_current_error(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr phd_version();
    }
}
