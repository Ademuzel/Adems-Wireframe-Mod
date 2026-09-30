using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace WireframeMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.Ademuzel.wireframemod";
        public const string PluginName = "WireframeMod";
        public const string PluginVersion = "1.1.0";

        public static ConfigEntry<bool> ConfigEnabled;
        public static ConfigEntry<bool> ConfigRGB;
        public static ConfigEntry<float> ConfigRGBSpeed;
        public static ConfigEntry<float> ConfigRGBSpan;
        public static ConfigEntry<bool> ConfigShareWithOthers;
        public static ConfigEntry<bool> ConfigShowOthers;
        private Harmony _harmony;

        private void Awake()
        {
            ConfigEnabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "Turn the wireframe effect on or off.");

            ConfigRGB = Config.Bind(
                "General",
                "RGB",
                true,
                "Rainbow gradient from the top of the body to the bottom.");

            ConfigRGBSpeed = Config.Bind(
                "General",
                "RGBSpeed",
                0.25f,
                "How fast the rainbow scrolls downward (0 = static).");

            ConfigRGBSpan = Config.Bind(
                "General",
                "RGBSpan",
                1f,
                "How many full rainbows fit top to bottom.");

            ConfigShareWithOthers = Config.Bind(
                "Multiplayer",
                "ShareWithOthers",
                true,
                "Let other players who have this mod see your wireframe.");

            ConfigShowOthers = Config.Bind(
                "Multiplayer",
                "ShowOthers",
                true,
                "Show the wireframe on other players who have this mod.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            var managerObj = new GameObject("WireframeMod_Manager");
            DontDestroyOnLoad(managerObj);
            managerObj.AddComponent<WireframeLocalController>();

            Logger.LogInfo("WireframeMod (networked) loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
