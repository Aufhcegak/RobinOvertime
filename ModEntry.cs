using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;

namespace RobinOvertime
{
    /// <summary>罗宾加班:①建造/升级后自动追问,付 2 倍建造费第二天一早完工;②右键罗宾时优先问"要不要补交加班费减半天数"(剩最后一天则立即完工),点"算了"回原版对话。</summary>
    public class ModEntry : Mod
    {
        /// <summary>记在建筑 modData 上的键,标记"已经问过加班"(防止重复追问,也随存档持久化)。</summary>
        internal const string AskedKey = "XiePe.RobinOvertime/Asked";

        /// <summary>记在建筑 modData 上的键,值=当天日期(TotalDays),右键补款当天只问一次,第二天重置。</summary>
        internal const string AskedDayKey = "XiePe.RobinOvertime/AskedDay";

        /// <summary>每隔多少 tick 扫一次工地(1 tick ≈ 1/60 秒)。</summary>
        private const int ScanIntervalTicks = 15;

        /// <summary>静态引用,供 Harmony 补丁类(无 Mod 实例上下文)访问日志与翻译。</summary>
        private static IMonitor ModMonitor;

        /// <summary>静态翻译器引用(同上)。</summary>
        private static ITranslationHelper ModTranslation;

        /// <summary>静态联机消息助手(广播加班询问给客机)。</summary>
        private static IMultiplayerHelper ModMultiplayer;

        /// <summary>静态 mod 唯一 ID(发联机消息时用作收件人过滤)。</summary>
        private static string ModUniqueId;

        /// <summary>当前配置(GMCM 可调)。</summary>
        internal static ModConfig Config;

        private IGenericModConfigMenuApi configMenu;

        private int tickCounter;

        public override void Entry(IModHelper helper)
        {
            ModMonitor = this.Monitor;
            ModTranslation = helper.Translation;
            ModMultiplayer = helper.Multiplayer;
            ModUniqueId = this.ModManifest.UniqueID;
            Config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;

            Harmony harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), nameof(NPC.checkAction)),
                prefix: new HarmonyMethod(typeof(RobinDialoguePatcher), nameof(RobinDialoguePatcher.Prefix))
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
                () => Config.AutoPromptEnabled,
                val => Config.AutoPromptEnabled = val,
                () => this.Helper.Translation.Get("cfg.auto-prompt"),
                () => this.Helper.Translation.Get("cfg.auto-prompt.tooltip"));
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
            this.configMenu.AddBoolOption(this.ModManifest,
                () => Config.FarmOnlyPrompt,
                val => Config.FarmOnlyPrompt = val,
                () => this.Helper.Translation.Get("cfg.farm-only"),
                () => this.Helper.Translation.Get("cfg.farm-only.tooltip"));
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (++this.tickCounter < ScanIntervalTicks)
                return;
            this.tickCounter = 0;
            // 只有主机(单人=主机)能改建筑状态;菜单/事件开着时不能弹问题框
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
                return;
            if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.fadeToBlack)
                return;
            if (!Config.AutoPromptEnabled)
                return;

            // 默认只在玩家位于农场时问(主农场/姜岛):矿洞、镇上等地点突然定身弹窗会打断操作;
            // 可配置关闭该限制
            if (Config.FarmOnlyPrompt)
            {
                GameLocation playerLocation = Game1.player.currentLocation;
                if (playerLocation == null || (!playerLocation.IsFarm && playerLocation.Name != "IslandFarm"))
                    return;
            }

            foreach (GameLocation location in Game1.locations)
            {
                if (!location.IsFarm && location.Name != "IslandFarm")
                    continue;

                foreach (Building building in location.buildings)
                {
                    if (!IsRobinConstruction(building))
                        continue;

                    // 只剩最后一天(明天一早完工)的不用问:付钱也没有收益,白白花钱
                    if (building.daysOfConstructionLeft.Value == 1 || building.daysUntilUpgrade.Value == 1)
                        continue;

                    if (building.modData.ContainsKey(AskedKey))
                        continue;

                    this.Monitor.Log($"命中在建建筑 [{building.buildingType.Value}] 施工天数={building.daysOfConstructionLeft.Value} 升级天数={building.daysUntilUpgrade.Value} 地点={location.Name}", LogLevel.Debug);

                    // 注意:这里不预打标记 —— 如果弹框被别的界面(如睡觉确认框)覆盖,
                    // 玩家等于没看到;标记移到玩家回答之后,被覆盖则第二天继续问
                    AskOvertime(building);
                    return; // 一次只问一个,避免连环弹框
                }
            }
        }

        /// <summary>客机收到主机的加班询问广播 → HUD 提示(客机看不到主机的问题框)。</summary>
        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModUniqueId || e.Type != "OvertimeAsked")
                return;
            if (!Context.IsWorldReady)
                return;

            string buildingName = e.ReadAs<string>();
            Game1.addHUDMessage(new HUDMessage(ModTranslation.Get("farmhandNotice", new
            {
                buildingName
            })));
        }

        /// <summary>是否罗宾正在施工(新建在建 或 升级中),且建造者确实是罗宾(排除法师的魔法建筑)。</summary>
        internal static bool IsRobinConstruction(Building building)
        {
            if (building.daysOfConstructionLeft.Value <= 0 && building.daysUntilUpgrade.Value <= 0)
                return false;
            return building.GetData()?.Builder == "Robin";
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

        /// <summary>弹出罗宾的追问:是否加班。加班费 = 建筑(或升级项)原价 × 2。无论钱够不够都弹窗,钱不够由回调提示。</summary>
        private static void AskOvertime(Building building)
        {
            BuildingData data = building.GetData();
            if (data == null)
                return;

            string buildingName = GetBuildingDisplayName(building);
            int fee = GetFee(building, data);

            ModMonitor.Log($"AskOvertime: [{buildingName}] fee={fee} 玩家现金={Game1.player.Money} 当前位置={Game1.currentLocation?.Name}", LogLevel.Debug);

            string question = ModTranslation.Get("question", new
            {
                buildingName,
                fee = Utility.getNumberWithCommas(fee)
            });

            Response[] responses =
            {
                new Response("Yes", ModTranslation.Get("payOption")),
                new Response("No", ModTranslation.Get("cancelOption"))
            };

            Game1.currentLocation.createQuestionDialogue(
                question,
                responses,
                delegate (Farmer who, string answer)
                {
                    OnAnswered(who, answer, building);
                }
                // 不挂罗宾头像:她本人不在农场,隔空问话挂脸反而出戏(文案已按"托话"口吻)
            );
            ModMonitor.Log("AskOvertime: 已调用 createQuestionDialogue", LogLevel.Debug);

            // 联机广播:客机看不到主机的问题框,提示一下"主机正在决定加班"
            ModMultiplayer.SendMessage(buildingName, "OvertimeAsked", new[] { ModUniqueId });
        }

        /// <summary>自动弹窗的玩家回答:选"是"且有足够钱 → 扣 2 倍费用,把剩余工期改成 1 天(明天一早完工)。</summary>
        private static void OnAnswered(Farmer who, string answer, Building building)
        {
            // 无论选什么,问过就不再自动弹(钱不够时撤销,下次有钱继续问)
            building.modData[AskedKey] = "true";

            if (answer != "Yes")
                return;

            BuildingData data = building.GetData();
            if (data == null)
                return;

            int fee = GetFee(building, data);
            string buildingName = GetBuildingDisplayName(building);

            if (who.Money < fee)
            {
                building.modData.Remove(AskedKey); // 付不起,撤销标记:攒够钱后下次继续问
                Game1.playSound("cancel"); // 明显的失败反馈,别让玩家以为点了没反应
                Game1.addHUDMessage(new HUDMessage(ModTranslation.Get("notEnoughMoney", new
                {
                    fee = Utility.getNumberWithCommas(fee)
                }), HUDMessage.error_type));
                return;
            }

            who.Money -= fee;
            if (building.daysOfConstructionLeft.Value > 0)
                building.daysOfConstructionLeft.Value = 1;
            if (building.daysUntilUpgrade.Value > 0)
                building.daysUntilUpgrade.Value = 1;

            Game1.playSound("coin");
            Game1.addHUDMessage(new HUDMessage(ModTranslation.Get("paid", new
            {
                buildingName,
                fee = Utility.getNumberWithCommas(fee)
            })));
        }

        /// <summary>右键罗宾的补款对话:付 2 倍建造费 → 剩余工期减半;只剩最后 1 天则立即完工。选"算了"回原版对话。</summary>
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
            // 问过就不再自动弹(钱不够时撤销,下次继续可问);并记"当天已问",当天右键不再追问
            building.modData[AskedKey] = "true";
            building.modData[AskedDayKey] = Game1.Date.TotalDays.ToString();

            if (answer != "Yes")
            {
                // 点"算了":回到原版默认对话(罗宾当前无可说内容时原版也默认"不能对话",行为一致)
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
                building.modData.Remove(AskedKey);
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

        /// <summary>正在建的那个东西的名字:升级中的话显示升级目标名,否则显示建筑名。</summary>
        internal static string GetBuildingDisplayName(Building building)
        {
            if (TryGetUpgradeData(building, out BuildingData upgradeData))
                return upgradeData.Name;
            return building.GetData()?.Name ?? building.buildingType.Value;
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
        /// <summary>为 true 时放行原版 NPC.checkAction(供"算了"回调重放原版对话,防递归)。</summary>
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
            if (Game1.CurrentEvent != null || __instance.CurrentDialogue.Count > 0)
                return true; // 节日/剧情对话优先:不要盖掉原版节日对话或待定剧情对话

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
}
