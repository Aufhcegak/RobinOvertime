using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.Menus;
using StardewValley.TokenizableStrings;

namespace RobinOvertime
{
    /// <summary>右键罗宾时询问是否加班补款:付 建造费×2 的加班费 → 剩余工期减半;剩最后一天则立即完工;点"不用了"回原版对话。</summary>
    public class ModEntry : Mod
    {
        /// <summary>记在建筑 modData 上的键,值=当天日期(TotalDays),右键补款当天只问一次,第二天重置。</summary>
        internal const string AskedDayKey = "XiePe.RobinOvertime/AskedDay";

        /// <summary>静态引用,供 Harmony 补丁类(无 Mod 实例上下文)访问日志与翻译。</summary>
        private static IMonitor ModMonitor;

        /// <summary>静态翻译器引用(同上)。</summary>
        private static ITranslationHelper ModTranslation;

        /// <summary>当前配置(GMCM 可调)。</summary>
        internal static ModConfig Config;

        private IGenericModConfigMenuApi configMenu;

        public override void Entry(IModHelper helper)
        {
            ModMonitor = this.Monitor;
            ModTranslation = helper.Translation;
            Config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;

            Harmony harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), nameof(NPC.checkAction)),
                prefix: new HarmonyMethod(typeof(RobinDialoguePatcher), nameof(RobinDialoguePatcher.Prefix))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(CarpenterMenu), nameof(CarpenterMenu.robinConstructionMessage)),
                postfix: new HarmonyMethod(typeof(CarpenterMenuPatcher), nameof(CarpenterMenuPatcher.RobinConstructionPostfix))
            );
        }

        /// <summary>注册 GMCM 配置页(未装 GMCM 时静默跳过)。</summary>
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            this.configMenu = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (this.configMenu == null)
                return;

            this.configMenu.Unregister(this.ModManifest);
            this.configMenu.Register(
                this.ModManifest,
                () => Config = new ModConfig(),
                () => this.Helper.WriteConfig(Config)
            );
            this.configMenu.SetTitleScreenOnlyForNextOptions(this.ModManifest, false);

            this.configMenu.AddSectionTitle(this.ModManifest,
                () => this.Helper.Translation.Get("cfg.section"));

            this.configMenu.AddBoolOption(this.ModManifest,
                () => Config.RobinRightClickEnabled,
                val => Config.RobinRightClickEnabled = val,
                () => this.Helper.Translation.Get("cfg.robin-click"),
                () => this.Helper.Translation.Get("cfg.robin-click.tooltip"));
            this.configMenu.AddNumberOption(this.ModManifest,
                () => Config.FeeMultiplier,
                val => Config.FeeMultiplier = val,
                () => this.Helper.Translation.Get("cfg.fee-multiplier"),
                () => this.Helper.Translation.Get("cfg.fee-multiplier.tooltip"),
                min: 1.0f,
                max: 3.0f,
                interval: 0.1f);
        }

        /// <summary>在所有农场地点(主农场 + 姜岛农场)里找到第一个"罗宾正在施工"的建筑,没有则返回 null。</summary>
        internal static Building FindActiveConstruction()
        {
            foreach (GameLocation location in Game1.locations)
            {
                if (!location.IsFarm && location.Name != "IslandFarm")
                    continue;

                foreach (Building building in location.buildings)
                {
                    if (IsRobinConstruction(building))
                        return building;
                }
            }
            return null;
        }

        /// <summary>按蓝图 ID 精确找"刚建/刚升级"的在建建筑(新建按 buildingType,升级按 upgradeName 匹配)。</summary>
        internal static Building FindBuildingForBlueprint(string blueprintId)
        {
            foreach (GameLocation location in Game1.locations)
            {
                if (!location.IsFarm && location.Name != "IslandFarm")
                    continue;

                foreach (Building building in location.buildings)
                {
                    if (!IsRobinConstruction(building))
                        continue;
                    if (building.buildingType.Value == blueprintId || building.upgradeName.Value == blueprintId)
                        return building;
                }
            }
            return null;
        }

        /// <summary>是否罗宾正在施工(新建在建 或 升级中),且建造者确实是罗宾(排除法师的魔法建筑)。</summary>
        internal static bool IsRobinConstruction(Building building)
        {
            if (building.daysOfConstructionLeft.Value <= 0 && building.daysUntilUpgrade.Value <= 0)
                return false;
            return building.GetData()?.Builder == "Robin";
        }

        /// <summary>右键罗宾的补款对话:付 2 倍建造费 → 剩余工期减半;只剩最后 1 天则立即完工。选"不用了"回原版对话。</summary>
        internal static void AskOvertimeViaRobin(NPC robin, Farmer who, GameLocation location, Building building)
        {
            BuildingData data = building.GetData();
            if (data == null)
                return;

            string buildingName = GetBuildingDisplayName(building);
            int fee = GetFee(building, data);

            ModMonitor.Log($"AskOvertimeViaRobin: [{buildingName}] fee={fee} 玩家现金={who.Money}", LogLevel.Debug);

            location.createQuestionDialogue(
                ModTranslation.Get("overtimeQuestion", new
                {
                    buildingName,
                    fee = Utility.getNumberWithCommas(fee)
                }),
                new Response[]
                {
                    new Response("Yes", ModTranslation.Get("overtimeYes")),
                    new Response("No", ModTranslation.Get("overtimeNo"))
                },
                delegate (Farmer who2, string answer)
                {
                    OnRobinAnswered(robin, who2, location, building, fee, buildingName, answer);
                },
                robin
            );
        }

        /// <summary>右键补款的回答处理:是 → 扣款、减半天数或立即完工;否 → 放行原版 NPC.checkAction(默认对话)。</summary>
        private static void OnRobinAnswered(NPC robin, Farmer who, GameLocation location, Building building, int fee, string buildingName, string answer)
        {
            // 记"当天已问",当天右键不再追问(第二天重置)
            building.modData[AskedDayKey] = Game1.Date.TotalDays.ToString();

            if (answer != "Yes")
            {
                // 点"不用了":回到原版默认对话(罗宾当前无可说内容时原版也默认"不能对话",行为一致)
                RobinDialoguePatcher.BypassOriginal = true;
                try
                {
                    robin.checkAction(who, location);
                }
                finally
                {
                    RobinDialoguePatcher.BypassOriginal = false;
                }
                return;
            }

            if (who.Money < fee)
            {
                Game1.playSound("cancel"); // 明显的失败反馈
                Game1.addHUDMessage(new HUDMessage(ModTranslation.Get("notEnoughMoney", new
                {
                    fee = Utility.getNumberWithCommas(fee)
                }), HUDMessage.error_type));
                return;
            }

            who.Money -= fee;
            Game1.playSound("coin");

            bool lastDay = building.daysOfConstructionLeft.Value == 1 || building.daysUntilUpgrade.Value == 1;
            if (lastDay)
            {
                // 只剩最后一天(明天一早完工)→ 补款后立即完工,走原版完工流程
                building.FinishConstruction();
                Game1.addHUDMessage(new HUDMessage(ModTranslation.Get("finishedEarly", new
                {
                    buildingName,
                    fee = Utility.getNumberWithCommas(fee)
                })));
            }
            else
            {
                // 减半天数:剩 2 → 1,剩 3 → 1,剩 4 → 2
                if (building.daysOfConstructionLeft.Value > 0)
                    building.daysOfConstructionLeft.Value /= 2;
                if (building.daysUntilUpgrade.Value > 0)
                    building.daysUntilUpgrade.Value /= 2;
                Game1.addHUDMessage(new HUDMessage(ModTranslation.Get("reducedTime", new
                {
                    buildingName,
                    fee = Utility.getNumberWithCommas(fee)
                })));
            }
        }

        /// <summary>正在建的那个东西的名字:升级中的话显示升级目标名,否则显示建筑名。名字是 token 文本,必须 TokenParser 解析成显示文本(原版 BuildingSkinMenu.cs:96 同款)。</summary>
        internal static string GetBuildingDisplayName(Building building)
        {
            if (TryGetUpgradeData(building, out BuildingData upgradeData))
                return TokenParser.ParseText(upgradeData.Name);
            BuildingData data = building.GetData();
            return TokenParser.ParseText(data?.Name ?? building.buildingType.Value);
        }

        /// <summary>钱数:升级中的话按升级项的原价算,否则按建筑原价算。</summary>
        internal static int GetBuildCost(Building building, BuildingData data)
        {
            if (TryGetUpgradeData(building, out BuildingData upgradeData))
                return upgradeData.BuildCost;
            return data.BuildCost;
        }

        /// <summary>加班费 = 建筑(或升级项)建造费 × 配置倍数(默认 2.0)。</summary>
        internal static int GetFee(Building building, BuildingData data)
        {
            return (int)(GetBuildCost(building, data) * Config.FeeMultiplier);
        }

        /// <summary>升级中且能在 Data/Buildings 找到升级目标时返回 true,并输出其数据。</summary>
        internal static bool TryGetUpgradeData(Building building, out BuildingData upgradeData)
        {
            upgradeData = null;
            if (building.daysUntilUpgrade.Value > 0 && !string.IsNullOrEmpty(building.upgradeName.Value))
                return Game1.buildingData.TryGetValue(building.upgradeName.Value, out upgradeData);
            return false;
        }
    }

    /// <summary>Harmony 补丁:拦截右键罗宾,有在建建筑时优先弹补款问题框。</summary>
    internal static class RobinDialoguePatcher
    {
        /// <summary>为 true 时放行原版 NPC.checkAction(供"不用了"回调重放原版对话,防递归)。</summary>
        internal static bool BypassOriginal;

        internal static bool Prefix(NPC __instance, Farmer who, GameLocation l, ref bool __result)
        {
            if (BypassOriginal)
                return true;
            if (who == null)
                return true;

            // 只拦罗宾、本地玩家、世界就绪;送礼优先(原版先处理手持物品)
            if (__instance.Name != "Robin" || !who.IsLocalPlayer)
                return true;
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
                return true;
            if (!ModEntry.Config.RobinRightClickEnabled)
                return true; // 配置里关闭了右键补款
            if (who.ActiveObject != null)
                return true;
            if (!who.CanMove)
                return true; // 对齐原版 NPC.checkAction 的前置检查
            if (Game1.eventUp || Game1.fadeToBlack)
                return true;
            // 注意:不再放行"罗宾有待定对话"—— 刚建完建筑罗宾会压入一段完工对话,
            // 放行的话要连点好几段原版对话才轮到加班框;加班框必须绝对优先
            // (节日时罗宾不在店建不了建筑,FindActiveConstruction 自然为空,不会盖节日对话)

            // 弹窗/对话进行中(activeClickableMenu 非空):吞掉本次右键,避免问题框与
            // 原版对话在连点右键时交替覆盖(此时放行原版会用默认对话盖掉加班问题框)
            if (Game1.activeClickableMenu != null)
            {
                __result = true;
                return false;
            }

            Building target = ModEntry.FindActiveConstruction();
            if (target == null)
                return true;

            // 当天已问过这个建筑 → 放行原版对话(右键补款当天只问一次,第二天重置)
            if (target.modData.TryGetValue(ModEntry.AskedDayKey, out string askedDay) && askedDay == Game1.Date.TotalDays.ToString())
                return true;

            // 注意:这里不预打标记 —— 回答(是/否)后才标记,见 OnRobinAnswered
            __result = true; // 本次右键已被我们的对话消费
            ModEntry.AskOvertimeViaRobin(__instance, who, l, target);
            return false;
        }
    }

    /// <summary>Harmony 补丁:建造/升级完成时,把原版自动弹的"建造中"对话替换成加班问题框(顺着建造流程直接弹,不用再右键罗宾)。</summary>
    internal static class CarpenterMenuPatcher
    {
        internal static void RobinConstructionPostfix(CarpenterMenu __instance)
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
                return;
            if (__instance.Blueprint == null || __instance.Blueprint.MagicalConstruction)
                return; // 魔法建筑瞬建,不弹

            Building target = ModEntry.FindBuildingForBlueprint(__instance.Blueprint.Id);
            if (target == null)
                return;

            // 替换原版"建造中"对话:此时原版 DrawDialogue 刚把对话压入 CurrentDialogue 并弹框,
            // 这里 createQuestionDialogue 会用加班问题框覆盖它;玩家点"不用了"时 CurrentDialogue
            // 里那一段会作为原版默认对话正常弹出
            ModEntry.AskOvertimeViaRobin(
                Game1.getCharacterFromName("Robin"),
                Game1.player,
                Game1.currentLocation,
                target
            );
        }
    }
}
