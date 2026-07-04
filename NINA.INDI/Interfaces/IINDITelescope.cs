#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.INDI.Enums;
using NINA.INDI.Model;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Interfaces {
    public interface IINDITelescope : IINDIDevice {
        AlignmentMode AlignmentMode { get; }
        double Altitude { get; }
        double ApertureArea { get; }
        double ApertureDiameter { get; }
        bool AtHome { get; }
        bool AtPark { get; }
        double Azimuth { get; }
        double Declination { get; }
        double DeclinationRate { get; set; }
        bool DoesRefraction { get; }
        double FocalLength { get; }
        double GuideRateDeclination { get; }
        double GuideRateRightAscension { get; }
        bool IsPulseGuiding { get; }
        double RightAscension { get; }
        double RightAscensionRate { get; set; }
        PierSide SideOfPier { get; }
        double SiderealTime { get; }
        double SiteElevation { get; }
        double SiteLatitude { get; }
        double SiteLongitude { get; }
        int SlewSettleTime { get; }
        bool Slewing { get; }
        double TargetDeclination { get; }
        double TargetRightAscension { get; }
        bool Tracking { get; set; }





        DateTime UTCDate { get; set; }

        void AbortSlew();
        IAxisRates AxisRates(TelescopeAxes axis);
        void ConfigureJNOW();
        void MoveAxis(TelescopeAxes axis, double rate);

        /// <summary>
        /// Direction-only manual slew: starts motion on <paramref name="axis"/> toward
        /// <paramref name="sign"/> (Primary +East/-West, Secondary +North/-South; 0 stops the
        /// axis) without changing the slew rate. The rate is controlled separately via
        /// <see cref="SetSlewRateIndex"/>/<see cref="SetSlewRateValue"/> so a chosen discrete
        /// step is not overwritten on every keepalive.
        /// </summary>
        void MoveAxisDirection(TelescopeAxes axis, int sign);

        /// <summary>
        /// Introspects the connected driver and reports how it lets a client choose the
        /// manual slew rate (discrete switch steps, a continuous numeric range, or none).
        /// Read live from the property store; safe to call repeatedly.
        /// </summary>
        SlewRateCapability GetSlewRateCapability();

        /// <summary>
        /// Selects a discrete slew rate by its <see cref="SlewRateOption.Index"/>.
        /// No-op (logged) when the driver does not expose <c>TELESCOPE_SLEW_RATE</c>.
        /// </summary>
        void SetSlewRateIndex(int index);

        /// <summary>
        /// Sets a continuous slew rate in °/s. No-op (logged) when the driver does not
        /// expose <c>TELESCOPE_MOTION_RATE</c>.
        /// </summary>
        void SetSlewRateValue(double rateDps);
        void PulseGuide(GuideDirections direction, int duration);
        Task ParkAsync(CancellationToken ct = default);
        void SetPark();
        void SetTrackingMode(TrackingMode mode);

        /// <summary>
        /// Enables sidereal tracking and waits for the driver to acknowledge before returning
        /// (no-op when already tracking). A fire-and-forget <see cref="Tracking"/> write races a
        /// goto issued immediately after - e.g. the first slew right after connect, while the
        /// mount is not yet tracking - and OnStep (and similar) reject that goto as "below the
        /// horizon limit" until the tracking transition settles. Awaiting the switch closes the race.
        /// </summary>
        Task<bool> EnableTrackingAsync(CancellationToken ct = default);
        TrackingMode GetTrackingMode();
        IList<TrackingMode> GetSupportedTrackingModes();
        Task SlewToCoordinates(double ra, double dec);
        Task SlewToCoordinatesTaskAsync(double ra, double dec, CancellationToken ct = default);
        void SlewToAltAz(double Azimuth, double Altitude);
        Task SlewToAltAzTaskAsync(double azimuth, double altitude, CancellationToken cancellationToken = default);
        void SyncToCoordinates(double ra, double dec);
        Task FindHomeAsync(CancellationToken ct = default);
        Task UnparkAsync(CancellationToken ct = default);
        bool CanMoveAxis(TelescopeAxes axis);
        PierSide DestinationSideOfPier(double ra, double dec);
    }
}