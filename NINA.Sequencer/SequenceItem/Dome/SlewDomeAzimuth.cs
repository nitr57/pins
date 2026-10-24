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
using NINA.Sequencer.Generators;
using NINA.Sequencer.Logic;

namespace NINA.Sequencer.SequenceItem.Dome {

    [ExportMetadata("Name", "Lbl_SequenceItem_Dome_SetDomeAzimuth_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Dome_SetDomeAzimuth_Description")]
    [ExportMetadata("Icon", "RotatorSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Dome")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    [UsesExpressions(GenerateValidation = true)]

    public partial class SlewDomeAzimuth : SequenceItem, IValidatable {

        [ImportingConstructor]
        public SlewDomeAzimuth(IDomeMediator domeMediator) {
            this.domeMediator = domeMediator;
        }

        private SlewDomeAzimuth(SlewDomeAzimuth cloneMe) : this(cloneMe.domeMediator) {
            CopyMetaData(cloneMe);
        }

        private IDomeMediator domeMediator;

        [IsExpression (Default = 0, Range = [0, 359.99])]
        public partial double AzimuthDegrees { get; set; }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            return domeMediator.SlewToAzimuth(AzimuthDegrees, token);
        }

        partial void ValidateAdditional(IList<string> issues) {
            var domeInfo = domeMediator.GetInfo();
            if (!domeInfo.Connected) {
                issues.Add(Loc.Instance["LblDomeNotConnected"]);
            } else {
                if (!domeInfo.CanSetAzimuth) {
                    issues.Add(Loc.Instance["LblDomeCannotSetAzimuth"]);
                }
            }
        }

        public override void AfterParentChanged() {
            Validate();
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SlewDomeAzimuth)}, Azimuth: {AzimuthDegrees}°";
        }
    }
}