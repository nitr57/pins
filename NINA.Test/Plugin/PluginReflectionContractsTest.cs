// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Messaging;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Mediator;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.ViewModel.FramingAssistant;
using NINA.ViewModel.Sequencer;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Mediator;
using NINA.WPF.Base.ViewModel;
using NINA.WPF.Base.ViewModel.Equipment.Camera;
using NINA.WPF.Base.ViewModel.Equipment.Dome;
using NINA.WPF.Base.ViewModel.Equipment.FilterWheel;
using NINA.WPF.Base.ViewModel.Equipment.FlatDevice;
using NINA.WPF.Base.ViewModel.Equipment.Focuser;
using NINA.WPF.Base.ViewModel.Equipment.Guider;
using NINA.WPF.Base.ViewModel.Equipment.Rotator;
using NINA.WPF.Base.ViewModel.Equipment.SafetyMonitor;
using NINA.WPF.Base.ViewModel.Equipment.Switch;
using NINA.WPF.Base.ViewModel.Equipment.Telescope;
using NINA.WPF.Base.ViewModel.Equipment.WeatherData;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NINA.Test.Plugin {

    /// <summary>
    /// The Advanced API (NINA.Plugins/ninaAPI) and the Touch-N-Stars plugin (NINA.Plugins/Touch-N-Stars) reach NINA
    /// members by name through reflection. A rename or an added overload in NINA still compiles and only breaks the
    /// plugin at runtime, so each test repeats a plugin's lookup and fails instead.
    /// <para>
    /// Not covered here, since NINA.Test does not reference those plugins: members of other plugins the TNS plugin
    /// reflects on (HocusFocus <c>nextHistoryId</c>, <c>InspectorVM.analyzeTask</c>; TenMicron's <c>mount</c>,
    /// <c>mountCommander</c>, <c>LoadModelNames</c>, <c>alignmentModelLoadTask</c>, <c>disconnectCts</c>,
    /// <c>LoadAlignmentModel</c> and its mediators' <c>handler</c>; NINA.PINS <c>Reset</c>/<c>Reboot</c>), optional
    /// device members the plugins only use when present (<c>Backlash</c>, <c>HeaterPower</c>, <c>ResetPosition</c>, …),
    /// the runtime type of the device view models' <c>RescanDevicesCommand</c> value (ninaAPI needs an
    /// <c>AsyncCommand&lt;bool&gt;</c> or <c>AsyncRelayCommand&lt;bool&gt;</c>), and TNS DialogManager's <c>OnClick</c>, which
    /// only runs on WPF windows and does not exist on the compat button.
    /// </para>
    /// </summary>
    [TestFixture]
    public class PluginReflectionContractsTest {

        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private static IEnumerable<TestCaseData> DeviceMediators() {
            // ninaAPI Equipment/Connection.cs GetDeviceVM, Dome.cs, Mount.cs, Rotator.cs, Guider.cs, FlatDevice.cs
            yield return new TestCaseData(typeof(CameraMediator), typeof(CameraVM));
            yield return new TestCaseData(typeof(DomeMediator), typeof(DomeVM));
            yield return new TestCaseData(typeof(FilterWheelMediator), typeof(FilterWheelVM));
            yield return new TestCaseData(typeof(FlatDeviceMediator), typeof(FlatDeviceVM));
            yield return new TestCaseData(typeof(FocuserMediator), typeof(FocuserVM));
            yield return new TestCaseData(typeof(GuiderMediator), typeof(GuiderVM));
            yield return new TestCaseData(typeof(TelescopeMediator), typeof(TelescopeVM));
            yield return new TestCaseData(typeof(RotatorMediator), typeof(RotatorVM));
            yield return new TestCaseData(typeof(SafetyMonitorMediator), typeof(SafetyMonitorVM));
            yield return new TestCaseData(typeof(SwitchMediator), typeof(SwitchVM));
            yield return new TestCaseData(typeof(WeatherDataMediator), typeof(WeatherDataVM));
        }

        private static IEnumerable<Type> DeviceVMs() {
            return DeviceMediators().Select(data => (Type)data.Arguments[1]!).Append(typeof(GuideCameraVM));
        }

        [TestCaseSource(nameof(DeviceMediators))]
        public void Mediator_HandlerField_HoldsTheDeviceVM(Type mediatorType, Type vmType) {
            var field = mediatorType.GetField("handler", PrivateInstance);

            Assert.That(field, Is.Not.Null, $"ninaAPI reads {mediatorType.Name}.handler to reach the {vmType.Name}");
            Assert.That(
                field!.FieldType.IsAssignableFrom(vmType),
                Is.True,
                $"ninaAPI casts {mediatorType.Name}.handler ({field.FieldType.Name}) to {vmType.Name}");
        }

        [Test]
        public void GuideCamera_IsReachedThroughTheCameraMediatorsHandlerField() {
            // ninaAPI Connection.cs reads typeof(CameraMediator)'s handler field on the guide camera mediator and casts it
            // to CameraVM.
            Assert.That(typeof(CameraMediator).IsAssignableFrom(typeof(GuideCameraMediator)), Is.True);
            Assert.That(typeof(CameraVM).IsAssignableFrom(typeof(GuideCameraVM)), Is.True);
        }

        [Test]
        public void ApplicationMediator_HandlerField_IsTheApplicationVM() {
            // ninaAPI Application/Application.cs
            var field = typeof(ApplicationMediator).GetField("handler", PrivateInstance);

            Assert.That(field, Is.Not.Null);
            Assert.That(field!.FieldType, Is.EqualTo(typeof(IApplicationVM)));
        }

        [TestCaseSource(nameof(DeviceVMs))]
        public void DeviceVM_ConnectDisconnectAndCommands_ResolveByName(Type vmType) {
            // ninaAPI Connection.cs: GetMethod("Connect") / ("Disconnect") without parameter types, invoked with no
            // arguments; an overload would make the lookup throw AmbiguousMatchException for every connect.
            MethodInfo? connect = null;
            MethodInfo? disconnect = null;

            Assert.DoesNotThrow(() => connect = vmType.GetMethod("Connect"), $"{vmType.Name}.Connect must not be overloaded");
            Assert.DoesNotThrow(() => disconnect = vmType.GetMethod("Disconnect"), $"{vmType.Name}.Disconnect must not be overloaded");

            Assert.That(connect, Is.Not.Null);
            Assert.That(connect!.GetParameters(), Is.Empty);
            Assert.That(connect.ReturnType, Is.EqualTo(typeof(Task<bool>)), "ninaAPI awaits it as Task<bool>");
            Assert.That(disconnect, Is.Not.Null);
            Assert.That(disconnect!.GetParameters(), Is.Empty);
            Assert.That(typeof(Task).IsAssignableFrom(disconnect.ReturnType), Is.True, "ninaAPI awaits it as Task");

            var cancel = vmType.GetProperty("CancelConnectCommand", BindingFlags.Public | BindingFlags.Instance);
            var rescan = vmType.GetProperty("RescanDevicesCommand", BindingFlags.Public | BindingFlags.Instance);

            Assert.That(cancel, Is.Not.Null);
            Assert.That(typeof(ICommand).IsAssignableFrom(cancel!.PropertyType), Is.True);
            Assert.That(rescan, Is.Not.Null);

            // ninaAPI runs the value as an AsyncCommand<bool> (what the view models create) or casts it to
            // AsyncRelayCommand<bool>; the value itself is only known at runtime (see the class comment).
#pragma warning disable CS0618 // The obsolete type is the one ninaAPI tests for.
            Assert.That(rescan!.PropertyType.IsAssignableFrom(typeof(AsyncCommand<bool>)), Is.True);
#pragma warning restore CS0618
        }

        [TestCase(typeof(RotatorVM))]
        [TestCase(typeof(FlatDeviceVM))]
        public void DeviceVM_GetDevice_IsPublicWithoutParameters(Type vmType) {
            // ninaAPI Rotator.cs and FlatDevice.cs
            var method = vmType.GetMethod("GetDevice", BindingFlags.Public | BindingFlags.Instance);

            Assert.That(method, Is.Not.Null);
            Assert.That(method!.GetParameters(), Is.Empty);
        }

        [Test]
        public void GuiderVM_GuideStepsHistory() {
            // ninaAPI Equipment/Guider.cs
            Assert.That(typeof(GuiderVM).GetProperty("GuideStepsHistory")?.PropertyType, Is.EqualTo(typeof(GuideStepsHistory)));
        }

        [Test]
        public void FocuserMediator_SetReverse_TakesOneBool() {
            // ninaAPI Equipment/Focuser.cs
            var method = typeof(FocuserMediator).GetMethod("SetReverse");

            Assert.That(method, Is.Not.Null);
            Assert.That(method!.GetParameters().Select(parameter => parameter.ParameterType), Is.EqualTo(new[] { typeof(bool) }));
        }

        [TestCase(typeof(SkyFlat))]
        [TestCase(typeof(AutoBrightnessFlat))]
        [TestCase(typeof(AutoExposureFlat))]
        [TestCase(typeof(TrainedDarkFlatExposure))]
        [TestCase(typeof(TrainedFlatExposure))]
        public void FlatInstruction_GetIterations_ReturnsTheLoopCondition(Type flatType) {
            // ninaAPI Application/Flat.cs, for the flat run in progress
            MethodInfo? method = null;

            Assert.DoesNotThrow(() => method = flatType.GetMethod("GetIterations"));
            Assert.That(method, Is.Not.Null);
            Assert.That(method!.GetParameters(), Is.Empty);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(LoopCondition)));
        }

        [Test]
        public void SequenceMediator_NavigationAndFactory() {
            // sequenceNavigation: ninaAPI Utility/CoreUtility.cs (GetSequenceNavigation) and TNS SequenceController.cs;
            // factory: TNS only (ninaAPI takes ISequence2VM.SequencerFactory)
            var navigation = typeof(SequenceMediator).GetField("sequenceNavigation", PrivateInstance);
            var factory = typeof(SequenceNavigationVM).GetField("factory", PrivateInstance);

            Assert.That(navigation, Is.Not.Null);
            Assert.That(navigation!.FieldType, Is.EqualTo(typeof(ISequenceNavigationVM)));
            Assert.That(factory, Is.Not.Null);
            Assert.That(factory!.FieldType, Is.EqualTo(typeof(ISequencerFactory)));

            // TNS reads navVM.Sequence2VM through dynamic and casts it to ISequence2VM.
            Assert.That(typeof(SequenceNavigationVM).GetProperty("Sequence2VM")?.PropertyType, Is.EqualTo(typeof(ISequence2VM)));
        }

        [Test]
        public void UnknownSequenceContainer_ByName() {
            // ninaAPI Application/Sequence.cs refuses a load whose JSON deserializes to this internal placeholder,
            // comparing the type's full name
            var type = typeof(SequenceRootContainer).Assembly.GetType("NINA.Sequencer.Container.UnknownSequenceContainer");

            Assert.That(type, Is.Not.Null);
            Assert.That(typeof(ISequenceContainer).IsAssignableFrom(type), Is.True);
        }

        [TestCase("SkipToEndOfSequenceCommand")]
        [TestCase("SkipCurrentItemCommand")]
        public void Sequence2VM_SkipCommands(string name) {
            // TNS SequenceController.cs, on the runtime Sequence2VM
            var property = typeof(Sequence2VM).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

            Assert.That(property, Is.Not.Null);
            Assert.That(typeof(ICommand).IsAssignableFrom(property!.PropertyType), Is.True);
        }

        [TestCase(typeof(CameraSettings))]
        [TestCase(typeof(TelescopeSettings))]
        [TestCase(typeof(TakeExposure))]
        [TestCase(typeof(DeepSkyObjectContainer))]
        public void RaisePropertyChanged_ResolvesOnProfileSettingsAndSequenceEntities(Type type) {
            // ninaAPI Application/Sequence.cs (settings path: no parameter types) and TNS SequenceController.cs
            // (string parameter): both raise PropertyChanged after setting a value by its path.
            MethodInfo? byName = null;

            Assert.DoesNotThrow(() => byName = type.GetMethod("RaisePropertyChanged", PrivateInstance), "must not be overloaded");
            Assert.That(byName, Is.Not.Null);
            Assert.That(byName!.GetParameters().Select(parameter => parameter.ParameterType), Is.EqualTo(new[] { typeof(string) }));

            var byParameters = type.GetMethod(
                "RaisePropertyChanged",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [typeof(string)],
                null);

            Assert.That(byParameters, Is.Not.Null);
        }

        [Test]
        public void FramingAssistantVM_GetRotationFromCameraAndCancelSlew() {
            // ninaAPI Application/Framing.cs invokes it with one null argument and awaits a Task<bool>
            var rotation = typeof(FramingAssistantVM).GetMethod("GetRotationFromCamera", PrivateInstance);

            Assert.That(rotation, Is.Not.Null);
            Assert.That(rotation!.GetParameters(), Has.Length.EqualTo(1));
            Assert.That(rotation.ReturnType, Is.EqualTo(typeof(Task<bool>)));

            // TNS FramingController.cs
            var cancel = typeof(FramingAssistantVM).GetProperty("CancelSlewToCoordinatesCommand");

            Assert.That(cancel, Is.Not.Null);
            Assert.That(typeof(ICommand).IsAssignableFrom(cancel!.PropertyType), Is.True);
        }

        [TestCase("altitudes")]
        [TestCase("azimuths")]
        public void CustomHorizon_PointArrays(string name) {
            // ninaAPI Application/Profile.cs
            Assert.That(typeof(CustomHorizon).GetField(name, PrivateInstance)?.FieldType, Is.EqualTo(typeof(double[])));
        }

        [Test]
        public void MessageBroker_Subscribers() {
            // TNS TPPAController.cs casts the value to IDictionary and each entry's value to IList
            var field = typeof(MessageBroker).GetField("subscribers", PrivateInstance);

            Assert.That(field, Is.Not.Null);
            Assert.That(typeof(IDictionary).IsAssignableFrom(field!.FieldType), Is.True);

            var valueType = field.FieldType.GetGenericArguments().ElementAtOrDefault(1);

            Assert.That(valueType, Is.Not.Null);
            Assert.That(typeof(IList).IsAssignableFrom(valueType), Is.True);
        }

        [Test]
        public void Profile_PropertyChangedBackingField_IsFoundWalkingItsBaseTypes() {
            // TNS LivestackSettingsService.cs raises the profile's PropertyChanged through the event's backing field
            FieldInfo? field = null;

            for (var type = typeof(NINA.Profile.Profile); type != null && field == null; type = type.BaseType) {
                field = type.GetField("PropertyChanged", PrivateInstance | BindingFlags.DeclaredOnly);
            }

            Assert.That(field, Is.Not.Null);
            Assert.That(field!.FieldType, Is.EqualTo(typeof(PropertyChangedEventHandler)));
        }

        [Test]
        public void BaseVM_ProfileService_AndTheIndiAddressPath() {
            // TNS TenMicronController.cs walks the mount VM's bases for BaseVM's profileService, then reads
            // ActiveProfile.TelescopeSettings.IndiAddress by name on the runtime types
            FieldInfo? field = null;

            for (var type = typeof(DockableVM); type != null && field == null; type = type.BaseType) {
                field = type.GetField("profileService", PrivateInstance);
            }

            Assert.That(field, Is.Not.Null);
            Assert.That(field!.FieldType, Is.EqualTo(typeof(IProfileService)));
            Assert.That(typeof(ProfileService).GetProperty("ActiveProfile", BindingFlags.Public | BindingFlags.Instance), Is.Not.Null);
            Assert.That(typeof(NINA.Profile.Profile).GetProperty("TelescopeSettings", BindingFlags.Public | BindingFlags.Instance), Is.Not.Null);
            Assert.That(
                typeof(TelescopeSettings).GetProperty("IndiAddress", BindingFlags.Public | BindingFlags.Instance)?.PropertyType,
                Is.EqualTo(typeof(string)));
        }

        [Test]
        public void Astrometry_TypesByName_ForTenMicronModelBuilding() {
            // TNS TenMicronController.cs builds InputCoordinates and an Angle by type name
            var inputCoordinates = Type.GetType("NINA.Astrometry.InputCoordinates, NINA.Astrometry");
            var angle = Type.GetType("NINA.Astrometry.Angle, NINA.Astrometry");

            Assert.That(inputCoordinates, Is.EqualTo(typeof(InputCoordinates)));
            Assert.That(angle, Is.EqualTo(typeof(Angle)));
            Assert.That(inputCoordinates!.GetConstructor(Type.EmptyTypes), Is.Not.Null, "created with Activator.CreateInstance");
            Assert.That(inputCoordinates.GetProperty("Coordinates")?.PropertyType, Is.EqualTo(typeof(Coordinates)));
            foreach (var name in new[] { "RA", "Dec" }) {
                var property = typeof(Coordinates).GetProperty(name);

                Assert.That(property, Is.Not.Null, name);
                Assert.That(property!.CanWrite, Is.True, $"TNS sets Coordinates.{name}");
                Assert.That(property.PropertyType, Is.EqualTo(typeof(double)), $"TNS sets Coordinates.{name} with a double");
            }

            MethodInfo? byDegree = null;

            Assert.DoesNotThrow(() => byDegree = angle!.GetMethod("ByDegree", BindingFlags.Public | BindingFlags.Static));
            Assert.That(byDegree?.GetParameters().Select(parameter => parameter.ParameterType), Is.EqualTo(new[] { typeof(double) }));
        }

        [TestCase("CanSetGuideRates", false)]
        [TestCase("GuideRateRightAscensionArcsecPerSec", true)]
        [TestCase("GuideRateDeclinationArcsecPerSec", true)]
        public void IndiTelescope_GuideRateProperties(string name, bool written) {
            // TNS MountController.cs, by name because IndiTelescope is internal to NINA.Equipment
            var property = typeof(IndiTelescope).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

            Assert.That(property, Is.Not.Null);

            if (written) {
                Assert.That(property!.CanWrite, Is.True);
                Assert.That(property.PropertyType, Is.EqualTo(typeof(double)));
            } else {
                Assert.That(property!.PropertyType, Is.EqualTo(typeof(bool)));
            }
        }
    }
}
