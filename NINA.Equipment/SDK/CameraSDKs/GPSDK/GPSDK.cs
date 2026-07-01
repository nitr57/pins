using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;

namespace GPSDK {

    public class GPSDK {

        private const string GPHOTO2 = "libgphoto2.so";
        private const string GPHOTO2PORT = "libgphoto2_port.so";

        static GPSDK() {
            DllLoader.LoadDll(GPHOTO2);
            DllLoader.LoadDll(GPHOTO2PORT);
        }

        // Camera specs lookup table: (width, height, pixel_size_x_um, pixel_size_y_um)
        public static readonly Dictionary<string, (int width, int height, double pixelSizeX, double pixelSizeY)> GpCameraSpecs = new() {
            // Canon EOS 5D series
            { "Canon EOS 5D Mark IV", (6720, 4480, 5.36, 5.36) },
            { "Canon EOS 5D Mark III", (5760, 3840, 6.25, 6.25) },
            { "Canon EOS 5DS", (8688, 5792, 4.14, 4.14) },
            { "Canon EOS 5DS R", (8688, 5792, 4.14, 4.14) },
            
            // Canon EOS R series
            { "Canon EOS R5", (8192, 5464, 4.39, 4.39) },
            { "Canon EOS R6", (5472, 3648, 6.56, 6.56) },
            { "Canon EOS R6 Mark II", (6000, 4000, 6.00, 6.00) },
            { "Canon EOS R6 Mark III", (6960, 4640, 5.16, 5.16) },
            { "Canon EOS R7", (6960, 4640, 3.20, 3.20) },
            { "Canon EOS R8", (6000, 4000, 6.00, 6.00) },
            { "Canon EOS R10", (6000, 4000, 3.72, 3.72) },
            { "Canon EOS R", (6720, 4480, 5.36, 5.36) },
            { "Canon EOS RP", (6240, 4160, 5.75, 5.75) },
            
            // Canon EOS 6D series
            { "Canon EOS 6D Mark II", (6240, 4160, 5.75, 5.75) },
            { "Canon EOS 6D", (5472, 3648, 6.54, 6.54) },
            
            // Canon EOS 1D series
            { "Canon EOS-1D X Mark III", (5472, 3648, 6.58, 6.58) },
            { "Canon EOS-1D X Mark II", (5472, 3648, 6.58, 6.58) },
            
            // Nikon D series
            { "Nikon DSC D750", (6016, 4016, 5.97, 5.97) },
            { "Nikon D750", (6016, 4016, 5.97, 5.97) },
            { "Nikon D850", (8256, 5504, 4.35, 4.35) },
            
            // Nikon Z series
            { "Nikon Z7", (8256, 5504, 4.35, 4.35) },
            { "Nikon Z6", (6000, 4000, 5.98, 5.98) },
            
            // Sony Alpha series
            { "Sony Alpha 7R V", (9504, 6336, 3.76, 3.76) },
            { "Sony Alpha 7R IV", (9504, 6336, 3.76, 3.76) },
            { "Sony Alpha 7R III", (7952, 5304, 4.51, 4.51) },
            { "Sony Alpha 7 III", (6000, 4000, 5.93, 5.93) },
            { "Sony Alpha 7C", (6000, 4000, 5.93, 5.93) },
        };

        public static Dictionary<string, string> Enum() {
            Dictionary<string, string> devices = [];

            // Create context
            IntPtr ctx = GpContextNew();

            // Fetch list
            IntPtr list = new();
            if (GpListNew(ref list) != GP_ERROR_CODE.GP_OK) {
                Logger.Error("Could not create list");
                GpContextUnref(ctx);
                return devices;
            }

            if (GpCameraAutodetect(list, ctx, out var count) != GP_ERROR_CODE.GP_OK) {
                Logger.Error("Could not detect cameras");
                GpListFree(list);
                GpContextUnref(ctx);
                return devices;
            }

            // Load abilities list once (contains all camera models gphoto2 knows about).
            // If this fails, the device-type filter below is skipped and every detected
            // device is returned.
            IntPtr abilitiesList = IntPtr.Zero;
            if (GpAbilitiesListNew(ref abilitiesList) != GP_ERROR_CODE.GP_OK) {
                Logger.Warning("Could not create abilities list; skipping device-type filtering");
                abilitiesList = IntPtr.Zero;
            } else if (GpAbilitiesListLoad(abilitiesList, ctx) != GP_ERROR_CODE.GP_OK) {
                Logger.Warning("Could not load abilities list; skipping device-type filtering");
                GpAbilitiesListFree(abilitiesList);
                abilitiesList = IntPtr.Zero;
            }

            // Fetch number of items
            for (int i = 0; i < count; i++) {
                // Fetch name and value
                string name = string.Empty;
                string path = string.Empty;
                GpListGetName(list, i, out name);
                GpListGetValue(list, i, out path);

                // Look up this specific camera model in the abilities list by name
                if (abilitiesList != IntPtr.Zero) {
                    int abilityIdx = GpAbilitiesListLookupModel(abilitiesList, name);
                    if (abilityIdx >= 0) {
                        CameraAbilities abilities = new();
                        GpAbilitiesListGetAbilities(abilitiesList, abilityIdx, out abilities);

                        // Skip non-still-camera devices (e.g. audio players)
                        if (abilities.device_type != GphotoDeviceType.GP_DEVICE_STILL_CAMERA) {
                            continue;
                        }
                    }
                }

                // Add camera to list
                devices.Add(name, path);
            }

            // Clean up
            if (abilitiesList != IntPtr.Zero) {
                GpAbilitiesListFree(abilitiesList);
            }
            GpListFree(list);
            GpContextUnref(ctx);

            return devices;
        }

        [SecurityCritical]
        public static string GpLibraryVersion() {
            try {
                // gp_library_version returns a char** (pointer to array of strings)
                // We need to dereference the first pointer in the array to get the actual string
                IntPtr versionArrayPtr = gp_library_version(0);
                if (versionArrayPtr != IntPtr.Zero) {
                    // Dereference to get the actual string pointer
                    IntPtr stringPtr = Marshal.ReadIntPtr(versionArrayPtr);
                    if (stringPtr != IntPtr.Zero) {
                        string version = Marshal.PtrToStringAnsi(stringPtr);
                        // Clean up the version string - remove extra whitespace and limit length
                        if (!string.IsNullOrEmpty(version)) {
                            version = version.Trim();
                            if (version.Length > 100) {
                                version = version.Substring(0, 100);
                            }
                            return version;
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Warning($"Failed to get libgphoto2 version: {ex.Message}");
            }
            return "unknown";
        }

        [SecurityCritical]
        public static int GpFileNew(out IntPtr file) {
            return gp_file_new(out file);
        }

        [SecurityCritical]
        public static int GpFileFree(IntPtr file) {
            return gp_file_free(file);
        }

        [SecurityCritical]
        public static int GpFileGetDataAndSize(IntPtr file, out IntPtr data, out ulong size) {
            return gp_file_get_data_and_size(file, out data, out size);
        }

        /* Enums */
        public enum GP_ERROR_CODE {
            GP_OK = 0,
            GP_ERROR = -1,
            GP_ERROR_BAD_PARAMETERS = -2,
            GP_ERROR_NO_MEMORY = -3,
            GP_ERROR_LIBRARY = -4,
            GP_ERROR_UNKNOWN_PORT = -5,
            GP_ERROR_NOT_SUPPORTED = -6,
            GP_ERROR_IO = -7,
            GP_ERROR_FIXED_LIMIT_EXCEEDED = -8,
            GP_ERROR_TIMEOUT = -10,
            GP_ERROR_IO_SUPPORTED_SERIAL = -20,
            GP_ERROR_IO_SUPPORTED_USB = -21,
            GP_ERROR_IO_INIT = -31,
            GP_ERROR_IO_READ = -34,
            GP_ERROR_IO_WRITE = -35,
            GP_ERROR_IO_UPDATE = -37,
            GP_ERROR_IO_SERIAL_SPEED = -41,
            GP_ERROR_IO_USB_CLEAR_HALT = -51,
            GP_ERROR_IO_USB_FIND = -52,
            GP_ERROR_IO_USB_CLAIM = -53,
            GP_ERROR_IO_LOCK = -60,
            GP_ERROR_HAL = -70,
            GP_ERROR_CORRUPTED_DATA = -102,
            GP_ERROR_FILE_EXISTS = -103,
            GP_ERROR_MODEL_NOT_FOUND = -105,
            GP_ERROR_DIRECTORY_NOT_FOUND = -107,
            GP_ERROR_FILE_NOT_FOUND = -108,
            GP_ERROR_DIRECTORY_EXISTS = -109,
            GP_ERROR_CAMERA_BUSY = -110,
            GP_ERROR_PATH_NOT_ABSOLUTE = -111,
            GP_ERROR_CANCEL = -112,
            GP_ERROR_CAMERA_ERROR = -113,
            GP_ERROR_OS_FAILURE = -114,
            GP_ERROR_NO_SPACE = -115
        }

        public enum GphotoDeviceType {
            GP_DEVICE_STILL_CAMERA = 0,
            GP_DEVICE_AUDIO_PLAYER = 1 << 0
        }

        public enum GPPortType {
            GP_PORT_NONE = 0,
            GP_PORT_SERIAL = 1 << 0,
            GP_PORT_USB = 1 << 2,
            GP_PORT_DISK = 1 << 3,
            GP_PORT_PTPIP = 1 << 4,
            GP_PORT_USB_DISK_DIRECT = 1 << 5,
            GP_PORT_USB_SCSI = 1 << 6
        }

        public enum CameraDriverStatus {
            GP_DRIVER_STATUS_PRODUCTION,
            GP_DRIVER_STATUS_TESTING,
            GP_DRIVER_STATUS_EXPERIMENTAL,
            GP_DRIVER_STATUS_DEPRECATED
        }

        public enum CameraOperation {
            GP_OPERATION_NONE = 0,
            GP_OPERATION_CAPTURE_IMAGE = 1 << 0,
            GP_OPERATION_CAPTURE_VIDEO = 1 << 1,
            GP_OPERATION_CAPTURE_AUDIO = 1 << 2,
            GP_OPERATION_CAPTURE_PREVIEW = 1 << 3,
            GP_OPERATION_CONFIG = 1 << 4,
            GP_OPERATION_TRIGGER_CAPTURE = 1 << 5
        }

        public enum CameraCaptureType {
            GP_CAPTURE_IMAGE,
            GP_CAPTURE_MOVIE,
            GP_CAPTURE_SOUND
        }

        public enum CameraEventType {
            GP_EVENT_UNKNOWN,
            GP_EVENT_TIMEOUT,
            GP_EVENT_FILE_ADDED,
            GP_EVENT_FOLDER_ADDED,
            GP_EVENT_CAPTURE_COMPLETE
        }

        public enum CameraFileOperation {
            GP_FILE_OPERATION_NONE = 0 << 0,
            GP_FILE_OPERATION_DELETE = 1 << 1,
            GP_FILE_OPERATION_PREVIEW = 1 << 3,
            GP_FILE_OPERATION_RAW = 1 << 4,
            GP_FILE_OPERATION_AUDIO = 1 << 5,
            GP_FILE_OPERATION_EXIF = 1 << 6
        }

        public enum CameraFolderOperation {
            GP_FOLDER_OPERATION_NONE = 0,
            GP_FOLDER_OPERATION_DELETE_ALL = 1 << 0,
            GP_FOLDER_OPERATION_PUT_FILE = 1 << 1,
            GP_FOLDER_OPERATION_MAKE_DIR = 1 << 2,
            GP_FOLDER_OPERATION_REMOVE_DIR = 1 << 3
        }

        public enum CameraFileType {
            GP_FILE_TYPE_PREVIEW,
            GP_FILE_TYPE_NORMAL,
            GP_FILE_TYPE_RAW,
            GP_FILE_TYPE_AUDIO,
            GP_FILE_TYPE_EXIF,
            GP_FILE_TYPE_METADATA
        }

        public enum CameraWidgetType {
            GP_WIDGET_WINDOW,
            GP_WIDGET_SECTION,
            GP_WIDGET_TEXT,
            GP_WIDGET_RANGE,
            GP_WIDGET_TOGGLE,
            GP_WIDGET_RADIO,
            GP_WIDGET_MENU,
            GP_WIDGET_BUTTON,
            GP_WIDGET_DATE
        }




        /* Structs */
        [StructLayout(LayoutKind.Sequential)]
        public struct CameraFilePath {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] name;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
            public byte[] folder;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CameraAbilities {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] model;

            public CameraDriverStatus status;
            public GPPortType port;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public int[] speed;

            public CameraOperation operations;
            public CameraFileOperation file_operations;
            public CameraFolderOperation folder_operations;
            public int usb_vendor;
            public int usb_product;
            public int usb_class;
            public int usb_subclass;
            public int usb_protocol;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
            public byte[] library;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
            public byte[] id;

            public GphotoDeviceType device_type;

            // reserved space
            int reserved2;
            int reserved3;
            int reserved4;
            int reserved5;
            int reserved6;
            int reserved7;
            int reserved8;
        }










        [SecurityCritical]
        public static IntPtr GpContextNew() {
            return gp_context_new();
        }

        [SecurityCritical]
        public static void GpContextUnref(IntPtr context) {
            gp_context_unref(context);
        }











        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraNew(ref IntPtr camera) {
            return (GP_ERROR_CODE)gp_camera_new(ref camera);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraFree(IntPtr camera) {
            return (GP_ERROR_CODE)gp_camera_free(camera);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraInit(IntPtr camera, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_init(camera, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraExit(IntPtr camera, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_exit(camera, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameracapturePreview(IntPtr camera, IntPtr file, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_capture_preview(camera, file, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraCapture(IntPtr camera, CameraCaptureType type, out CameraFilePath path, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_capture(camera, type, out path, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraWaitForEvent(IntPtr camera, int timeout, out CameraEventType eventtype, out IntPtr eventdata, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_wait_for_event(camera, timeout, out eventtype, out eventdata, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraFileGet(IntPtr camera, byte[] folder, byte[] filename, CameraFileType filetype, IntPtr file, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_file_get(camera, folder, filename, filetype, file, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraFileDelete(IntPtr camera, byte[] folder, byte[] filename, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_file_delete(camera, folder, filename, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraAutodetect(IntPtr list, IntPtr context, out int count) {
            count = 0;
            var ret = gp_camera_autodetect(list, context);
            if (ret < 0) {
                return (GP_ERROR_CODE)ret;
            }
            count = ret;
            return GP_ERROR_CODE.GP_OK;
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraGetPortInfo(IntPtr camera, out IntPtr info) {
            return (GP_ERROR_CODE)gp_camera_get_port_info(camera, out info);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraSetPortInfo(IntPtr camera, IntPtr info) {
            return (GP_ERROR_CODE)gp_camera_set_port_info(camera, info);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraGetSingleConfig(IntPtr camera, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr widget, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_get_single_config(camera, name, out widget, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraSetSingleConfig(IntPtr camera, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr widget, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_set_single_config(camera, name, widget, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpCameraGetConfig(IntPtr camera, out IntPtr window, IntPtr context) {
            return (GP_ERROR_CODE)gp_camera_get_config(camera, out window, context);
        }







        [SecurityCritical]
        public static GP_ERROR_CODE GpListNew(ref IntPtr list) {
            return (GP_ERROR_CODE)gp_list_new(ref list);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpListFree(IntPtr list) {
            return (GP_ERROR_CODE)gp_list_free(list);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpListGetName(IntPtr list, int index, out string name) {
            IntPtr namePtr = IntPtr.Zero;
            name = string.Empty;
            var ret = (GP_ERROR_CODE)gp_list_get_name(list, index, ref namePtr);
            if (ret == GP_ERROR_CODE.GP_OK) {
                name = Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
            }
            return ret;
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpListGetValue(IntPtr list, int index, out string value) {
            IntPtr valuePtr = IntPtr.Zero;
            value = string.Empty;
            var ret = (GP_ERROR_CODE)gp_list_get_value(list, index, ref valuePtr);
            if (ret == GP_ERROR_CODE.GP_OK) {
                value = Marshal.PtrToStringAnsi(valuePtr) ?? string.Empty;
            }
            return ret;
        }








        [SecurityCritical]
        public static GP_ERROR_CODE GpAbilitiesListNew(ref IntPtr list) {
            return (GP_ERROR_CODE)gp_abilities_list_new(ref list);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpAbilitiesListFree(IntPtr list) {
            return (GP_ERROR_CODE)gp_abilities_list_free(list);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpAbilitiesListLoad(IntPtr list, IntPtr context) {
            return (GP_ERROR_CODE)gp_abilities_list_load(list, context);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpAbilitiesListGetAbilities(IntPtr list, int index, out CameraAbilities abilities) {
            return (GP_ERROR_CODE)gp_abilities_list_get_abilities(list, index, out abilities);
        }

        [SecurityCritical]
        public static int GpAbilitiesListLookupModel(IntPtr list, string model) {
            return gp_abilities_list_lookup_model(list, model);
        }

        [SecurityCritical]
        public static void GpFree(IntPtr ptr) {
            if (ptr != IntPtr.Zero) {
                free(ptr);
            }
        }






        [SecurityCritical]
        public static GP_ERROR_CODE GpPortInfoListNew(ref IntPtr list) {
            return (GP_ERROR_CODE)gp_port_info_list_new(ref list);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpPortInfoListFree(IntPtr list) {
            return (GP_ERROR_CODE)gp_port_info_list_free(list);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpPortInfoListLoad(IntPtr list) {
            return (GP_ERROR_CODE)gp_port_info_list_load(list);
        }

        [SecurityCritical]
        public static int GpPortInfoListLookupPath(IntPtr list, string path) {
            return gp_port_info_list_lookup_path(list, path);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpPortInfoListGetInfo(IntPtr list, int index, out IntPtr info) {
            return (GP_ERROR_CODE)gp_port_info_list_get_info(list, index, out info);
        }





















        // gp_widget_get/set_value uses void* in C; expose typed wrappers so callers
        // pass the right storage for toggles, ranges, and strings.
        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetGetValue(IntPtr widget, out string value) {
            IntPtr valuePtr = IntPtr.Zero;
            var ret = (GP_ERROR_CODE)gp_widget_get_value_string(widget, out valuePtr);
            value = Marshal.PtrToStringAnsi(valuePtr) ?? string.Empty;
            return ret;
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetGetValue(IntPtr widget, out int value) {
            return (GP_ERROR_CODE)gp_widget_get_value_int(widget, out value);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetGetValue(IntPtr widget, out float value) {
            return (GP_ERROR_CODE)gp_widget_get_value_float(widget, out value);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetGetType(IntPtr widget, out CameraWidgetType type) {
            return (GP_ERROR_CODE)gp_widget_get_type(widget, out type);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetFree(IntPtr widget) {
            return (GP_ERROR_CODE)gp_widget_free(widget);
        }

        [SecurityCritical]
        public static int GpWidgetCountChoices(IntPtr widget) {
            return gp_widget_count_choices(widget);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetGetChoice(IntPtr widget, int index, out string choice) {
            choice = string.Empty;
            IntPtr choicePtr = IntPtr.Zero;
            var ret = (GP_ERROR_CODE)gp_widget_get_choice(widget, index, ref choicePtr);
            if (ret == GP_ERROR_CODE.GP_OK && choicePtr != IntPtr.Zero) {
                choice = Marshal.PtrToStringAnsi(choicePtr);
            }
            return ret;
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetSetValue(IntPtr widget, [MarshalAs(UnmanagedType.LPStr)] string value) {
            return (GP_ERROR_CODE)gp_widget_set_value_string(widget, value);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetSetValue(IntPtr widget, int value) {
            return (GP_ERROR_CODE)gp_widget_set_value_int(widget, ref value);
        }

        [SecurityCritical]
        public static GP_ERROR_CODE GpWidgetSetValue(IntPtr widget, float value) {
            return (GP_ERROR_CODE)gp_widget_set_value_float(widget, ref value);
        }
        /* Context */
        [DllImport(GPHOTO2, EntryPoint = "gp_context_new", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr gp_context_new();

        [DllImport(GPHOTO2, EntryPoint = "gp_context_unref", CallingConvention = CallingConvention.Cdecl)]
        private static extern void gp_context_unref(IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_library_version", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr gp_library_version(int index);

        /* Camera */
        [DllImport(GPHOTO2, EntryPoint = "gp_camera_new", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_new(ref IntPtr camera);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_free(IntPtr camera);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_init", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_init(IntPtr camera, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_exit", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_exit(IntPtr camera, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_capture_preview", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_capture_preview(IntPtr camera, IntPtr file, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_capture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_capture(IntPtr camera, CameraCaptureType type, out CameraFilePath path, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_wait_for_event", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_wait_for_event(IntPtr camera, int timeout, out CameraEventType eventtype, out IntPtr eventdata, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_file_get", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_file_get(IntPtr camera, [In] byte[] folder, [In] byte[] filename, CameraFileType filetype, IntPtr file, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_file_delete", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_file_delete(IntPtr camera, [In] byte[] folder, [In] byte[] filename, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_autodetect", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_autodetect(IntPtr list, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_get_port_info", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_get_port_info(IntPtr camera, out IntPtr info);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_set_port_info", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_set_port_info(IntPtr camera, IntPtr info);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_get_single_config", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_get_single_config(IntPtr camera, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr widget, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_set_single_config", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_set_single_config(IntPtr camera, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr widget, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_camera_get_config", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_camera_get_config(IntPtr camera, out IntPtr window, IntPtr context);

        /* List */
        [DllImport(GPHOTO2, EntryPoint = "gp_list_new", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_list_new(ref IntPtr list);

        [DllImport(GPHOTO2, EntryPoint = "gp_list_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_list_free(IntPtr list);

        [DllImport(GPHOTO2, EntryPoint = "gp_list_get_name", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_list_get_name(IntPtr list, int index, ref IntPtr name);

        [DllImport(GPHOTO2, EntryPoint = "gp_list_get_value", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_list_get_value(IntPtr list, int index, ref IntPtr value);







        /* Abilities list */
        [DllImport(GPHOTO2, EntryPoint = "gp_abilities_list_new", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_abilities_list_new(ref IntPtr list);

        [DllImport(GPHOTO2, EntryPoint = "gp_abilities_list_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_abilities_list_free(IntPtr list);

        [DllImport(GPHOTO2, EntryPoint = "gp_abilities_list_load", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_abilities_list_load(IntPtr list, IntPtr context);

        [DllImport(GPHOTO2, EntryPoint = "gp_abilities_list_get_abilities", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_abilities_list_get_abilities(IntPtr list, int index, out CameraAbilities abilities);

        [DllImport(GPHOTO2, EntryPoint = "gp_abilities_list_lookup_model", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_abilities_list_lookup_model(IntPtr list, [MarshalAs(UnmanagedType.LPStr)] string model);

        [DllImport("libc", EntryPoint = "free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void free(IntPtr ptr);

        /* Port info list */
        [DllImport(GPHOTO2PORT, EntryPoint = "gp_port_info_list_new", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_port_info_list_new(ref IntPtr list);

        [DllImport(GPHOTO2PORT, EntryPoint = "gp_port_info_list_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_port_info_list_free(IntPtr list);

        [DllImport(GPHOTO2PORT, EntryPoint = "gp_port_info_list_load", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_port_info_list_load(IntPtr list);

        [DllImport(GPHOTO2PORT, EntryPoint = "gp_port_info_list_lookup_path", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_port_info_list_lookup_path(IntPtr list, [MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(GPHOTO2PORT, EntryPoint = "gp_port_info_list_get_info", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_port_info_list_get_info(IntPtr list, int index, out IntPtr info);

        /* Widget */
        [DllImport(GPHOTO2, EntryPoint = "gp_widget_get_value", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_get_value_string(IntPtr widget, out IntPtr value);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_get_value", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_get_value_int(IntPtr widget, out int value);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_get_value", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_get_value_float(IntPtr widget, out float value);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_get_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_get_type(IntPtr widget, out CameraWidgetType type);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_free(IntPtr widget);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_count_choices", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_count_choices(IntPtr widget);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_get_choice", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_get_choice(IntPtr widget, int index, ref IntPtr choice);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_set_value", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_set_value_string(IntPtr widget, [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_set_value", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_set_value_int(IntPtr widget, ref int value);

        [DllImport(GPHOTO2, EntryPoint = "gp_widget_set_value", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_widget_set_value_float(IntPtr widget, ref float value);

        /* File Operations */
        [DllImport(GPHOTO2, EntryPoint = "gp_file_new", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_file_new(out IntPtr file);

        [DllImport(GPHOTO2, EntryPoint = "gp_file_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_file_free(IntPtr file);

        [DllImport(GPHOTO2, EntryPoint = "gp_file_get_data_and_size", CallingConvention = CallingConvention.Cdecl)]
        private static extern int gp_file_get_data_and_size(IntPtr file, out IntPtr data, out ulong size);
    }
}