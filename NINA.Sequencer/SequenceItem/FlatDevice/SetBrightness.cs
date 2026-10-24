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
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Locale;
using NINA.Sequencer.Generators;
using NINA.Sequencer.Logic;

namespace NINA.Sequencer.SequenceItem.FlatDevice {

    [ExportMetadata("Name", "Lbl_SequenceItem_FlatDevice_SetBrightness_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_FlatDevice_SetBrightness_Description")]
    [ExportMetadata("Icon", "BrightnessSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_FlatDevice")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    [UsesExpressions(GenerateValidation = true)]

    public partial class SetBrightness : SequenceItem, IValidatable {

        [ImportingConstructor]
        public SetBrightness(IFlatDeviceMediator flatDeviceMediator) {
            this.flatDeviceMediator = flatDeviceMediator;
        }

        private SetBrightness(SetBrightness cloneMe) : this(cloneMe.flatDeviceMediator) {
            CopyMetaData(cloneMe);
        }

        private IFlatDeviceMediator flatDeviceMediator;

        [IsExpression]
        public partial int Brightness { get; set; }

        public int MinBrightness {
            get => field;
            set {
                field = value;
                RaisePropertyChanged();
            }
        }

        public int MaxBrightness {
            get => field;
            set {
                field = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            await flatDeviceMediator.SetBrightness(Brightness, progress, token);

            var brightnessState = flatDeviceMediator.GetInfo().Brightness;
            MinBrightness = flatDeviceMediator.GetInfo().MinBrightness;
            MaxBrightness = flatDeviceMediator.GetInfo().MaxBrightness;

            // we shouldn't consider the flatdevice bringing the brightness up to to min or down to the max a failure
            if (Brightness < MinBrightness && brightnessState == MinBrightness) {
                return;
            }

            if (Brightness > MaxBrightness && brightnessState == MaxBrightness) {
                return;
            }

            if (brightnessState != Brightness) {
                throw new SequenceEntityFailedException($"Failed to set brightness. Current brightness: {brightnessState}");
            }
        }

        partial void PrepareExpressionValidation() {
            MinBrightness = flatDeviceMediator.GetInfo().MinBrightness;
            MaxBrightness = flatDeviceMediator.GetInfo().MaxBrightness;
        }

        partial void ValidateAdditional(IList<string> issues) {
            var info = flatDeviceMediator.GetInfo();
            if (!info.Connected) {
                issues.Add(Loc.Instance["LblFlatDeviceNotConnected"]);
            } else {
                if (!info.SupportsOnOff) {
                    issues.Add(Loc.Instance["LblFlatDeviceCannotControlBrightness"]);
                }
            }
        }

        public override void AfterParentChanged() {
            base.AfterParentChanged();
            Validate();
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SetBrightness)}, {nameof(Brightness)}: {Brightness}";
        }
    }
}