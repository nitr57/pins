#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Windows.Media;

namespace NINA.Profile {

    [Serializable()]
    [DataContract]
    public class GuiderSettings : Settings, IGuiderSettings {

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            SetDefaultValues();
        }

        protected override void SetDefaultValues() {
            lastDeviceName = string.Empty;
            ditherPixels = 5;
            ditherRAOnly = false;
            settleTime = 10;
            pHD2ServerUrl = "localhost";
            pHD2ServerPort = 4400;
            pHD2InstanceNumber = 1;
            pHD2LargeHistorySize = 100;
            pHD2GuiderScale = GuiderScaleEnum.ARCSECONDS;
            pHD2Camera = "None";
            pHD2CameraId = string.Empty;
            pHD2CameraDepth = 16;
            phd2ROIPct = 100;
            settlePixels = 1.5;
            settleTimeout = 40;
            autoRetryStartGuiding = false;
            autoRetryStartGuidingTimeoutSeconds = 300;
            maxY = 4;
            phd2RAMinMove = 0.2;
            phd2DecMinMove = 0.2;
            phd2RAAggressiveness = 0.7;
            phd2DecAggressiveness = 1.0;
            phd2RAHysteresis = 0.1;
            phd2DecHysteresis = 0.1;
            phd2DecFastSwitch = true;
            phd2RAFastSwitch = true;
            phd2RASlopeWeight = 5.0;
            phd2DecSlopeWeight = 5.0;
            phd2RALowpass2Aggressiveness = 80.0;
            phd2DecLowpass2Aggressiveness = 80.0;
            phd2RAPredictiveWeight = 0.5;
            phd2DecPredictiveWeight = 0.5;
            phd2RAReactiveWeight = 0.6;
            phd2DecReactiveWeight = 0.6;
            phd2RAPeriodLength = 200.0;
            phd2DecPeriodLength = 200.0;
            phd2RAExpFactor = 2.0;
            phd2DecExpFactor = 2.0;
            phd2RAGPAutoAdjustPeriod = true;
            phd2DecGPAutoAdjustPeriod = true;
            phd2DecGuideMode = "Auto";
            phd2ExposureMs = 1000;
            phd2CalibrationStepMs = 750;
            phd2CalibrationDistancePx = 25;
            phd2SearchRegion = 15;
            phd2MaxRADuration = 2500;
            phd2MaxDecDuration = 2500;
            phd2GuideAlgorithmRA = "Hysteresis";
            phd2GuideAlgorithmDec = "Resist Switch";
            phd2DitherScale = 1.0;
            phd2DitherRAOnly = false;
            phd2DitherMode = "random";
            phd2NoiseReductionMethod = 0;
            phd2CameraGain = 95;
            phd2CameraBinning = 1;
            phd2UseSubframes = false;
            phd2FocalLength = 200;
            phd2AssumeDecOrthogonal = false;
            phd2UseDecCompensation = true;
            phd2ReverseDecOnFlip = false;
            phd2FastRecenter = true;
            phd2MinStarHFD = 1.5;
            phd2MaxStarHFD = 10.0;
            phd2BeepForLostStar = true;
            phd2MassChangeThresholdEnabled = true;
            phd2MassChangeThreshold = 0.5;
            phd2UseMultipleStars = true;
            phd2TimeLapseMs = 0;
            phd2VarDelayEnabled = false;
            phd2VarDelayShortSec = 1;
            phd2VarDelayLongSec = 10;
            phd2AfMinStarSnr = 6.0;
            phd2AutoSelectDownsample = "Auto";
            phd2SaturationByADU = true;
            phd2SaturationADUValue = 255;
            phd2BacklashCompEnabled = false;
            phd2BacklashPulseWidth = 0;
            phd2BacklashFloor = 0;
            phd2BacklashCeiling = 0;
            metaGuideUseIpAddressAny = false;
            metaGuidePort = 1277;
            metaGuideMinIntensity = 100;
            metaGuideLockWhenGuiding = false;
            skyGuardServerUrl = "localhost";
            skyGuardServerPort = 18700;
            skyGuardCallbackPort = 8000;
            skyGuardTimeLapsChecked = false;
            skyGuardValueMaxGuiding = 1;
            skyGuardTimeLapsGuiding = 60;
            skyGuardTimeLapsDitherChecked = false;
            skyGuardValueMaxDithering = 1;
            skyGuardTimeLapsDithering = 60;
            skyGuardTimeOutGuiding = 5;

            var defaultPHD2Path = Environment.ExpandEnvironmentVariables(@"/usr/bin/phd2");

            phd2Path =
                File.Exists(defaultPHD2Path)
                ? defaultPHD2Path
                : string.Empty;
            guiderName = "PHD2";
            mgenFocalLength = 1000;
            mgenPixelMargin = 10;
            metaGuideDitherSettleSeconds = 30;

            var defaultSkyGuardPath = Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES%\SkyGuard\SkyGuard.exe");
            skyGuardPath = File.Exists(defaultSkyGuardPath) ? defaultSkyGuardPath : string.Empty;

            guideChartRightAscensionColor = Colors.Blue;
            guideChartDeclinationColor = Colors.Red;
            guideChartShowCorrections = true;
        }

        private string lastDeviceName;

        [DataMember]
        public string LastDeviceName {
            get => lastDeviceName;
            set {
                if (lastDeviceName != value) {
                    lastDeviceName = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double ditherPixels;

        [DataMember]
        public double DitherPixels {
            get => ditherPixels;
            set {
                if (ditherPixels != value) {
                    ditherPixels = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool ditherRAOnly;

        [DataMember]
        public bool DitherRAOnly {
            get => ditherRAOnly;
            set {
                if (ditherRAOnly != value) {
                    ditherRAOnly = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int settleTime;

        [DataMember]
        public int SettleTime {
            get => settleTime;
            set {
                if (settleTime != value) {
                    settleTime = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int pHD2InstanceNumber;

        [DataMember]
        public int PHD2InstanceNumber {
            get => pHD2InstanceNumber;
            set {
                if (pHD2InstanceNumber != value) {
                    pHD2InstanceNumber = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string pHD2ServerUrl;

        [DataMember]
        public string PHD2ServerUrl {
            get => pHD2ServerUrl;
            set {
                if (pHD2ServerUrl != value) {
                    pHD2ServerUrl = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int pHD2ServerPort;

        [DataMember]
        public int PHD2ServerPort {
            get => pHD2ServerPort;
            set {
                if (pHD2ServerPort != value) {
                    pHD2ServerPort = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string pHD2Camera;

        [DataMember]
        public string PHD2Camera {
            get => pHD2Camera;
            set {
                if (pHD2Camera != value) {
                    pHD2Camera = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string pHD2CameraId;

        [DataMember]
        public string PHD2CameraId {
            get => pHD2CameraId;
            set {
                if (pHD2CameraId != value) {
                    pHD2CameraId = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int pHD2CameraDepth;

        [DataMember]
        public int PHD2CameraDepth {
            get => pHD2CameraDepth;
            set {
                if (pHD2CameraDepth != value) {
                    pHD2CameraDepth = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int pHD2LargeHistorySize;

        [DataMember]
        public int PHD2HistorySize {
            get => pHD2LargeHistorySize;
            set {
                if (pHD2LargeHistorySize != value) {
                    pHD2LargeHistorySize = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string phd2Path;

        [DataMember]
        public string PHD2Path {
            get => phd2Path;
            set {
                if (phd2Path != value) {
                    phd2Path = value;
                    RaisePropertyChanged();
                }
            }
        }

        private GuiderScaleEnum pHD2GuiderScale;

        [DataMember]
        public GuiderScaleEnum PHD2GuiderScale {
            get => pHD2GuiderScale;
            set {
                if (pHD2GuiderScale != value) {
                    pHD2GuiderScale = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double settlePixels;

        [DataMember]
        public double SettlePixels {
            get => settlePixels;

            set {
                if (settlePixels != value) {
                    settlePixels = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int settleTimeout;

        [DataMember]
        public int SettleTimeout {
            get => settleTimeout;

            set {
                if (settleTimeout != value) {
                    settleTimeout = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string guiderName;

        [DataMember]
        public string GuiderName {
            get => guiderName;
            set {
                if (guiderName != value) {
                    guiderName = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool autoRetryStartGuiding;

        [DataMember]
        public bool AutoRetryStartGuiding {
            get => autoRetryStartGuiding;
            set {
                if (autoRetryStartGuiding == value) return;
                autoRetryStartGuiding = value;
                RaisePropertyChanged();
            }
        }

        private int autoRetryStartGuidingTimeoutSeconds;

        [DataMember]
        public int AutoRetryStartGuidingTimeoutSeconds {
            get => autoRetryStartGuidingTimeoutSeconds;
            set {
                if (autoRetryStartGuidingTimeoutSeconds == value) return;
                autoRetryStartGuidingTimeoutSeconds = value;
                RaisePropertyChanged();
            }
        }

        private double maxY;

        [DataMember]
        public double MaxY {
            get => maxY;
            set {
                if (maxY != value) {
                    maxY = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool metaGuideUseIpAddressAny;

        [DataMember]
        public bool MetaGuideUseIpAddressAny {
            get => metaGuideUseIpAddressAny;
            set {
                if (metaGuideUseIpAddressAny != value) {
                    metaGuideUseIpAddressAny = value;
                    RaisePropertyChanged();
                }
            }
        
        }

        private int metaGuidePort;

        [DataMember]
        public int MetaGuidePort {
            get => metaGuidePort;
            set {
                if (metaGuidePort != value) {
                    metaGuidePort = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int mgenFocalLength;

        [DataMember]
        public int MGENFocalLength {
            get => mgenFocalLength;
            set {
                if (mgenFocalLength != value) {
                    mgenFocalLength = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int mgenPixelMargin;

        [DataMember]
        public int MGENPixelMargin {
            get => mgenPixelMargin;
            set {
                if (mgenPixelMargin != value) {
                    mgenPixelMargin = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int metaGuideMinIntensity;

        [DataMember]
        public int MetaGuideMinIntensity {
            get => metaGuideMinIntensity;
            set {
                if (metaGuideMinIntensity != value) {
                    metaGuideMinIntensity = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int metaGuideDitherSettleSeconds;

        [DataMember]
        public int MetaGuideDitherSettleSeconds {
            get => metaGuideDitherSettleSeconds;
            set {
                if (metaGuideDitherSettleSeconds != value) {
                    metaGuideDitherSettleSeconds = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool metaGuideLockWhenGuiding;

        [DataMember]
        public bool MetaGuideLockWhenGuiding {
            get => metaGuideLockWhenGuiding;
            set {
                if (metaGuideLockWhenGuiding != value) {
                    metaGuideLockWhenGuiding = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int phd2ROIPct;

        [DataMember]
        public int PHD2ROIPct {
            get => phd2ROIPct;
            set {
                if (phd2ROIPct != value) {
                    phd2ROIPct = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2ProfileId;

        [DataMember]
        public int? PHD2ProfileId {
            get => phd2ProfileId;
            set {
                if (phd2ProfileId != value) {
                    phd2ProfileId = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RAMinMove;

        [DataMember]
        public double? PHD2RAMinMove {
            get => phd2RAMinMove;
            set {
                if (phd2RAMinMove != value) {
                    phd2RAMinMove = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecMinMove;

        [DataMember]
        public double? PHD2DecMinMove {
            get => phd2DecMinMove;
            set {
                if (phd2DecMinMove != value) {
                    phd2DecMinMove = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RAAggressiveness;

        [DataMember]
        public double? PHD2RAAggressiveness {
            get => phd2RAAggressiveness;
            set {
                if (phd2RAAggressiveness != value) {
                    phd2RAAggressiveness = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecAggressiveness;

        [DataMember]
        public double? PHD2DecAggressiveness {
            get => phd2DecAggressiveness;
            set {
                if (phd2DecAggressiveness != value) {
                    phd2DecAggressiveness = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RAHysteresis;

        [DataMember]
        public double? PHD2RAHysteresis {
            get => phd2RAHysteresis;
            set {
                if (phd2RAHysteresis != value) {
                    phd2RAHysteresis = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecHysteresis;

        [DataMember]
        public double? PHD2DecHysteresis {
            get => phd2DecHysteresis;
            set {
                if (phd2DecHysteresis != value) {
                    phd2DecHysteresis = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2DecFastSwitch;

        [DataMember]
        public bool? PHD2DecFastSwitch {
            get => phd2DecFastSwitch;
            set {
                if (phd2DecFastSwitch != value) {
                    phd2DecFastSwitch = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2RAFastSwitch;

        [DataMember]
        public bool? PHD2RAFastSwitch {
            get => phd2RAFastSwitch;
            set {
                if (phd2RAFastSwitch != value) {
                    phd2RAFastSwitch = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RASlopeWeight;

        [DataMember]
        public double? PHD2RASlopeWeight {
            get => phd2RASlopeWeight;
            set {
                if (phd2RASlopeWeight != value) {
                    phd2RASlopeWeight = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecSlopeWeight;

        [DataMember]
        public double? PHD2DecSlopeWeight {
            get => phd2DecSlopeWeight;
            set {
                if (phd2DecSlopeWeight != value) {
                    phd2DecSlopeWeight = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RALowpass2Aggressiveness;

        [DataMember]
        public double? PHD2RALowpass2Aggressiveness {
            get => phd2RALowpass2Aggressiveness;
            set {
                if (phd2RALowpass2Aggressiveness != value) {
                    phd2RALowpass2Aggressiveness = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecLowpass2Aggressiveness;

        [DataMember]
        public double? PHD2DecLowpass2Aggressiveness {
            get => phd2DecLowpass2Aggressiveness;
            set {
                if (phd2DecLowpass2Aggressiveness != value) {
                    phd2DecLowpass2Aggressiveness = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RAPredictiveWeight;

        [DataMember]
        public double? PHD2RAPredictiveWeight {
            get => phd2RAPredictiveWeight;
            set {
                if (phd2RAPredictiveWeight != value) {
                    phd2RAPredictiveWeight = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecPredictiveWeight;

        [DataMember]
        public double? PHD2DecPredictiveWeight {
            get => phd2DecPredictiveWeight;
            set {
                if (phd2DecPredictiveWeight != value) {
                    phd2DecPredictiveWeight = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RAReactiveWeight;

        [DataMember]
        public double? PHD2RAReactiveWeight {
            get => phd2RAReactiveWeight;
            set {
                if (phd2RAReactiveWeight != value) {
                    phd2RAReactiveWeight = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecReactiveWeight;

        [DataMember]
        public double? PHD2DecReactiveWeight {
            get => phd2DecReactiveWeight;
            set {
                if (phd2DecReactiveWeight != value) {
                    phd2DecReactiveWeight = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RAPeriodLength;

        [DataMember]
        public double? PHD2RAPeriodLength {
            get => phd2RAPeriodLength;
            set {
                if (phd2RAPeriodLength != value) {
                    phd2RAPeriodLength = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecPeriodLength;

        [DataMember]
        public double? PHD2DecPeriodLength {
            get => phd2DecPeriodLength;
            set {
                if (phd2DecPeriodLength != value) {
                    phd2DecPeriodLength = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2RAExpFactor;

        [DataMember]
        public double? PHD2RAExpFactor {
            get => phd2RAExpFactor;
            set {
                if (phd2RAExpFactor != value) {
                    phd2RAExpFactor = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DecExpFactor;

        [DataMember]
        public double? PHD2DecExpFactor {
            get => phd2DecExpFactor;
            set {
                if (phd2DecExpFactor != value) {
                    phd2DecExpFactor = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2RAGPAutoAdjustPeriod;

        [DataMember]
        public bool? PHD2RAGPAutoAdjustPeriod {
            get => phd2RAGPAutoAdjustPeriod;
            set {
                if (phd2RAGPAutoAdjustPeriod != value) {
                    phd2RAGPAutoAdjustPeriod = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2DecGPAutoAdjustPeriod;

        [DataMember]
        public bool? PHD2DecGPAutoAdjustPeriod {
            get => phd2DecGPAutoAdjustPeriod;
            set {
                if (phd2DecGPAutoAdjustPeriod != value) {
                    phd2DecGPAutoAdjustPeriod = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string phd2DecGuideMode;

        [DataMember]
        public string PHD2DecGuideMode {
            get => phd2DecGuideMode;
            set {
                if (phd2DecGuideMode != value) {
                    phd2DecGuideMode = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2ExposureMs;

        [DataMember]
        public int? PHD2ExposureMs {
            get => phd2ExposureMs;
            set {
                if (phd2ExposureMs != value) {
                    phd2ExposureMs = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2CalibrationStepMs;

        [DataMember]
        public int? PHD2CalibrationStepMs {
            get => phd2CalibrationStepMs;
            set {
                if (phd2CalibrationStepMs != value) {
                    phd2CalibrationStepMs = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2CalibrationDistancePx;

        [DataMember]
        public int? PHD2CalibrationDistancePx {
            get => phd2CalibrationDistancePx;
            set {
                if (phd2CalibrationDistancePx != value) {
                    phd2CalibrationDistancePx = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2SearchRegion;

        [DataMember]
        public int? PHD2SearchRegion {
            get => phd2SearchRegion;
            set {
                if (phd2SearchRegion != value) {
                    phd2SearchRegion = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2MaxRADuration;

        [DataMember]
        public int? PHD2MaxRADuration {
            get => phd2MaxRADuration;
            set {
                if (phd2MaxRADuration != value) {
                    phd2MaxRADuration = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2MaxDecDuration;

        [DataMember]
        public int? PHD2MaxDecDuration {
            get => phd2MaxDecDuration;
            set {
                if (phd2MaxDecDuration != value) {
                    phd2MaxDecDuration = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string phd2GuideAlgorithmRA;

        [DataMember]
        public string PHD2GuideAlgorithmRA {
            get => phd2GuideAlgorithmRA;
            set {
                if (phd2GuideAlgorithmRA != value) {
                    phd2GuideAlgorithmRA = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string phd2GuideAlgorithmDec;

        [DataMember]
        public string PHD2GuideAlgorithmDec {
            get => phd2GuideAlgorithmDec;
            set {
                if (phd2GuideAlgorithmDec != value) {
                    phd2GuideAlgorithmDec = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2DitherScale;

        [DataMember]
        public double? PHD2DitherScale {
            get => phd2DitherScale;
            set {
                if (phd2DitherScale != value) {
                    phd2DitherScale = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2DitherRAOnly;

        [DataMember]
        public bool? PHD2DitherRAOnly {
            get => phd2DitherRAOnly;
            set {
                if (phd2DitherRAOnly != value) {
                    phd2DitherRAOnly = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string phd2DitherMode;

        [DataMember]
        public string PHD2DitherMode {
            get => phd2DitherMode;
            set {
                if (phd2DitherMode != value) {
                    phd2DitherMode = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2NoiseReductionMethod;

        [DataMember]
        public int? PHD2NoiseReductionMethod {
            get => phd2NoiseReductionMethod;
            set {
                if (phd2NoiseReductionMethod != value) {
                    phd2NoiseReductionMethod = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2CameraGain;

        [DataMember]
        public int? PHD2CameraGain {
            get => phd2CameraGain;
            set {
                if (phd2CameraGain != value) {
                    phd2CameraGain = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2CameraBinning;

        [DataMember]
        public int? PHD2CameraBinning {
            get => phd2CameraBinning;
            set {
                if (phd2CameraBinning != value) {
                    phd2CameraBinning = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2UseSubframes;

        [DataMember]
        public bool? PHD2UseSubframes {
            get => phd2UseSubframes;
            set {
                if (phd2UseSubframes != value) {
                    phd2UseSubframes = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int? phd2FocalLength;

        [DataMember]
        public int? PHD2FocalLength {
            get => phd2FocalLength;
            set {
                if (phd2FocalLength != value) {
                    phd2FocalLength = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2AutoRestoreCalibration;

        [DataMember]
        public bool? PHD2AutoRestoreCalibration {
            get => phd2AutoRestoreCalibration;
            set {
                if (phd2AutoRestoreCalibration != value) {
                    phd2AutoRestoreCalibration = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2AssumeDecOrthogonal;

        [DataMember]
        public bool? PHD2AssumeDecOrthogonal {
            get => phd2AssumeDecOrthogonal;
            set {
                if (phd2AssumeDecOrthogonal != value) {
                    phd2AssumeDecOrthogonal = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2UseDecCompensation;

        [DataMember]
        public bool? PHD2UseDecCompensation {
            get => phd2UseDecCompensation;
            set {
                if (phd2UseDecCompensation != value) {
                    phd2UseDecCompensation = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2ReverseDecOnFlip;

        [DataMember]
        public bool? PHD2ReverseDecOnFlip {
            get => phd2ReverseDecOnFlip;
            set {
                if (phd2ReverseDecOnFlip != value) {
                    phd2ReverseDecOnFlip = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2FastRecenter;

        [DataMember]
        public bool? PHD2FastRecenter {
            get => phd2FastRecenter;
            set {
                if (phd2FastRecenter != value) {
                    phd2FastRecenter = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2MinStarHFD;

        [DataMember]
        public double? PHD2MinStarHFD {
            get => phd2MinStarHFD;
            set {
                if (phd2MinStarHFD != value) {
                    phd2MinStarHFD = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2MaxStarHFD;

        [DataMember]
        public double? PHD2MaxStarHFD {
            get => phd2MaxStarHFD;
            set {
                if (phd2MaxStarHFD != value) {
                    phd2MaxStarHFD = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2BeepForLostStar;

        [DataMember]
        public bool? PHD2BeepForLostStar {
            get => phd2BeepForLostStar;
            set {
                if (phd2BeepForLostStar != value) {
                    phd2BeepForLostStar = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2MassChangeThresholdEnabled;

        [DataMember]
        public bool? PHD2MassChangeThresholdEnabled {
            get => phd2MassChangeThresholdEnabled;
            set {
                if (phd2MassChangeThresholdEnabled != value) {
                    phd2MassChangeThresholdEnabled = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double? phd2MassChangeThreshold;

        [DataMember]
        public double? PHD2MassChangeThreshold {
            get => phd2MassChangeThreshold;
            set {
                if (phd2MassChangeThreshold != value) {
                    phd2MassChangeThreshold = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool? phd2UseMultipleStars;

        [DataMember]
        public bool? PHD2UseMultipleStars {
            get => phd2UseMultipleStars;
            set {
                if (phd2UseMultipleStars != value) {
                    phd2UseMultipleStars = value;
                    RaisePropertyChanged();
                }
            }
        }

        int? phd2TimeLapseMs;
        [DataMember]
        public int? PHD2TimeLapseMs {
            get => phd2TimeLapseMs;
            set {
                if (phd2TimeLapseMs != value) {
                    phd2TimeLapseMs = value;
                    RaisePropertyChanged();
                }
            }
        }

        bool? phd2VarDelayEnabled;
        [DataMember]
        public bool? PHD2VarDelayEnabled {
            get => phd2VarDelayEnabled;
            set {
                if (phd2VarDelayEnabled != value) {
                    phd2VarDelayEnabled = value;
                    RaisePropertyChanged();
                }
            }
        }

        int? phd2VarDelayShortSec;
        [DataMember]
        public int? PHD2VarDelayShortSec {
            get => phd2VarDelayShortSec;
            set {
                if (phd2VarDelayShortSec != value) {
                    phd2VarDelayShortSec = value;
                    RaisePropertyChanged();
                }
            }
        }

        int? phd2VarDelayLongSec;
        [DataMember]
        public int? PHD2VarDelayLongSec {
            get => phd2VarDelayLongSec;
            set {
                if (phd2VarDelayLongSec != value) {
                    phd2VarDelayLongSec = value;
                    RaisePropertyChanged();
                }
            }
        }

        double? phd2AfMinStarSnr;
        [DataMember]
        public double? PHD2AfMinStarSnr {
            get => phd2AfMinStarSnr;
            set {
                if (phd2AfMinStarSnr != value) {
                    phd2AfMinStarSnr = value;
                    RaisePropertyChanged();
                }
            }
        }

        string phd2AutoSelectDownsample;
        [DataMember]
        public string PHD2AutoSelectDownsample {
            get => phd2AutoSelectDownsample;
            set {
                if (phd2AutoSelectDownsample != value) {
                    phd2AutoSelectDownsample = value;
                    RaisePropertyChanged();
                }
            }
        }

        bool? phd2SaturationByADU;
        [DataMember]
        public bool? PHD2SaturationByADU {
            get => phd2SaturationByADU;
            set {
                if (phd2SaturationByADU != value) {
                    phd2SaturationByADU = value;
                    RaisePropertyChanged();
                }
            }
        }

        int? phd2SaturationADUValue;
        [DataMember]
        public int? PHD2SaturationADUValue {
            get => phd2SaturationADUValue;
            set {
                if (phd2SaturationADUValue != value) {
                    phd2SaturationADUValue = value;
                    RaisePropertyChanged();
                }
            }
        }

        bool? phd2BacklashCompEnabled;
        [DataMember]
        public bool? PHD2BacklashCompEnabled {
            get => phd2BacklashCompEnabled;
            set {
                if (phd2BacklashCompEnabled != value) {
                    phd2BacklashCompEnabled = value;
                    RaisePropertyChanged();
                }
            }
        }

        int? phd2BacklashPulseWidth;
        [DataMember]
        public int? PHD2BacklashPulseWidth {
            get => phd2BacklashPulseWidth;
            set {
                if (phd2BacklashPulseWidth != value) {
                    phd2BacklashPulseWidth = value;
                    RaisePropertyChanged();
                }
            }
        }

        int? phd2BacklashFloor;
        [DataMember]
        public int? PHD2BacklashFloor {
            get => phd2BacklashFloor;
            set {
                if (phd2BacklashFloor != value) {
                    phd2BacklashFloor = value;
                    RaisePropertyChanged();
                }
            }
        }

        int? phd2BacklashCeiling;
        [DataMember]
        public int? PHD2BacklashCeiling {
            get => phd2BacklashCeiling;
            set {
                if (phd2BacklashCeiling != value) {
                    phd2BacklashCeiling = value;
                    RaisePropertyChanged();
                }
            }
        }

        #region SkyGuard settings
        string skyGuardServerUrl;
        int skyGuardServerPort;
        string skyGuardPath;
        int skyGuardCallbackPort;
        bool skyGuardTimeLapsChecked;
        double skyGuardValueMaxGuiding;
        double skyGuardTimeLapsGuiding;
        bool skyGuardTimeLapsDitherChecked;
        double skyGuardValueMaxDithering;
        double skyGuardTimeLapsDithering;
        double skyGuardTimeOutGuiding;

        /// <summary>
        /// Property allowing to set the endpoint URL for SkyGuard software
        /// </summary>
        [DataMember]
        public string SkyGuardServerUrl
        {
            get => skyGuardServerUrl;
            set
            {
                if (skyGuardServerUrl != value)
                {
                    skyGuardServerUrl = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Property to set endpoint URL port for SkyGuard software
        /// </summary>
        [DataMember]
        public int SkyGuardServerPort
        {
            get => skyGuardServerPort;
            set
            {
                if (skyGuardServerPort != value)
                {
                    skyGuardServerPort = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Property allowing to set SkyGuard.exe file path
        /// </summary>
        [DataMember]
        public string SkyGuardPath
        {
            get => skyGuardPath;
            set
            {
                if (skyGuardPath != value)
                {
                    skyGuardPath = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Property to set callback port
        /// </summary>
        [DataMember]
        public int SkyGuardCallbackPort
        {
            get => skyGuardCallbackPort;
            set
            {
                if (skyGuardCallbackPort != value)
                {
                    skyGuardCallbackPort = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Property to set callback port
        /// </summary>
        [DataMember]
        public bool SkyGuardTimeLapsChecked
        {
            get => skyGuardTimeLapsChecked;
            set
            {
                if (skyGuardTimeLapsChecked != value)
                {
                    skyGuardTimeLapsChecked = value;
                    RaisePropertyChanged();
                }
            }
        }

        [DataMember]
        public double SkyGuardValueMaxGuiding
        {
            get => skyGuardValueMaxGuiding;
            set
            {
                if (skyGuardValueMaxGuiding != value)
                {
                    skyGuardValueMaxGuiding = value;
                    RaisePropertyChanged();
                }
            }
        }

        [DataMember]
        public double SkyGuardTimeLapsGuiding
        {
            get => skyGuardTimeLapsGuiding;
            set
            {
                if (skyGuardTimeLapsGuiding != value)
                {
                    skyGuardTimeLapsGuiding = value;
                    RaisePropertyChanged();
                }
            }
        }

        [DataMember]
        public bool SkyGuardTimeLapsDitherChecked {
            get => skyGuardTimeLapsDitherChecked;
            set {
                if (skyGuardTimeLapsDitherChecked != value) {
                    skyGuardTimeLapsDitherChecked = value;
                    RaisePropertyChanged();
                }
            }
        }

        [DataMember]
        public double SkyGuardValueMaxDithering {
            get => skyGuardValueMaxDithering;
            set {
                if (skyGuardValueMaxDithering != value) {
                    skyGuardValueMaxDithering = value;
                    RaisePropertyChanged();
                }
            }
        }

        [DataMember]
        public double SkyGuardTimeLapsDithering {
            get => skyGuardTimeLapsDithering;
            set {
                if (skyGuardTimeLapsDithering != value) {
                    skyGuardTimeLapsDithering = value;
                    RaisePropertyChanged();
                }
            }
        }

        [DataMember]
        public double SkyGuardTimeOutGuiding
        {
            get => skyGuardTimeOutGuiding;
            set
            {
                if (skyGuardTimeOutGuiding != value)
                {
                    skyGuardTimeOutGuiding = value;
                    RaisePropertyChanged();
                }
            }
        }
        #endregion


        private Color guideChartRightAscensionColor;        
        [DataMember]
        public Color GuideChartRightAscensionColor {
            get => guideChartRightAscensionColor;
            set {
                if (guideChartRightAscensionColor != value) {
                    guideChartRightAscensionColor = value;
                    RaisePropertyChanged();
                }
            }
        }

        private Color guideChartDeclinationColor;        
        [DataMember]
        public Color GuideChartDeclinationColor {
            get => guideChartDeclinationColor;
            set {
                if (guideChartDeclinationColor != value) {
                    guideChartDeclinationColor = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool guideChartShowCorrections;
        [DataMember]
        public bool GuideChartShowCorrections {
            get => guideChartShowCorrections;
            set {
                if (guideChartShowCorrections != value) {
                    guideChartShowCorrections = value;
                    RaisePropertyChanged();
                }
            }

        }
    }
}
