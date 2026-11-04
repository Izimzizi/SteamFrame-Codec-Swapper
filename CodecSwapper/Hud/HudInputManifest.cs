using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodecSwapper;

/// <summary>
/// The SteamVR input manifest of the HUD helper: an aim pose and a trigger click, with default bindings for the common controllers, so the user
/// never opens SteamVR's binding screen. The pose set is always active (reading it takes no input from any game); the click set is only
/// activated while the laser is on the HUD, at an overlay priority above the game's, so the trigger press goes to the HUD and not to the game.
/// </summary>
public static class HudInputManifest
{
    public const string AimSet = "/actions/hudaim", ClickSet = "/actions/hudclick", TipAction = "/actions/hudaim/in/tip", ClickAction = "/actions/hudclick/in/click";
    private static readonly string[] ControllerTypes = ["frame_controller", "knuckles", "oculus_touch", "vive_controller"];
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Writes the manifest and the bindings into <paramref name="directory"/> (only the files that changed) and returns the manifest's full path.</summary>
    public static string Write(string directory, string appKey)
    {
        Directory.CreateDirectory(directory);
        var bindings = new JsonArray();
        foreach (var type in ControllerTypes)
        {
            var file = $"hud_bindings_{type}.json";
            bindings.Add(new JsonObject { ["controller_type"] = type, ["binding_url"] = file });
            WriteIfChanged(Path.Combine(directory, file), Binding(type, appKey).ToJsonString(Pretty));
        }
        var manifest = new JsonObject
        {
            ["default_bindings"] = bindings,
            ["actions"] = new JsonArray
            {
                new JsonObject { ["name"] = TipAction, ["type"] = "pose" },
                new JsonObject { ["name"] = ClickAction, ["type"] = "boolean" }
            },
            ["action_sets"] = new JsonArray
            {
                new JsonObject { ["name"] = AimSet, ["usage"] = "single" },
                new JsonObject { ["name"] = ClickSet, ["usage"] = "single" }
            },
            ["localization"] = new JsonArray
            {
                new JsonObject
                {
                    ["language_tag"] = "en_US",
                    [AimSet] = "SteamFrame HUD aim", [TipAction] = "Aim", [ClickSet] = "SteamFrame HUD click", [ClickAction] = "Click"
                }
            }
        };
        var path = Path.Combine(directory, "actions.json");
        WriteIfChanged(path, manifest.ToJsonString(Pretty));
        return path;
    }

    private static JsonObject Binding(string type, string appKey)
    {
        static JsonObject Pose(string hand) => new() { ["output"] = TipAction, ["path"] = $"/user/hand/{hand}/pose/tip" };
        static JsonObject Trigger(string hand) => new()
        {
            ["inputs"] = new JsonObject { ["click"] = new JsonObject { ["output"] = ClickAction } },
            ["mode"] = "button",
            ["path"] = $"/user/hand/{hand}/input/trigger"
        };
        return new JsonObject
        {
            ["action_manifest_version"] = 0,
            ["alias_info"] = new JsonObject(),
            ["app_key"] = appKey,
            ["bindings"] = new JsonObject
            {
                [AimSet] = new JsonObject { ["poses"] = new JsonArray { Pose("left"), Pose("right") } },
                [ClickSet] = new JsonObject { ["sources"] = new JsonArray { Trigger("left"), Trigger("right") } }
            },
            ["category"] = "steamvr_input",
            ["controller_type"] = type,
            ["description"] = "Aim and trigger for the SteamFrame HUD laser while the dashboard is closed.",
            ["name"] = "SteamFrame HUD",
            ["options"] = new JsonObject(),
            ["simulated_actions"] = new JsonArray()
        };
    }

    private static void WriteIfChanged(string path, string text)
    {
        try { if (File.Exists(path) && File.ReadAllText(path) == text) return; } catch (IOException) { /* rewritten below */ }
        File.WriteAllText(path, text);
    }
}
