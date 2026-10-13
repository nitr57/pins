// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Mcp.Observatory;
using NINA.WPF.Base.Model;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    public ObservatoryStatus GetStatus() {
        var devices = new List<DeviceStatus>();

        foreach (var kind in Enum.GetValues<DeviceKind>()) {
            devices.Add(ReadDevice(kind));
        }

        bool running;
        IReadOnlyList<string> runningItems;

        try {
            running = sequence.IsRunning();
            runningItems = sequence.GetRunningItemNames();
        } catch (Exception ex) {
            Logger.Debug($"MCP: reading the sequence state failed: {ex.Message}");
            running = false;
            runningItems = [];
        }

        return new ObservatoryStatus(devices, running, runningItems, DateTimeOffset.Now);
    }

    public IReadOnlyList<ImageRecord> GetRecentImages(int count) {
        if (count <= 0) {
            return [];
        }

        var points = SnapshotImageHistory();

        return points
            .Where(p => p != null)
            .OrderByDescending(p => p.Id)
            .Take(count)
            .Select(ToImageRecord)
            .ToList();
    }

    private DeviceStatus ReadDevice(DeviceKind kind) {
        try {
            DeviceInfo? info = GetDeviceInfo(kind);
            var connected = info?.Connected == true;
            var properties = new Dictionary<string, object?>();

            if (connected && info != null) {
                FillProperties(kind, info, properties);
            }

            return new DeviceStatus(kind, connected, connected ? DisplayName(info) : null, properties);
        } catch (Exception ex) {
            Logger.Debug($"MCP: reading the {kind} status failed: {ex.Message}");

            return new DeviceStatus(
                kind,
                false,
                null,
                new Dictionary<string, object?> { ["error"] = ex.Message });
        }
    }

    private DeviceInfo? GetDeviceInfo(DeviceKind kind) {
        return kind switch {
            DeviceKind.Camera => cameraMediator.GetInfo(),
            DeviceKind.Mount => telescopeMediator.GetInfo(),
            DeviceKind.Focuser => focuserMediator.GetInfo(),
            DeviceKind.FilterWheel => filterWheelMediator.GetInfo(),
            DeviceKind.Guider => guiderMediator.GetInfo(),
            DeviceKind.Rotator => rotatorMediator.GetInfo(),
            DeviceKind.FlatDevice => flatDeviceMediator.GetInfo(),
            DeviceKind.Dome => domeMediator.GetInfo(),
            DeviceKind.Switch => switchMediator.GetInfo(),
            DeviceKind.Weather => weatherDataMediator.GetInfo(),
            DeviceKind.SafetyMonitor => safetyMonitorMediator.GetInfo(),
            _ => null
        };
    }

    private bool IsConnected(DeviceKind kind) {
        return GetDeviceInfo(kind)?.Connected == true;
    }

    private string? DeviceName(DeviceKind kind) {
        return DisplayName(GetDeviceInfo(kind));
    }

    private static string? DisplayName(DeviceInfo? info) {
        if (info == null) {
            return null;
        }

        return string.IsNullOrWhiteSpace(info.DisplayName) ? info.Name : info.DisplayName;
    }

    private void FillProperties(DeviceKind kind, DeviceInfo info, Dictionary<string, object?> properties) {
        switch (kind) {
            case DeviceKind.Camera:
                FillCamera(properties);
                break;

            case DeviceKind.Mount:
                FillMount(properties);
                break;

            case DeviceKind.Focuser:
                FillFocuser(properties);
                break;

            case DeviceKind.FilterWheel:
                FillFilterWheel(properties);
                break;

            case DeviceKind.Guider:
                FillGuider(properties);
                break;

            case DeviceKind.Rotator:
                FillRotator(properties);
                break;

            case DeviceKind.FlatDevice:
                FillFlatDevice(properties);
                break;

            case DeviceKind.Dome:
                FillDome(properties);
                break;

            case DeviceKind.Switch:
                FillSwitch(properties);
                break;

            case DeviceKind.Weather:
                FillWeather(properties);
                break;

            case DeviceKind.SafetyMonitor:
                properties["isSafe"] = safetyMonitorMediator.GetInfo().IsSafe;
                break;
        }
    }

    private void FillCamera(Dictionary<string, object?> p) {
        var info = cameraMediator.GetInfo();

        p["state"] = info.CameraState.ToString();
        p["isExposing"] = info.IsExposing;
        p["temperature"] = Number(info.Temperature);
        p["targetTemperature"] = info.CanSetTemperature ? Number(info.TemperatureSetPoint) : null;
        p["coolerOn"] = info.CoolerOn;
        p["coolerPower"] = Number(info.CoolerPower);
        p["coolingRampRunning"] = info.TempChangeRunning;
        p["gain"] = info.Gain;
        p["defaultGain"] = info.DefaultGain;
        p["offset"] = info.Offset;
        p["defaultOffset"] = info.DefaultOffset;
        p["binX"] = (int)info.BinX;
        p["binY"] = (int)info.BinY;
        p["pixelSizeMicrons"] = Number(info.PixelSize);
        p["sensorWidth"] = info.XSize;
        p["sensorHeight"] = info.YSize;
        p["sensorType"] = info.SensorType.ToString();
        p["exposureMinSeconds"] = Number(info.ExposureMin);
        p["exposureMaxSeconds"] = Number(info.ExposureMax);

        if (info.HasDewHeater) {
            p["dewHeaterOn"] = info.DewHeaterOn;
        }

        if (info.CanSetGain) {
            p["gainMin"] = info.GainMin;
            p["gainMax"] = info.GainMax;
        }

        if (info.CanSetOffset) {
            p["offsetMin"] = info.OffsetMin;
            p["offsetMax"] = info.OffsetMax;
        }
    }

    private void FillMount(Dictionary<string, object?> p) {
        var info = telescopeMediator.GetInfo();

        // raHours/decDegrees are J2000 like every coordinate the tools accept; the mount's own epoch (usually JNow) is
        // reported separately.
        try {
            var coordinates = info.Coordinates;

            if (coordinates != null) {
                var j2000 = coordinates.Transform(Epoch.J2000);
                p["raHours"] = Number(j2000.RA);
                p["decDegrees"] = Number(j2000.Dec);
            }
        } catch (Exception ex) {
            Logger.Debug($"MCP: J2000 transform of the mount position failed: {ex.Message}");
        }

        p["raHoursMount"] = Number(info.RightAscension);
        p["decDegreesMount"] = Number(info.Declination);
        p["mountEpoch"] = info.EquatorialSystem.ToString();

        p["altitude"] = Number(info.Altitude);
        p["azimuth"] = Number(info.Azimuth);
        p["sideOfPier"] = info.SideOfPier.ToString();
        p["tracking"] = info.TrackingEnabled;
        p["trackingMode"] = info.TrackingRate.TrackingMode.ToString();
        p["slewing"] = info.Slewing;
        p["atPark"] = info.AtPark;
        p["atHome"] = info.AtHome;
        p["canPark"] = info.CanPark;
        p["canFindHome"] = info.CanFindHome;
        p["hoursToMeridianFlip"] = Number(info.TimeToMeridianFlip);
        p["siderealTimeHours"] = Number(info.SiderealTime);
    }

    private void FillFocuser(Dictionary<string, object?> p) {
        var info = focuserMediator.GetInfo();

        p["position"] = info.Position;
        p["maxStep"] = info.MaxStep;
        p["temperature"] = Number(info.Temperature);
        p["isMoving"] = info.IsMoving;
        p["isSettling"] = info.IsSettling;
        p["temperatureCompensation"] = info.TempCompAvailable ? info.TempComp : null;
    }

    private void FillFilterWheel(Dictionary<string, object?> p) {
        var info = filterWheelMediator.GetInfo();
        var selected = info.SelectedFilter;

        p["isMoving"] = info.IsMoving;
        p["filter"] = selected?.Name;
        p["position"] = selected == null ? null : (int)selected.Position;

        var filters = profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;

        if (filters != null) {
            p["filters"] = filters
                .ToList()
                .Select(f => new Dictionary<string, object?> {
                    ["position"] = (int)f.Position,
                    ["name"] = f.Name,
                    ["focusOffset"] = f.FocusOffset
                })
                .ToList();
        }
    }

    private void FillGuider(Dictionary<string, object?> p) {
        var info = guiderMediator.GetInfo();
        var rms = info.RMSError;

        p["state"] = (guiderMediator.GetDevice() as IGuider)?.State;
        p["pixelScaleArcsecPerPixel"] = Number(info.PixelScale);

        if (rms != null) {
            p["rmsRaArcsec"] = Number(rms.RA.Arcseconds);
            p["rmsDecArcsec"] = Number(rms.Dec.Arcseconds);
            p["rmsTotalArcsec"] = Number(rms.Total.Arcseconds);
            p["peakRaArcsec"] = Number(rms.PeakRA.Arcseconds);
            p["peakDecArcsec"] = Number(rms.PeakDec.Arcseconds);
        }
    }

    private void FillRotator(Dictionary<string, object?> p) {
        var info = rotatorMediator.GetInfo();

        p["positionDegrees"] = Number(info.Position);
        p["mechanicalPositionDegrees"] = Number(info.MechanicalPosition);
        p["isMoving"] = info.IsMoving;
        p["synced"] = info.Synced;
    }

    private void FillFlatDevice(Dictionary<string, object?> p) {
        var info = flatDeviceMediator.GetInfo();

        if (info.SupportsOpenClose) {
            p["coverState"] = info.CoverState.ToString();
        }

        if (info.SupportsOnOff) {
            p["lightOn"] = info.LightOn;
        }

        p["brightness"] = info.Brightness;
        p["minBrightness"] = info.MinBrightness;
        p["maxBrightness"] = info.MaxBrightness;
    }

    private void FillDome(Dictionary<string, object?> p) {
        var info = domeMediator.GetInfo();

        p["shutter"] = info.ShutterStatus.ToString();
        p["azimuth"] = Number(info.Azimuth);
        p["altitude"] = Number(info.Altitude);
        p["slewing"] = info.Slewing;
        p["atPark"] = info.AtPark;
        p["atHome"] = info.AtHome;
        p["followingScope"] = domeMediator.IsFollowingScope;
    }

    private void FillSwitch(Dictionary<string, object?> p) {
        var info = switchMediator.GetInfo();
        var switches = new List<Dictionary<string, object?>>();

        if (info.WritableSwitches != null) {
            var index = 0;

            foreach (var item in info.WritableSwitches.ToList()) {
                switches.Add(new Dictionary<string, object?> {
                    ["index"] = index,
                    ["name"] = item.Name,
                    ["description"] = item.Description,
                    ["value"] = Number(item.Value),
                    ["min"] = Number(item.Minimum),
                    ["max"] = Number(item.Maximum),
                    ["step"] = Number(item.StepSize),
                    ["writable"] = true
                });

                index++;
            }
        }

        if (info.ReadonlySwitches != null) {
            foreach (var item in info.ReadonlySwitches.ToList()) {
                switches.Add(new Dictionary<string, object?> {
                    ["name"] = item.Name,
                    ["description"] = item.Description,
                    ["value"] = Number(item.Value),
                    ["writable"] = false
                });
            }
        }

        p["switches"] = switches;
    }

    private void FillWeather(Dictionary<string, object?> p) {
        var info = weatherDataMediator.GetInfo();

        p["temperature"] = Number(info.Temperature);
        p["humidity"] = Number(info.Humidity);
        p["dewPoint"] = Number(info.DewPoint);
        p["pressure"] = Number(info.Pressure);
        p["cloudCover"] = Number(info.CloudCover);
        p["rainRate"] = Number(info.RainRate);
        p["windSpeed"] = Number(info.WindSpeed);
        p["windGust"] = Number(info.WindGust);
        p["windDirection"] = Number(info.WindDirection);
        p["skyQuality"] = Number(info.SkyQuality);
        p["skyBrightness"] = Number(info.SkyBrightness);
        p["skyTemperature"] = Number(info.SkyTemperature);
        p["starFwhm"] = Number(info.StarFWHM);
    }

    /// <summary>
    /// <c>IImageHistoryVM.ImageHistory</c> is a plain list that NINA appends to under a private lock; copying it can
    /// race with an append, so retry a few times.
    /// </summary>
    private List<ImageHistoryPoint> SnapshotImageHistory() {
        for (var attempt = 0; attempt < 3; attempt++) {
            try {
                var history = imageHistory.ImageHistory;

                return history == null ? [] : history.ToList();
            } catch (InvalidOperationException) {
                Thread.Sleep(5);
            } catch (ArgumentException) {
                Thread.Sleep(5);
            }
        }

        return [];
    }

    private static ImageRecord ToImageRecord(ImageHistoryPoint point) {
        return new ImageRecord(
            point.Id,
            new DateTimeOffset(point.dateTime),
            point.Type,
            string.IsNullOrEmpty(point.Filter) ? null : point.Filter,
            Number(point.Duration),
            Positive(point.HFR),
            point.Stars,
            Number(point.Mean),
            Number(point.Median),
            Number(point.StDev),
            Number(point.Temperature),
            Positive(point.Rms),
            string.IsNullOrEmpty(point.Target?.Name) ? null : point.Target.Name,
            string.IsNullOrEmpty(point.LocalPath) ? null : point.LocalPath,
            Positive(point.FWHMArcseconds),
            Positive(point.Eccentricity),
            point.FocuserPosition > 0 ? point.FocuserPosition : null);
    }

    /// <summary>NINA uses 0/NaN for "not measured"; report those as null.</summary>
    private static double? Positive(double value) {
        return double.IsFinite(value) && value > 0 ? value : null;
    }
}
