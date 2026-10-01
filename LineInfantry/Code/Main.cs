using NeoModLoader.api;

namespace LineInfantry
{
    public class ModClass : BasicMod<ModClass>
    {
        public static ModConfig Config;

        protected override void OnModLoad()
        {
            Config = GetConfig();
            MusketAssets.init();
            LinePatches.init();
            LogInfo("LineInfantry v0.1 loaded: 四级火枪与线列战术已就绪");
        }
    }
}
