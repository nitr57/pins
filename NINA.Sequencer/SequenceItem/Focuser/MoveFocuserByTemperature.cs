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
using NINA.Core.Model;
using NINA.Sequencer.Validations;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Sequencer.Generators;
using NINA.Sequencer.Logic;

namespace NINA.Sequencer.SequenceItem.Focuser {

    [ExportMetadata("Name", "Lbl_SequenceItem_Focuser_MoveFocuserByTemperature_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Focuser_MoveFocuserByTemperature_Description")]
    [ExportMetadata("Icon", "MoveFocuserByTemperatureSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Focuser")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    [UsesExpressions(GenerateValidation = true)]

    public partial class MoveFocuserByTemperature : SequenceItem, IValidatable, IFocuserConsumer {

        [ImportingConstructor]
        public MoveFocuserByTemperature(IFocuserMediator focuserMediator) {
            this.focuserMediator = focuserMediator;
        }

        private MoveFocuserByTemperature(MoveFocuserByTemperature cloneMe) : this(cloneMe.focuserMediator) {
            CopyMetaData(cloneMe);
        }

        partial void AfterClone(MoveFocuserByTemperature clone) {
            Absolute = Absolute;
        }

        private IFocuserMediator focuserMediator;

        [IsExpression (Default = 1)]
        public partial double Slope { get; set; }

        private bool absolute = true;

        [JsonProperty]
        public bool Absolute {
            get => absolute;
            set {
                absolute = value;
                RaisePropertyChanged();
            }
        }

        [IsExpression (Default = 0)]
        public partial double Intercept { get; set; }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var info = focuserMediator.GetInfo();
            Task<int> result;

            if (absolute) {
                double position = Slope * info.Temperature + Intercept;
                Logger.Info($"Moving Focuser By Temperature - Slope {Slope} * Temperature {info.Temperature} °C + Intercept {Intercept} = Position {position}");
                result = focuserMediator.MoveFocuser((int)position, token);
            } else {
                Logger.Info($"Moving Focuser By Temperature Relative - Slope {Slope}");
                result = focuserMediator.MoveFocuserByTemperatureRelative(info.Temperature, Slope, token);
            }

            return result;
        }

        public override void AfterParentChanged() {
            base.AfterParentChanged();
            Validate();
        }

        partial void ValidateAdditional(IList<string> issues) {
            var info = focuserMediator.GetInfo();
            if (!info.Connected) {
                issues.Add(Loc.Instance["LblFocuserNotConnected"]);
            } else {
                if (double.IsNaN(info.Temperature)) {
                    issues.Add(Loc.Instance["Lbl_SequenceItem_Focuser_MoveFocuserByTemperature_Validation_NoTemperature"]);
                }
            }
        }

        public string MiniDescription => $"{Slope} * " + (absolute ? $"T + {Intercept}" : "DeltaT");

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(MoveFocuserByTemperature)}, Slope: {Slope}" + (absolute ? $", Intercept {Intercept}" : " (Relative mode)");
        }

        public void UpdateDeviceInfo(Equipment.Equipment.MyFocuser.FocuserInfo deviceInfo) {
            ;
        }

        public void Dispose() {
        }

        public void UpdateEndAutoFocusRun(AutoFocusInfo info) {
            Logger.Info($"Autofocus notification received - Temperature {info.Temperature}");
        }

        public void UpdateUserFocused(Equipment.Equipment.MyFocuser.FocuserInfo info) {
            Logger.Info($"User Focused notification received - Temperature {info.Temperature}");
        }
    }
}