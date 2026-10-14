using SteamLinkVRSettings.Models;

namespace SteamLinkVRSettings.Helpers;

public static class SettingDefinitions
{
    public static List<SettingCategory> BuildCategories() =>
    [
        new SettingCategory
        {
            Name = "Streaming Quality",
            Glyph = "\uE714",
            Description = "Video encoding and bandwidth",
            Settings =
            [
                new SettingItem
                {
                    Key = "targetBandwidth",
                    DisplayName = "Target Bandwidth",
                    Description = "Requested video target. The driver may cap this; the tested override raises its ceiling to 500 Mbps. Actual bitrate varies with the scene.",
                    Unit = "Mbps",
                    ControlType = ControlType.Slider,
                    Minimum = 25, Maximum = 500, StepFrequency = 5,
                    StoreAsFloat = true
                },
                new SettingItem
                {
                    Key = "recommendedBandwidthMbit",
                    DisplayName = "Recommended Bandwidth",
                    Description = "Initial bandwidth the adaptive algorithm starts from",
                    Unit = "Mbps",
                    ControlType = ControlType.Slider,
                    Minimum = 25, Maximum = 1000, StepFrequency = 5
                },
                new SettingItem
                {
                    Key = "encodeWidth",
                    DisplayName = "Encode Width",
                    Description = "Base video encode width in pixels",
                    Unit = "px",
                    ControlType = ControlType.Slider,
                    Minimum = 512, Maximum = 4096, StepFrequency = 64
                },
                new SettingItem
                {
                    Key = "streamFormatWidth",
                    DisplayName = "Stream Format Width",
                    Description = "Transmitted stream width (foveated center resolution)",
                    Unit = "px",
                    ControlType = ControlType.Slider,
                    Minimum = 512, Maximum = 4096, StepFrequency = 64
                },
                new SettingItem
                {
                    Key = "renderWidth",
                    DisplayName = "Render Width",
                    Description = "Full render target width before encode",
                    Unit = "px",
                    ControlType = ControlType.Slider,
                    Minimum = 1024, Maximum = 8192, StepFrequency = 16
                },
                new SettingItem
                {
                    Key = "renderHeight",
                    DisplayName = "Render Height",
                    Description = "Full render target height before encode",
                    Unit = "px",
                    ControlType = ControlType.Slider,
                    Minimum = 1024, Maximum = 8192, StepFrequency = 16
                },
                new SettingItem
                {
                    Key = "10bit",
                    DisplayName = "10-bit Encoding",
                    Description = "Use 10-bit color depth (requires HMD and GPU support)",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "reqEncMode",
                    DisplayName = "Transport Encryption Mode",
                    Description = "Transport mode override. Auto selects from the encryption setting; this does not select video encoder quality.",
                    ControlType = ControlType.Dropdown,
                    RequiresRestart = true,
                    Options =
                    [
                        new OptionItem { Label = "Auto", Value = "auto" },
                        new OptionItem { Label = "AES", Value = "aes" },
                        new OptionItem { Label = "MID", Value = "mid" }
                    ]
                },
                new SettingItem
                {
                    Key = "enableAmfEncoder",
                    DisplayName = "Enable AMF Encoder",
                    Description = "Allow AMD AMF hardware encoder (for AMD GPUs)",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "automaticStreamFormatWidth",
                    DisplayName = "Auto Stream Format Width",
                    Description = "Let the driver automatically choose encode resolution",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "automaticBandwidth",
                    DisplayName = "Auto Bandwidth Target",
                    Description = "Let the driver automatically set the bandwidth target",
                    ControlType = ControlType.Toggle
                }
            ]
        },
        new SettingCategory
        {
            Name = "Network",
            Glyph = "\uE968",
            Description = "Transport, QoS, and error correction",
            Settings =
            [
                new SettingItem
                {
                    Key = "enableQoS",
                    DisplayName = "Enable QoS",
                    Description = "Tag network packets with priority for router QoS",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "enableEncryption",
                    DisplayName = "Enable Encryption",
                    Description = "Encrypt the video stream (adds slight latency)",
                    ControlType = ControlType.Toggle,
                    RequiresRestart = true
                },
                new SettingItem
                {
                    Key = "forceBaselineVideoFEC",
                    DisplayName = "Force Baseline FEC",
                    Description = "Send Valve's baseline FEC request. Its effect on repair traffic is unverified in the researched driver/client build.",
                    ControlType = ControlType.Toggle,
                    RequiresRestart = true
                },
                new SettingItem
                {
                    Key = "asyncSend",
                    DisplayName = "Async Send",
                    Description = "Use asynchronous network send for lower latency",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "backoffRecoveryCoefficient",
                    DisplayName = "Backoff Recovery Coefficient",
                    Description = "How aggressively bandwidth recovers after throttling (higher = faster recovery)",
                    ControlType = ControlType.Slider,
                    Minimum = 0.5, Maximum = 10.0, StepFrequency = 0.1
                },
                new SettingItem
                {
                    Key = "enableNAT",
                    DisplayName = "Enable NAT",
                    Description = "Enable NAT traversal for remote connections",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "enableTimedRetry",
                    DisplayName = "Timed Retry",
                    Description = "Enable timed retry on connection failures",
                    ControlType = ControlType.Dropdown,
                    Options =
                    [
                        new OptionItem { Label = "Disabled", Value = 0 },
                        new OptionItem { Label = "Enabled", Value = 1 }
                    ]
                }
            ]
        },
        new SettingCategory
        {
            Name = "Display",
            Glyph = "\uE7F4",
            Description = "Refresh rate and display behavior",
            Settings =
            [
                new SettingItem
                {
                    Key = "displayFrequency",
                    DisplayName = "Display Frequency",
                    Description = "Target refresh rate sent to the HMD",
                    Unit = "Hz",
                    ControlType = ControlType.Dropdown,
                    Options =
                    [
                        new OptionItem { Label = "72 Hz", Value = 72 },
                        new OptionItem { Label = "80 Hz", Value = 80 },
                        new OptionItem { Label = "90 Hz", Value = 90 },
                        new OptionItem { Label = "120 Hz", Value = 120 },
                        new OptionItem { Label = "144 Hz", Value = 144 }
                    ]
                },
                new SettingItem
                {
                    Key = "openDashboardOnConnectionLoss",
                    DisplayName = "Open Dashboard on Connection Loss",
                    Description = "Automatically open the SteamVR dashboard if the link drops",
                    ControlType = ControlType.Toggle
                }
            ]
        },
        new SettingCategory
        {
            Name = "Tracking & Input",
            Glyph = "\uE7C9",
            Description = "Hand, eye, and face tracking",
            Settings =
            [
                new SettingItem
                {
                    Key = "enableHandTracking",
                    DisplayName = "Hand Tracking Passthrough",
                    Description = "Enable hand tracking from the HMD",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "shareEyeTrackingData",
                    DisplayName = "Share Eye Tracking Data",
                    Description = "Share eye tracking data with other applications on this PC",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "useOSC",
                    DisplayName = "Enable OSC",
                    Description = "Enable Open Sound Control protocol for tracking data",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "useOSCFace",
                    DisplayName = "OSC Face Tracking",
                    Description = "Share face tracking data to other apps via OSC",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "OSCInPort",
                    DisplayName = "OSC Input Port",
                    Description = "Port for incoming OSC data",
                    ControlType = ControlType.NumberBox,
                    Minimum = 1024, Maximum = 65535, StepFrequency = 1
                },
                new SettingItem
                {
                    Key = "OSCOutPort",
                    DisplayName = "OSC Output Port",
                    Description = "Port for outgoing OSC data",
                    ControlType = ControlType.Dropdown,
                    Options =
                    [
                        new OptionItem { Label = "9000 (Direct)", Value = 9000 },
                        new OptionItem { Label = "9015 (Alt)", Value = 9015 }
                    ]
                }
            ]
        },
        new SettingCategory
        {
            Name = "Advanced",
            Glyph = "\uE90F",
            Description = "Developer and diagnostic options",
            Settings =
            [
                new SettingItem
                {
                    Key = "wirelessShutdownTimeout",
                    DisplayName = "Wireless Shutdown Timeout",
                    Description = "On disconnect, exit SteamVR after this duration",
                    ControlType = ControlType.Dropdown,
                    Options =
                    [
                        new OptionItem { Label = "Never", Value = 0 },
                        new OptionItem { Label = "30 seconds", Value = 30 },
                        new OptionItem { Label = "1 minute", Value = 60 },
                        new OptionItem { Label = "5 minutes", Value = 300 },
                        new OptionItem { Label = "10 minutes", Value = 600 },
                        new OptionItem { Label = "20 minutes", Value = 1200 },
                        new OptionItem { Label = "30 minutes", Value = 1800 },
                        new OptionItem { Label = "1 hour", Value = 3600 }
                    ]
                },
                new SettingItem
                {
                    Key = "allowVolumeSync",
                    DisplayName = "Volume Control Sync",
                    Description = "Control headset volume and mute from SteamVR",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "asyncStartEncode",
                    DisplayName = "Async Start Encode",
                    Description = "Start encoding asynchronously for reduced latency",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "debugRegionColoring",
                    DisplayName = "Debug Region Coloring",
                    Description = "Colorize foveation regions for debugging",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "showAdvancedGraphs",
                    DisplayName = "Show Debug Graphs",
                    Description = "Show advanced performance graphs in the HMD",
                    ControlType = ControlType.Toggle
                },
                new SettingItem
                {
                    Key = "forceOpenMode",
                    DisplayName = "Force Open Mode",
                    Description = "Force the encoder into open mode",
                    ControlType = ControlType.Toggle,
                    IsExperimental = true
                },
                new SettingItem
                {
                    Key = "watchForShaderChanges",
                    DisplayName = "Watch For Shader Changes",
                    Description = "Hot-reload shaders when files change on disk",
                    ControlType = ControlType.Toggle,
                    IsExperimental = true
                },
                new SettingItem
                {
                    Key = "spoofAtStart",
                    DisplayName = "Spoof At Start",
                    Description = "Spoof a connected device at driver startup",
                    ControlType = ControlType.Toggle,
                    IsExperimental = true
                },
                new SettingItem
                {
                    Key = "enable8814AP",
                    DisplayName = "Enable 8814 AP",
                    Description = "Enable Realtek 8814 Access Point mode for WiFi",
                    ControlType = ControlType.Toggle,
                    IsExperimental = true
                }
            ]
        }
    ];
}
