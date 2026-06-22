#region "copyright"

/*
    Copyright � 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using System.Windows.Media;

namespace NINA.Profile.Interfaces {

    public interface IGuiderSettings : ISettings {
        string GuiderName { get; set; }
        string LastDeviceName { get; set; }
        double DitherPixels { get; set; }
        bool DitherRAOnly { get; set; }
        GuiderScaleEnum PHD2GuiderScale { get; set; }
        double MaxY { get; set; }
        int PHD2HistorySize { get; set; }
        int PHD2ServerPort { get; set; }
        string PHD2ServerUrl { get; set; }
        int PHD2InstanceNumber { get; set; }
        string PHD2Camera { get; set; }
        string PHD2CameraId { get; set; }
        int PHD2CameraDepth { get; set; }
        string IntegratedGuideCameraId { get; set; }
        int SettleTime { get; set; }
        double SettlePixels { get; set; }
        int SettleTimeout { get; set; }
        string PHD2Path { get; set; }
        bool AutoRetryStartGuiding { get; set; }
        int AutoRetryStartGuidingTimeoutSeconds { get; set; }
        bool MetaGuideUseIpAddressAny { get; set; }
        int MetaGuidePort { get; set; }
        int MGENFocalLength { get; set; }
        int MGENPixelMargin { get; set; }
        int MetaGuideMinIntensity { get; set; }
        int MetaGuideDitherSettleSeconds { get; set; }
        bool MetaGuideLockWhenGuiding { get; set; }
        int PHD2ROIPct { get; set; }
        int? PHD2ProfileId { get; set; }
        double? PHD2RAMinMove { get; set; }
        double? PHD2DecMinMove { get; set; }
        double? PHD2RAAggressiveness { get; set; }
        double? PHD2DecAggressiveness { get; set; }
        double? PHD2RAHysteresis { get; set; }
        double? PHD2DecHysteresis { get; set; }
        bool? PHD2DecFastSwitch { get; set; }
        bool? PHD2RAFastSwitch { get; set; }
        double? PHD2RASlopeWeight { get; set; }
        double? PHD2DecSlopeWeight { get; set; }
        double? PHD2RALowpass2Aggressiveness { get; set; }
        double? PHD2DecLowpass2Aggressiveness { get; set; }
        double? PHD2RAPredictiveWeight { get; set; }
        double? PHD2DecPredictiveWeight { get; set; }
        double? PHD2RAReactiveWeight { get; set; }
        double? PHD2DecReactiveWeight { get; set; }
        double? PHD2RAPeriodLength { get; set; }
        double? PHD2DecPeriodLength { get; set; }
        bool? PHD2RAGPAutoAdjustPeriod { get; set; }
        bool? PHD2DecGPAutoAdjustPeriod { get; set; }
        double? PHD2RAExpFactor { get; set; }
        double? PHD2DecExpFactor { get; set; }
        string PHD2DecGuideMode { get; set; }
        int? PHD2ExposureMs { get; set; }
        int? PHD2CalibrationStepMs { get; set; }
        int? PHD2CalibrationDistancePx { get; set; }
        int? PHD2SearchRegion { get; set; }
        int? PHD2MaxRADuration { get; set; }
        int? PHD2MaxDecDuration { get; set; }
        string PHD2GuideAlgorithmRA { get; set; }
        string PHD2GuideAlgorithmDec { get; set; }
        double? PHD2DitherScale { get; set; }
        bool? PHD2DitherRAOnly { get; set; }
        string PHD2DitherMode { get; set; }
        int? PHD2NoiseReductionMethod { get; set; }
        int? PHD2CameraGain { get; set; }
        int? PHD2CameraBinning { get; set; }
        bool? PHD2UseSubframes { get; set; }
        int? PHD2FocalLength { get; set; }
        bool? PHD2AutoRestoreCalibration { get; set; }
        bool? PHD2AssumeDecOrthogonal { get; set; }
        bool? PHD2UseDecCompensation { get; set; }
        bool? PHD2ReverseDecOnFlip { get; set; }
        bool? PHD2FastRecenter { get; set; }
        double? PHD2MinStarHFD { get; set; }
        double? PHD2MaxStarHFD { get; set; }
        bool? PHD2BeepForLostStar { get; set; }
        bool? PHD2MassChangeThresholdEnabled { get; set; }
        double? PHD2MassChangeThreshold { get; set; }
        bool? PHD2UseMultipleStars { get; set; }
        int? PHD2TimeLapseMs { get; set; }
        bool? PHD2VarDelayEnabled { get; set; }
        int? PHD2VarDelayShortSec { get; set; }
        int? PHD2VarDelayLongSec { get; set; }
        double? PHD2AfMinStarSnr { get; set; }
        string PHD2AutoSelectDownsample { get; set; }
        bool? PHD2SaturationByADU { get; set; }
        int? PHD2SaturationADUValue { get; set; }
        bool? PHD2BacklashCompEnabled { get; set; }
        int? PHD2BacklashPulseWidth { get; set; }
        int? PHD2BacklashFloor { get; set; }
        int? PHD2BacklashCeiling { get; set; }
        int SkyGuardServerPort { get; set; }
        string SkyGuardServerUrl { get; set; }
        string SkyGuardPath { get; set; }
        int SkyGuardCallbackPort { get; set; }
        bool SkyGuardTimeLapsChecked { get; set; }
        double SkyGuardValueMaxGuiding { get; set; }
        double SkyGuardTimeLapsGuiding { get; set; }
        bool SkyGuardTimeLapsDitherChecked { get; set; }
        double SkyGuardValueMaxDithering { get; set; }
        double SkyGuardTimeLapsDithering { get; set; }
        double SkyGuardTimeOutGuiding { get; set; }
        Color GuideChartRightAscensionColor { get; set; }
        Color GuideChartDeclinationColor { get; set; }
        bool GuideChartShowCorrections { get; set; }
    }
}
