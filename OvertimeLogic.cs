using System;
using StardewValley.GameData.Buildings;

namespace RobinOvertime
{
    /// <summary>纯计算逻辑(零游戏依赖)。与 ModEntry 分离,供 logic_test 控制台工程无头测试;
    /// ModEntry 里的游戏交互代码一律不碰,只调用这里的纯函数。</summary>
    internal static class OvertimeLogic
    {
        /// <summary>配置倍数下限(与 GMCM 滑块一致)。</summary>
        internal const float MinFeeMultiplier = 1.0f;

        /// <summary>配置倍数上限(与 GMCM 滑块一致)。</summary>
        internal const float MaxFeeMultiplier = 3.0f;

        /// <summary>GMCM 滑块的步进(1.0~3.0 每 0.1 一档,共 21 档)。与 ClampFeeMultiplier 边界必须一致,测试会互查。</summary>
        internal const float FeeMultiplierInterval = 0.1f;

        /// <summary>把配置倍数收进 [1.0, 3.0]:GMCM 滑块已限制,但 config.json 手改会绕过 UI,这里兜底。</summary>
        internal static float ClampFeeMultiplier(float multiplier)
        {
            if (float.IsNaN(multiplier) || multiplier < MinFeeMultiplier)
                return MinFeeMultiplier;
            if (multiplier > MaxFeeMultiplier)
                return MaxFeeMultiplier;
            return multiplier;
        }

        /// <summary>加班费 = 原价 × 倍数(默认 2.0)。用 double 运算后四舍五入(半分向上),避免
        /// float 直接截断在非整数倍数时差 1 元(例:65000 × 1.9f ≈ 123499.998 → (int) 会截成 123499)。
        /// 精度策略:GMCM 滑块按 0.1 步进,float 无法精确表示 1.3(实际存的是 1.29999995…),
        /// 直接乘会在 .5 分界处因表示误差差 1 元(例:12345 × 1.3f = 16048.4994… 被舍成 16048,
        /// 而滑块显示 1.3 用户预期 16048.5 → 16049)。因此滑块档位内(误差 &lt; 1e-4)先量化到
        /// 十分位整数再乘,行为与显示一致;档位外(手改 config.json 的非 0.1 步进值,如 2.05)
        /// 按原始数学值计算,不强行吸附到最近档位。</summary>
        internal static int CalcFee(int buildCost, float multiplier)
        {
            float clamped = ClampFeeMultiplier(multiplier);
            double scaled = clamped * 10.0;
            int tenths = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (Math.Abs(scaled - tenths) < 1e-4)
                return (int)Math.Round(buildCost * (double)tenths / 10.0, MidpointRounding.AwayFromZero);
            return (int)Math.Round(buildCost * (double)clamped, MidpointRounding.AwayFromZero);
        }

        /// <summary>剩余工期减半(整数除法向下取整:2 → 1,3 → 1,4 → 2,5 → 2);≤0 保持 0。
        /// 调用方保证先判 ShouldFinishImmediately(剩 1 天直接完工,不会走到这里)。</summary>
        internal static int ReduceRemainingDays(int remaining)
        {
            return remaining <= 0 ? 0 : remaining / 2;
        }

        /// <summary>只剩最后 1 天(明天一早完工)→ 补款后立即完工,不再减半。</summary>
        internal static bool ShouldFinishImmediately(int remainingDays)
        {
            return remainingDays == 1;
        }

        /// <summary>原版房屋升级价:按当前等级镜像 GameLocation.cs houseUpgradeAccept() 的硬编码价格
        /// (1.6 无数据文件,原版也是写死的:0→1 一万、1→2 六万五、2→3 十万;原版到 3 级后不再提供升级)。</summary>
        internal static int GetHouseUpgradeCost(int houseUpgradeLevel)
        {
            if (houseUpgradeLevel <= 0)
                return 10000;
            if (houseUpgradeLevel == 1)
                return 65000;
            return 100000;
        }

        /// <summary>多项目一次只问一个:农场建筑优先,房屋升级其次,都没有则 None(放行原版对话)。</summary>
        internal static OvertimeTargetKind SelectOvertimeTarget(bool hasActiveBuilding, bool houseUpgradeInProgress)
        {
            if (hasActiveBuilding)
                return OvertimeTargetKind.Building;
            if (houseUpgradeInProgress)
                return OvertimeTargetKind.HouseUpgrade;
            return OvertimeTargetKind.None;
        }

        /// <summary>现金是否够付加班费(不够 → 拒绝并提示,不扣款)。</summary>
        internal static bool CanAfford(int money, int fee)
        {
            return money >= fee;
        }

        /// <summary>对话回答是否为"是"(其他任何回答都放行原版对话)。</summary>
        internal static bool WantsOvertime(string answer)
        {
            return answer == "Yes";
        }

        /// <summary>是否罗宾的施工项目(按建造者区分,排除法师的魔法建筑):原版 BuildingData.Builder 字段默认值就是 "Robin"。</summary>
        internal static bool IsRobinBuilder(BuildingData data)
        {
            return data?.Builder == "Robin";
        }

        /// <summary>联机:只有主机(IsMainPlayer)在付款成功后广播结果给客机。</summary>
        internal static bool ShouldBroadcastResult(bool isMainPlayer)
        {
            return isMainPlayer;
        }

        /// <summary>联机:客机收到广播后弹 HUD(世界就绪且本机不是主机;主机自己本地已弹,不重复)。</summary>
        internal static bool ShouldShowReceivedHud(bool isWorldReady, bool isMainPlayer)
        {
            return isWorldReady && !isMainPlayer;
        }

        /// <summary>联机消息识别:发件 mod 的 UniqueID 与消息类型都匹配才算我们的广播。</summary>
        internal static bool IsOvertimeResultMessage(string fromModId, string expectedModId, string messageType)
        {
            return fromModId == expectedModId && messageType == OvertimeMessage.Type;
        }

        /// <summary>结果 HUD 文案键:完工用 finishedEarly,减半用 reducedTime。
        /// 主机本地 HUD、客机广播 HUD、i18n 三处共用这一个映射,防写散失配。</summary>
        internal static string GetResultHudKey(bool finished)
        {
            return finished ? "finishedEarly" : "reducedTime";
        }
    }

    /// <summary>联机广播的消息契约(发送与接收共用同一个常量,避免两侧写散后失配)。SMAPI 用 JSON 序列化载荷。</summary>
    internal static class OvertimeMessage
    {
        internal const string Type = "RobinOvertimeResult";
    }

    /// <summary>右键罗宾时本次要询问的加班目标。</summary>
    internal enum OvertimeTargetKind
    {
        None,
        Building,
        HouseUpgrade
    }
}
