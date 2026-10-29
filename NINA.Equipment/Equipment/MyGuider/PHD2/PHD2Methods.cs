#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Text.Json.Nodes;

namespace NINA.Equipment.Equipment.MyGuider.PHD2 {

    public abstract class Phd2Method {

        [JsonProperty(PropertyName = "id")]
        public string Id { get; } = Guid.NewGuid().ToString();

        [JsonProperty(PropertyName = "method")]
        public abstract string Method { get; }
    }

    public abstract class Phd2Method<T> : Phd2Method {

        [JsonProperty(PropertyName = "params")]
        public T Parameters { get; set; }
    }

    public class Phd2Guide : Phd2Method<Phd2GuideParameter> {
        public override string Method => "guide";
    }

    public class Phd2GuideParameter {

        [JsonProperty(PropertyName = "settle")]
        public Phd2Settle Settle { get; set; }

        [JsonProperty(PropertyName = "recalibrate")]
        public bool Recalibrate { get; set; }

        [JsonProperty(PropertyName = "roi")]
        public int[] Roi { get; set; }
    }

    public class Phd2Dither : Phd2Method<Phd2DitherParameter> {
        public override string Method => "dither";
    }

    public class Phd2DitherParameter {

        [JsonProperty(PropertyName = "amount")]
        public double Amount { get; set; }

        [JsonProperty(PropertyName = "raOnly")]
        public bool RaOnly { get; set; }

        [JsonProperty(PropertyName = "settle")]
        public Phd2Settle Settle { get; set; }
    }

    public class Phd2Settle {

        [JsonProperty(PropertyName = "pixels")]
        public double Pixels { get; set; }

        [JsonProperty(PropertyName = "time")]
        public int Time { get; set; }

        [JsonProperty(PropertyName = "timeout")]
        public int Timeout { get; set; }
    }

    public class Phd2GetCameraFrameSize : Phd2Method {
        public override string Method => "get_camera_frame_size";
    }

    public class Phd2FindStar : Phd2Method<Phd2FindStarParameter> {
        public override string Method => "find_star";
    }

    public class Phd2FindStarParameter {

        [JsonProperty(PropertyName = "roi")]
        public int[] Roi { get; set; }
    }

    public class Phd2Loop : Phd2Method {
        public override string Method => "loop";
    }

    public class Phd2StopCapture : Phd2Method {
        public override string Method => "stop_capture";
    }

    public class Phd2GetStarImage : Phd2Method {
        public override string Method => "get_star_image";
    }

    public class Phd2GetPixelScale : Phd2Method {
        public override string Method => "get_pixel_scale";
    }

    public class Phd2GetExposure : Phd2Method {
        public override string Method => "get_exposure";
    }

    public class Phd2GetAppState : Phd2Method {
        public override string Method => "get_app_state";
    }

    public class Phd2Pause : Phd2Method<Array> {
        public override string Method => "set_paused";
    }

    public class Phd2GetConnected : Phd2Method {
        public override string Method => "get_connected";
    }

    public class Phd2SetConnected : Phd2Method<Array> {
        public override string Method => "set_connected";
    }

    public class Phd2ClearCalibration : Phd2Method<Array> {
        public override string Method => "clear_calibration";
    }

    public class Phd2GetProfile : Phd2Method {
        public override string Method => "get_profile";
    }

    public class Phd2GetProfiles : Phd2Method<Array> {
        public override string Method => "get_profiles";
    }

    public class Phd2GetLockPosition : Phd2Method {
        public override string Method => "get_lock_position";
    }

    public class Phd2SetProfile : Phd2Method<Array> {
        public override string Method => "set_profile";
    }

    public class Phd2GetAlgoParamNames : Phd2Method<Array> {
        public override string Method => "get_algo_param_names";
    }

    public class Phd2GetAlgoParam : Phd2Method<Array> {
        public override string Method => "get_algo_param";
    }

    public class Phd2GetCalibrated : Phd2Method {
        public override string Method => "get_calibrated";
    }

    public class Phd2GetCalibrationData : Phd2Method<Array> {
        public override string Method => "get_calibration_data";
    }

    public class Phd2GetCoolerStatus : Phd2Method {
        public override string Method => "get_cooler_status";
    }

    public class Phd2GetCurrentEquipment : Phd2Method {
        public override string Method => "get_current_equipment";
    }

    public class Phd2GetDecGuideMode : Phd2Method {
        public override string Method => "get_dec_guide_mode";
    }

    public class Phd2GetExposureDurations : Phd2Method {
        public override string Method => "get_exposure_durations";
    }

    public class Phd2GetGuideOutputEnabled : Phd2Method {
        public override string Method => "get_guide_output_enabled";
    }

    public class Phd2GetLockShiftEnabled : Phd2Method {
        public override string Method => "get_lock_shift_enabled";
    }

    public class Phd2GetLockShiftParams : Phd2Method {
        public override string Method => "get_lock_shift_params";
    }

    public class Phd2GetPaused : Phd2Method {
        public override string Method => "get_paused";
    }

    public class Phd2GetSearchRegion : Phd2Method {
        public override string Method => "get_search_region";
    }

    public class Phd2SetSearchRegion : Phd2Method<Array> {
        public override string Method => "set_search_region";
    }

    public class Phd2GetMaxRADuration : Phd2Method {
        public override string Method => "get_max_ra_duration";
    }

    public class Phd2SetMaxRADuration : Phd2Method<Array> {
        public override string Method => "set_max_ra_duration";
    }

    public class Phd2GetMaxDecDuration : Phd2Method {
        public override string Method => "get_max_dec_duration";
    }

    public class Phd2SetMaxDecDuration : Phd2Method<Array> {
        public override string Method => "set_max_dec_duration";
    }

    public class Phd2GetGuideAlgorithmRA : Phd2Method {
        public override string Method => "get_guide_algorithm_ra";
    }

    public class Phd2SetGuideAlgorithmRA : Phd2Method<Array> {
        public override string Method => "set_guide_algorithm_ra";
    }

    public class Phd2GetGuideAlgorithmDec : Phd2Method {
        public override string Method => "get_guide_algorithm_dec";
    }

    public class Phd2SetGuideAlgorithmDec : Phd2Method<Array> {
        public override string Method => "set_guide_algorithm_dec";
    }

    public class Phd2GetDitherScale : Phd2Method {
        public override string Method => "get_dither_scale";
    }

    public class Phd2SetDitherScale : Phd2Method<Array> {
        public override string Method => "set_dither_scale";
    }

    public class Phd2GetDitherRAOnly : Phd2Method {
        public override string Method => "get_dither_ra_only";
    }

    public class Phd2SetDitherRAOnly : Phd2Method<Array> {
        public override string Method => "set_dither_ra_only";
    }

    public class Phd2GetDitherMode : Phd2Method {
        public override string Method => "get_dither_mode";
    }

    public class Phd2SetDitherMode : Phd2Method<Array> {
        public override string Method => "set_dither_mode";
    }

    public class Phd2GetCCDTemperature : Phd2Method {
        public override string Method => "get_ccd_temperature";
    }

    public class Phd2GetUseSubFrames : Phd2Method {
        public override string Method => "get_use_subframes";
    }

    public class Phd2GetNoiseReductionMethod : Phd2Method {
        public override string Method => "get_noise_reduction_method";
    }

    public class Phd2SetNoiseReductionMethod : Phd2Method<Array> {
        public override string Method => "set_noise_reduction_method";
    }

    public class Phd2GetCameraGain : Phd2Method {
        public override string Method => "get_camera_gain";
    }

    public class Phd2SetCameraGain : Phd2Method<Array> {
        public override string Method => "set_camera_gain";
    }

    public class Phd2GetCameraBinning : Phd2Method {
        public override string Method => "get_camera_binning";
    }

    public class Phd2SetCameraBinning : Phd2Method<Array> {
        public override string Method => "set_camera_binning";
    }

    public class Phd2GetCameraUseSubframes : Phd2Method {
        public override string Method => "get_camera_use_subframes";
    }

    public class Phd2SetCameraUseSubframes : Phd2Method<Array> {
        public override string Method => "set_camera_use_subframes";
    }

    public class Phd2GetFocalLength : Phd2Method {
        public override string Method => "get_focal_length";
    }

    public class Phd2SetFocalLength : Phd2Method<Array> {
        public override string Method => "set_focal_length";
    }

    public class Phd2GetAutoRestoreCalibration : Phd2Method {
        public override string Method => "get_auto_restore_calibration";
    }

    public class Phd2SetAutoRestoreCalibration : Phd2Method<Array> {
        public override string Method => "set_auto_restore_calibration";
    }

    public class Phd2GetAssumeDecOrthogonal : Phd2Method {
        public override string Method => "get_assume_dec_orthogonal";
    }

    public class Phd2SetAssumeDecOrthogonal : Phd2Method<Array> {
        public override string Method => "set_assume_dec_orthogonal";
    }

    public class Phd2GetUseDecCompensation : Phd2Method {
        public override string Method => "get_use_dec_compensation";
    }

    public class Phd2SetUseDecCompensation : Phd2Method<Array> {
        public override string Method => "set_use_dec_compensation";
    }

    public class Phd2GetReverseDecOnFlip : Phd2Method {
        public override string Method => "get_reverse_dec_on_flip";
    }

    public class Phd2SetReverseDecOnFlip : Phd2Method<Array> {
        public override string Method => "set_reverse_dec_on_flip";
    }

    public class Phd2GetFastRecenterEnabled : Phd2Method {
        public override string Method => "get_fast_recenter_enabled";
    }

    public class Phd2SetFastRecenterEnabled : Phd2Method<Array> {
        public override string Method => "set_fast_recenter_enabled";
    }

    public class Phd2GetMinStarHFD : Phd2Method {
        public override string Method => "get_min_star_hfd";
    }

    public class Phd2SetMinStarHFD : Phd2Method<Array> {
        public override string Method => "set_min_star_hfd";
    }

    public class Phd2GetMaxStarHFD : Phd2Method {
        public override string Method => "get_max_star_hfd";
    }

    public class Phd2SetMaxStarHFD : Phd2Method<Array> {
        public override string Method => "set_max_star_hfd";
    }

    public class Phd2GetBeepForLostStar : Phd2Method {
        public override string Method => "get_beep_for_lost_star";
    }

    public class Phd2SetBeepForLostStar : Phd2Method<Array> {
        public override string Method => "set_beep_for_lost_star";
    }

    public class Phd2GetMassChangeThresholdEnabled : Phd2Method {
        public override string Method => "get_mass_change_threshold_enabled";
    }

    public class Phd2SetMassChangeThresholdEnabled : Phd2Method<Array> {
        public override string Method => "set_mass_change_threshold_enabled";
    }

    public class Phd2GetMassChangeThreshold : Phd2Method {
        public override string Method => "get_mass_change_threshold";
    }

    public class Phd2SetMassChangeThreshold : Phd2Method<Array> {
        public override string Method => "set_mass_change_threshold";
    }

    public class Phd2GetUseMultipleStars : Phd2Method {
        public override string Method => "get_use_multiple_stars";
    }

    public class Phd2SetUseMultipleStars : Phd2Method<Array> {
        public override string Method => "set_use_multiple_stars";
    }

    public class Phd2SetAlgoParam : Phd2Method<Array> {
        public override string Method => "set_algo_param";
    }

    public class Phd2SetDecGuideMode : Phd2Method<Array> {
        public override string Method => "set_dec_guide_mode";
    }

    public class Phd2SetExposure : Phd2Method<Array> {
        public override string Method => "set_exposure";
    }

    public class Phd2SetGuideOutputEnabled : Phd2Method<Array> {
        public override string Method => "set_guide_output_enabled";
    }

    public class Phd2SetLockPosition : Phd2Method<Array> {
        public override string Method => "set_lock_position";
    }

    public class Phd2SetLockShiftEnabled : Phd2Method<Array> {
        public override string Method => "set_lock_shift_enabled";
    }

    public class Phd2SetLockShiftParams : Phd2Method<Phd2SetLockShiftParamsParameter> {
        public override string Method => "set_lock_shift_params";
    }

    public class Phd2SetLockShiftParamsParameter {

        [JsonProperty(PropertyName = "rate")]
        public double[] Rate { get; set; }

        [JsonProperty(PropertyName = "units")]
        public string Units { get; set; }

        [JsonProperty(PropertyName = "axes")]
        public string Axes { get; set; }
    }

    public class Phd2CaptureSingleFrame : Phd2Method<Phd2CaptureSingleFrameParameter> {
        public override string Method => "capture_single_frame";
    }

    public class Phd2CaptureSingleFrameParameter {

        [JsonProperty(PropertyName = "exposure")]
        public int Exposure { get; set; }

        [JsonProperty(PropertyName = "subframe")]
        public int[] Subframe { get; set; }
    }

    public class Phd2FlipCalibration : Phd2Method {
        public override string Method => "flip_calibration";
    }

    public class Phd2GuidePulse : Phd2Method<Array> {
        public override string Method => "guide_pulse";
    }

    public class Phd2SaveImage : Phd2Method {
        public override string Method => "save_image";
    }

    public class Phd2Shutdown : Phd2Method {
        public override string Method => "shutdown";
    }

    public class Phd2GetSelectedMount : Phd2Method {
        public override string Method => "get_selected_mount";
    }

    public class Phd2GetSelectedINDIMountDriver : Phd2Method {
        public override string Method => "get_selected_indi_mount_driver";
    }

    public class Phd2SetSelectedMount : Phd2Method<JObject> {
        public override string Method => "set_selected_mount";
    }

    public class Phd2SetSelectedINDIMountDriver : Phd2Method<JObject> {
        public override string Method => "set_selected_indi_mount_driver";
    }

    public class Phd2GetSelectedCamera : Phd2Method {
        public override string Method => "get_selected_camera";
    }

    public class Phd2GetSelectedCameraId : Phd2Method {
        public override string Method => "get_selected_camera_id";
    }

    public class Phd2GetSelectedINDICameraDriver : Phd2Method {
        public override string Method => "get_selected_indi_camera_driver";
    }

    public class Phd2SetSelectedCamera : Phd2Method<JObject> {
        public override string Method => "set_selected_camera";
    }

    public class Phd2SetSelectedCameraId : Phd2Method<JObject> {
        public override string Method => "set_selected_camera_id";
    }

    public class Phd2GetCameraBitDepth : Phd2Method {
        public override string Method => "get_camera_bitdepth";
    }

    public class Phd2SetCameraBitDepth : Phd2Method<JObject> {
        public override string Method => "set_camera_bitdepth";
    }

    public class Phd2SetSelectedINDICameraDriver : Phd2Method<JObject> {
        public override string Method => "set_selected_indi_camera_driver";
    }

    public class PhdMethodResponse {
        public string jsonrpc;
        public PhdError error;
        public string id;
    }

    public class GenericPhdMethodResponse : PhdMethodResponse {
        public object result;
    }

    public class IntegerPhdMethodResponse : PhdMethodResponse {
        public int result;
    }

    public class BooleanPhdMethodResponse : PhdMethodResponse {
        public bool result;
    }

    public class GetCameraFrameSizeResponse : PhdMethodResponse {
        public int[] result;
    }

    public class PhdImageResult {
        public int frame;
        public int width;
        public int height;
        public double[] star_pos;
        public string pixels;
    }

    public class Phd2ProfileResponse {
        public int id;
        public string name { get; set; }
    }

    public class GetProfileResponse : PhdMethodResponse {
        public Phd2ProfileResponse result;
    }

    public class GetProfilesResponse : PhdMethodResponse {
        public Phd2ProfileResponse[] result;
    }

    public class GetLockPositionResponse : PhdMethodResponse {
        public float[] result;
    }

    public class GetLockShiftParamsResponse : PhdMethodResponse {
        public LockShiftParams result;
    }

    public class GetExposureResponse : PhdMethodResponse {
        public int result;
    }

    public class StringPhdMethodResponse : PhdMethodResponse {
        public string result;
    }

    public class StringArrayPhdMethodResponse : PhdMethodResponse {
        public string[] result;
    }

    public class DoublePhdMethodResponse : PhdMethodResponse {
        public double result;
    }

    public class Phd2GetCalibrationStep : Phd2Method {
        public override string Method => "get_calibration_step";
    }

    public class Phd2SetCalibrationStep : Phd2Method<Array> {
        public override string Method => "set_calibration_step";
    }

    public class Phd2GetCalibrationDistance : Phd2Method {
        public override string Method => "get_calibration_distance";
    }

    public class Phd2SetCalibrationDistance : Phd2Method<Array> {
        public override string Method => "set_calibration_distance";
    }

    public class Phd2GetTimeLapse : Phd2Method {
        public override string Method => "get_time_lapse";
    }

    public class Phd2SetTimeLapse : Phd2Method<Array> {
        public override string Method => "set_time_lapse";
    }

    public class Phd2GetVariableDelaySettings : Phd2Method {
        public override string Method => "get_variable_delay_settings";
    }

    public class Phd2SetVariableDelaySettingsParam {
        [JsonProperty(PropertyName = "Enabled")]
        public bool Enabled { get; set; }
        [JsonProperty(PropertyName = "ShortDelaySeconds")]
        public int ShortDelaySeconds { get; set; }
        [JsonProperty(PropertyName = "LongDelaySeconds")]
        public int LongDelaySeconds { get; set; }
    }

    public class Phd2SetVariableDelaySettings : Phd2Method<Phd2SetVariableDelaySettingsParam> {
        public override string Method => "set_variable_delay_settings";
    }

    public class VariableDelayResult {
        [JsonProperty(PropertyName = "Enabled")]
        public bool Enabled { get; set; }
        [JsonProperty(PropertyName = "ShortDelaySeconds")]
        public int ShortDelaySeconds { get; set; }
        [JsonProperty(PropertyName = "LongDelaySeconds")]
        public int LongDelaySeconds { get; set; }
    }

    public class VariableDelaySettingsResponse : PhdMethodResponse {
        public VariableDelayResult result { get; set; }
    }

    public class Phd2GetAfMinStarSnr : Phd2Method {
        public override string Method => "get_af_min_star_snr";
    }

    public class Phd2SetAfMinStarSnr : Phd2Method<Array> {
        public override string Method => "set_af_min_star_snr";
    }

    public class Phd2GetAutoSelectDownsample : Phd2Method {
        public override string Method => "get_auto_select_downsample";
    }

    public class Phd2SetAutoSelectDownsample : Phd2Method<Array> {
        public override string Method => "set_auto_select_downsample";
    }

    public class Phd2GetSaturationByADU : Phd2Method {
        public override string Method => "get_saturation_by_adu";
    }

    public class Phd2GetSaturationADUValue : Phd2Method {
        public override string Method => "get_saturation_adu_value";
    }

    public class Phd2SetSaturationByADUParam {
        [JsonProperty(PropertyName = "by_adu")]
        public bool ByADU { get; set; }
        [JsonProperty(PropertyName = "adu_value", NullValueHandling = NullValueHandling.Ignore)]
        public int? ADUValue { get; set; }
    }

    public class Phd2SetSaturationByADU : Phd2Method<Phd2SetSaturationByADUParam> {
        public override string Method => "set_saturation_by_adu";
    }

    public class Phd2SetSaturationADUValue : Phd2Method<Array> {
        public override string Method => "set_saturation_adu_value";
    }

    public class Phd2GetBacklashComp : Phd2Method {
        public override string Method => "get_backlash_comp";
    }

    public class Phd2SetBacklashCompParam {
        [JsonProperty(PropertyName = "enabled", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Enable { get; set; }
        [JsonProperty(PropertyName = "pulseWidth", NullValueHandling = NullValueHandling.Ignore)]
        public int? Pulse { get; set; }
        [JsonProperty(PropertyName = "floor", NullValueHandling = NullValueHandling.Ignore)]
        public int? Floor { get; set; }
        [JsonProperty(PropertyName = "ceiling", NullValueHandling = NullValueHandling.Ignore)]
        public int? Ceiling { get; set; }
    }

    public class Phd2SetBacklashComp : Phd2Method<Phd2SetBacklashCompParam> {
        public override string Method => "set_backlash_comp";
    }

    public class LockShiftParams {

        [JsonProperty(PropertyName = "enabled")]
        public bool Enabled { get; set; }

        [JsonProperty(PropertyName = "rate")]
        public float[] Rate { get; set; }

        [JsonProperty(PropertyName = "units")]
        public string Units { get; set; }

        [JsonProperty(PropertyName = "axes")]
        public string Axes { get; set; }
    }

    public class PhdError {
        public int code;
        public string message;
    }
}
