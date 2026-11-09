#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Interfaces {
    public interface IINDICamera : IINDIDevice {
        int CameraXSize { get; }
        int CameraYSize { get; }
        double PixelSizeX { get; }
        double PixelSizeY { get; }
        int BitDepth { get; }
        double ExposureMin { get; }
        double ExposureMax { get; }
        short MaxBinX { get; }
        short MaxBinY { get; }
        short BinX { get; set; }
        short BinY { get; set; }
        string CfaType { get; }
        int CfaOffsetX { get; }
        int CfaOffsetY { get; }
        double Temperature { get; }
        double TemperatureSetPoint { get; set; }
        bool CanSetTemperature { get; }
        bool CoolerOn { get; set; }
        double CoolerPower { get; }
        bool CanGetGain { get; }
        bool CanSetGain { get; }
        int Gain { get; set; }
        int GainMin { get; }
        int GainMax { get; }
        bool CanSetOffset { get; }
        int Offset { get; set; }
        int OffsetMin { get; }
        int OffsetMax { get; }
        bool CanSubSample { get; }
        bool HasDewHeater { get; }
        bool DewHeaterOn { get; }
        int DewHeaterMaxStrength { get; }
        void SetDewHeater(bool on, int strength);
        bool HasLowNoiseMode { get; }
        bool LowNoiseMode { get; set; }
        bool HasHighFullwell { get; }
        bool HighFullwellMode { get; set; }
        bool HasTailLight { get; }
        bool TailLight { get; set; }
        bool CanSetUSBLimit { get; }
        int USBLimit { get; set; }
        int USBLimitMin { get; }
        int USBLimitMax { get; }
        int USBLimitStep { get; }
        string[] ReadoutModes { get; }
        short ReadoutMode { get; set; }

        void QueueProfileSwitch(string propertyName, bool enable);
        void SetBinning(short x, short y);
        void StartExposure(double exposureTime, short binX, short binY, bool enableSubSample, int subSampleX, int subSampleY, int subSampleWidth, int subSampleHeight, int gain, int offset);
        Task WaitUntilExposureIsReady(CancellationToken token);
        byte[] GetLastBlobData();
        string GetLastBlobFormat();
        void AbortExposure();
        void StopExposure();

        /// <summary>A timed guide pulse on the camera's ST4 port (TELESCOPE_TIMED_GUIDE_*); throws when it has none.</summary>
        void St4PulseGuide(GuideDirections direction, int duration);

        /// <summary>True while an ST4 pulse runs (from sending it until the driver reports its end, bounded).</summary>
        bool IsSt4PulseGuiding { get; }

        /// <summary>Why the driver rejected the last ST4 pulse on this direction's axis (Alert), or null; taken once.</summary>
        string TakeRejectedSt4Pulse(GuideDirections direction);
    }
}
