using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Newtonsoft.Json;
using StardewValley.GameData.Buildings;
using RobinOvertime;

// ============================================================
// RobinOvertime 纯逻辑回归测试 —— 无头,不启动游戏。
// 被测对象:OvertimeLogic(零游戏依赖的纯函数)+ ModConfig 默认值
// + OvertimeResultMessage 联机载荷(Newtonsoft round-trip,SMAPI 同款序列化器)
// + ModEntry 补丁接线(反射结构校验)+ i18n 键完整性。
// 经主工程 InternalsVisibleTo("logic_test") 暴露 internal 成员。
// 跑法:cd logic_test && dotnet run -c Release
// ============================================================

int fails = 0, pass = 0;
void Check(string name, bool ok, string detail = null)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : "  << " + detail));
    if (ok) pass++; else fails++;
}
void CheckEq<T>(string name, T got, T expected)
{
    bool ok = Equals(got, expected);
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : $"  << got={got} expected={expected}"));
    if (ok) pass++; else fails++;
}

// ---------- 工具:i18n 读取与键提取 ----------
string FindI18nFile(string name)
{
    // 从 bin/Release/net6.0 上溯到仓库根,或 cwd 上溯(不同跑法都覆盖)
    string[] roots =
    {
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..")),
        Environment.CurrentDirectory
    };
    foreach (string root in roots)
    {
        string p = Path.Combine(root, "i18n", name);
        if (File.Exists(p)) return p;
    }
    return null;
}
Dictionary<string, string> LoadI18n(string path)
{
    return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
}
HashSet<string> ExtractTranslationKeys(string sourceText)
{
    // 覆盖 ModTranslation.Get("...") 与 Helper.Translation.Get("...") 两种调用形式
    var keys = new HashSet<string>();
    foreach (Match m in Regex.Matches(sourceText, @"Translation\.Get\(""([^""]+)"""))
        keys.Add(m.Groups[1].Value);
    return keys;
}

// ============================================================
// 第一组:默认配置 —— 倍数默认 2.0,右键开关默认开
// ============================================================
{
    var cfg = new ModConfig();
    CheckEq("cfg: FeeMultiplier 默认 2.0", cfg.FeeMultiplier, 2.0f);
    Check("cfg: RobinRightClickEnabled 默认开", cfg.RobinRightClickEnabled);
}

// ============================================================
// 第二组:加班费计算 —— 原价 × 倍数,含边界 1.0 / 3.0
// ============================================================
{
    CheckEq("fee: 一万 × 2.0", OvertimeLogic.CalcFee(10000, 2.0f), 20000);
    CheckEq("fee: 六万五 × 2.0", OvertimeLogic.CalcFee(65000, 2.0f), 130000);
    CheckEq("fee: 十万 × 2.0", OvertimeLogic.CalcFee(100000, 2.0f), 200000);
    CheckEq("fee: 边界 × 1.0", OvertimeLogic.CalcFee(10000, 1.0f), 10000);
    CheckEq("fee: 边界 × 3.0", OvertimeLogic.CalcFee(10000, 3.0f), 30000);
    CheckEq("fee: 零成本", OvertimeLogic.CalcFee(0, 2.0f), 0);
    CheckEq("fee: 奇数成本", OvertimeLogic.CalcFee(12345, 2.0f), 24690);
    // 非整数倍数 × 大成本:float 直乘截断会差 1 元(65000 × 1.9f ≈ 123499.998 → 截断 123499),
    // 回归:必须四舍五入到数学结果 123500
    CheckEq("fee: 回归 float 截断差 1 元 (65000×1.9)", OvertimeLogic.CalcFee(65000, 1.9f), 123500);
    CheckEq("fee: 回归 float 截断 (10000×1.1)", OvertimeLogic.CalcFee(10000, 1.1f), 11000);
    // 四舍五入半分向上:12345 × 1.3 = 16048.5 → 16049。
    // 回归:1.3f 实际存的是 1.29999995…,直接乘得 16048.4994… 会被舍成 16048(差 1 元);
    // 修复:滑块档位内先量化到 0.1 的十分位整数,再按用户看到的档位算。
    CheckEq("fee: 半分向上", OvertimeLogic.CalcFee(12345, 1.3f), 16049);
    // 档位外(手改 config 的 2.05,float 实际 2.04999995…)不吸附档位,按原始数学值算:12345 × 2.05 = 25307.25 → 25307
    CheckEq("fee: 档位外 2.05 按原始值", OvertimeLogic.CalcFee(12345, 2.05f), 25307);
    // 档位内恰好在 0.5 分界:1.5 → 15 档,费 = 成本 × 1.5
    CheckEq("fee: 1.5 档半分向上 (12345×1.5=18517.5→18518)", OvertimeLogic.CalcFee(12345, 1.5f), 18518);
}

// ============================================================
// 第三组:倍数钳制 —— config.json 手改可绕过 GMCM 滑块,这里兜底到 [1.0, 3.0]
// ============================================================
{
    CheckEq("clamp: 下限 1.0", OvertimeLogic.ClampFeeMultiplier(1.0f), 1.0f);
    CheckEq("clamp: 上限 3.0", OvertimeLogic.ClampFeeMultiplier(3.0f), 3.0f);
    CheckEq("clamp: 中间不动", OvertimeLogic.ClampFeeMultiplier(2.0f), 2.0f);
    CheckEq("clamp: 低于下限抬到 1.0", OvertimeLogic.ClampFeeMultiplier(0.5f), 1.0f);
    CheckEq("clamp: 高于上限压到 3.0", OvertimeLogic.ClampFeeMultiplier(5.0f), 3.0f);
    CheckEq("clamp: 负数抬到 1.0", OvertimeLogic.ClampFeeMultiplier(-2.0f), 1.0f);
    CheckEq("clamp: NaN 兜底到 1.0", OvertimeLogic.ClampFeeMultiplier(float.NaN), 1.0f);

    CheckEq("clamp-fee: 0.5 被钳到 1.0", OvertimeLogic.CalcFee(10000, 0.5f), 10000);
    CheckEq("clamp-fee: 5.0 被钳到 3.0", OvertimeLogic.CalcFee(10000, 5.0f), 30000);
}

// ============================================================
// 第四组:工期减半 —— 整数除法向下取整,奇数天怎么处理
// ============================================================
{
    CheckEq("half: 剩 2 天 → 1", OvertimeLogic.ReduceRemainingDays(2), 1);
    CheckEq("half: 剩 3 天(奇数)→ 1", OvertimeLogic.ReduceRemainingDays(3), 1);
    CheckEq("half: 剩 4 天 → 2", OvertimeLogic.ReduceRemainingDays(4), 2);
    CheckEq("half: 剩 5 天(奇数)→ 2", OvertimeLogic.ReduceRemainingDays(5), 2);
    CheckEq("half: 剩 6 天 → 3", OvertimeLogic.ReduceRemainingDays(6), 3);
    CheckEq("half: 剩 7 天(奇数)→ 3", OvertimeLogic.ReduceRemainingDays(7), 3);
    CheckEq("half: 剩 1 天 → 0(正常流程先判完工不会走到)", OvertimeLogic.ReduceRemainingDays(1), 0);
    CheckEq("half: 剩 0 天 → 0", OvertimeLogic.ReduceRemainingDays(0), 0);
    CheckEq("half: 负数 → 0", OvertimeLogic.ReduceRemainingDays(-1), 0);
}

// ============================================================
// 第五组:剩最后一天立即完工 —— 只认恰好 1 天
// ============================================================
{
    Check("finish: 剩 1 天立即完工", OvertimeLogic.ShouldFinishImmediately(1));
    Check("finish: 剩 2 天不完工", !OvertimeLogic.ShouldFinishImmediately(2));
    Check("finish: 剩 0 天不完工", !OvertimeLogic.ShouldFinishImmediately(0));
    Check("finish: 负数不完工", !OvertimeLogic.ShouldFinishImmediately(-1));
}

// ============================================================
// 第六组:房屋升级分级费用 —— 第 1/2/3 次 1万/6.5万/10万
// (镜像原版 GameLocation.cs:12825-12870 houseUpgradeAccept 硬编码:
//  等级0→10000(+450木材)、1→65000(+100硬木)、2→100000,无 case 3)
// ============================================================
{
    CheckEq("upgrade: 第 1 次(等级 0)一万", OvertimeLogic.GetHouseUpgradeCost(0), 10000);
    CheckEq("upgrade: 第 2 次(等级 1)六万五", OvertimeLogic.GetHouseUpgradeCost(1), 65000);
    CheckEq("upgrade: 第 3 次(等级 2)十万", OvertimeLogic.GetHouseUpgradeCost(2), 100000);
    CheckEq("upgrade: 超原版上限(等级 3)仍十万", OvertimeLogic.GetHouseUpgradeCost(3), 100000);
    CheckEq("upgrade: 负数按第 1 次", OvertimeLogic.GetHouseUpgradeCost(-1), 10000);
    // 与默认倍数组合:第 3 次 × 2.0 = 二十万
    CheckEq("upgrade-fee: 第 3 次 × 2.0", OvertimeLogic.CalcFee(OvertimeLogic.GetHouseUpgradeCost(2), 2.0f), 200000);
    // 与原版天数一致:原版升级工期 3 天(houseUpgradeAccept 里 daysUntilHouseUpgrade.Value = 3)
    CheckEq("upgrade: 原版升级工期 3 天 → 加班减半到 1", OvertimeLogic.ReduceRemainingDays(3), 1);
}

// ============================================================
// 第七组:多项目一次只问一个 —— 建筑优先、房屋升级其次、都没有放行原版
// ============================================================
{
    CheckEq("select: 建筑+房屋升级都在 → 问建筑", OvertimeLogic.SelectOvertimeTarget(true, true), OvertimeTargetKind.Building);
    CheckEq("select: 只有建筑 → 问建筑", OvertimeLogic.SelectOvertimeTarget(true, false), OvertimeTargetKind.Building);
    CheckEq("select: 只有房屋升级 → 问房屋升级", OvertimeLogic.SelectOvertimeTarget(false, true), OvertimeTargetKind.HouseUpgrade);
    CheckEq("select: 都没有 → 放行原版", OvertimeLogic.SelectOvertimeTarget(false, false), OvertimeTargetKind.None);
}

// ============================================================
// 第八组:组合语义 —— 把减半/完工判定串起来模拟完整决策流
// ============================================================
{
    // 剩 3 天:不立即完工 → 减半到 1 天
    int d3 = 3;
    bool im3 = OvertimeLogic.ShouldFinishImmediately(d3);
    Check("combo: 剩 3 天不完工", !im3);
    if (!im3)
        CheckEq("combo: 剩 3 天加班后剩 1 天", OvertimeLogic.ReduceRemainingDays(d3), 1);

    // 剩 1 天:立即完工(不走减半)
    int d1 = 1;
    Check("combo: 剩 1 天立即完工", OvertimeLogic.ShouldFinishImmediately(d1));

    // 剩 4 天:减半到 2 天,再减半到 1 天,再加班立即完工 —— 三次加班把 4 天工期变 0
    int days = 4;
    Check("combo: 4 天不立即完工", !OvertimeLogic.ShouldFinishImmediately(days));
    days = OvertimeLogic.ReduceRemainingDays(days);
    CheckEq("combo: 第一次减半 4 → 2", days, 2);
    days = OvertimeLogic.ReduceRemainingDays(days);
    CheckEq("combo: 第二次减半 2 → 1", days, 1);
    Check("combo: 第三次直接完工", OvertimeLogic.ShouldFinishImmediately(days));
}

// ============================================================
// 第九组:联机广播消息契约 —— SMAPI 用 Newtonsoft 序列化载荷,
// 这里按同一序列化器做 round-trip + 主机/客机语义矩阵
// ============================================================
{
    CheckEq("msg: Type 常量", OvertimeMessage.Type, "RobinOvertimeResult");

    var m1 = new OvertimeResultMessage { ProjectName = "Stable", Fee = 20000, Finished = false };
    string j1 = JsonConvert.SerializeObject(m1);
    var r1 = JsonConvert.DeserializeObject<OvertimeResultMessage>(j1);
    CheckEq("msg: round-trip ProjectName", r1.ProjectName, "Stable");
    CheckEq("msg: round-trip Fee", r1.Fee, 20000);
    Check("msg: round-trip Finished=false", !r1.Finished);
    Check("msg: JSON 含字段名(跨版本兼容)", j1.Contains("\"ProjectName\"") && j1.Contains("\"Fee\"") && j1.Contains("\"Finished\""));

    var m2 = new OvertimeResultMessage { ProjectName = "Big Barn", Fee = 320000, Finished = true };
    var r2 = JsonConvert.DeserializeObject<OvertimeResultMessage>(JsonConvert.SerializeObject(m2));
    CheckEq("msg: round-trip 大额 Fee", r2.Fee, 320000);
    Check("msg: round-trip Finished=true", r2.Finished);
    CheckEq("msg: round-trip 中文项目名", JsonConvert.DeserializeObject<OvertimeResultMessage>(
        JsonConvert.SerializeObject(new OvertimeResultMessage { ProjectName = "咱家的房子", Fee = 130000, Finished = false })).ProjectName, "咱家的房子");

    // 消息过滤:mod 的 UniqueID 与类型都必须匹配
    Check("msg: 匹配 mod+type 通过", OvertimeLogic.IsOvertimeResultMessage("XiePe.RobinOvertime", "XiePe.RobinOvertime", "RobinOvertimeResult"));
    Check("msg: 异 mod 拒绝(如 LookupAnything)", !OvertimeLogic.IsOvertimeResultMessage("Pathoschild.LookupAnything", "XiePe.RobinOvertime", "RobinOvertimeResult"));
    Check("msg: 异 type 拒绝", !OvertimeLogic.IsOvertimeResultMessage("XiePe.RobinOvertime", "XiePe.RobinOvertime", "RobinResult"));
    Check("msg: 空发件 mod 拒绝", !OvertimeLogic.IsOvertimeResultMessage(null, "XiePe.RobinOvertime", "RobinOvertimeResult"));

    // 主机/客机语义:主机付款成功后广播;客机收到弹 HUD;主机本地已弹不重复
    Check("msg: 主机广播", OvertimeLogic.ShouldBroadcastResult(true));
    Check("msg: 客机不广播", !OvertimeLogic.ShouldBroadcastResult(false));
    Check("msg: 客机收到弹 HUD", OvertimeLogic.ShouldShowReceivedHud(true, false));
    Check("msg: 主机不重复弹 HUD", !OvertimeLogic.ShouldShowReceivedHud(true, true));
    Check("msg: 世界未就绪不弹", !OvertimeLogic.ShouldShowReceivedHud(false, false));

    // 客机 HUD 文案键:完工 → finishedEarly,减半 → reducedTime
    CheckEq("msg: HUD 键 完工", OvertimeLogic.GetResultHudKey(true), "finishedEarly");
    CheckEq("msg: HUD 键 减半", OvertimeLogic.GetResultHudKey(false), "reducedTime");
}

// ============================================================
// 第十组:GMCM 配置面一致性 —— 滑块边界/步进与钳制逻辑互查(21 档)
// ============================================================
{
    CheckEq("gmcm: Min=1.0", OvertimeLogic.MinFeeMultiplier, 1.0f);
    CheckEq("gmcm: Max=3.0", OvertimeLogic.MaxFeeMultiplier, 3.0f);
    CheckEq("gmcm: Interval=0.1", OvertimeLogic.FeeMultiplierInterval, 0.1f);
    int slots = (int)Math.Round((OvertimeLogic.MaxFeeMultiplier - OvertimeLogic.MinFeeMultiplier) / OvertimeLogic.FeeMultiplierInterval) + 1;
    CheckEq("gmcm: 滑块共 21 档", slots, 21);

    // 21 个档位逐一互查:档位值 clamp 后不变;费 = 成本 × 档位(量化与滑块一致,无 1 元误差)
    bool gridOk = true;
    for (int i = 0; i < slots; i++)
    {
        float v = OvertimeLogic.MinFeeMultiplier + i * OvertimeLogic.FeeMultiplierInterval;
        if (OvertimeLogic.ClampFeeMultiplier(v) != v)
            gridOk = false;
        int expect = (int)Math.Round(10000 * v, MidpointRounding.AwayFromZero);
        if (OvertimeLogic.CalcFee(10000, v) != expect)
            gridOk = false;
    }
    Check("gmcm: 21 档 clamp 与费互查一致", gridOk);
}

// ============================================================
// 第十一组:BuildingData 纯函数 —— 建造者判定(null 安全、原版默认值)
// ============================================================
{
    var robinData = new BuildingData { Builder = "Robin", BuildCost = 50000, Name = "Coop" };
    Check("bdata: Builder=Robin 算罗宾施工", OvertimeLogic.IsRobinBuilder(robinData));
    Check("bdata: Wizard 魔法建筑不算", !OvertimeLogic.IsRobinBuilder(new BuildingData { Builder = "Wizard" }));
    Check("bdata: Builder=null 不算", !OvertimeLogic.IsRobinBuilder(new BuildingData { Builder = null }));
    Check("bdata: null 数据不算(null 安全)", !OvertimeLogic.IsRobinBuilder(null));
    Check("bdata: 全新数据默认 Builder=Robin(原版约定)", OvertimeLogic.IsRobinBuilder(new BuildingData()));

    // 与 vanilla houseUpgradeAccept(GameLocation.cs:12825-12870)逐档对照
    CheckEq("bdata: vanilla 对照 0→10000", OvertimeLogic.GetHouseUpgradeCost(0), 10000);
    CheckEq("bdata: vanilla 对照 1→65000", OvertimeLogic.GetHouseUpgradeCost(1), 65000);
    CheckEq("bdata: vanilla 对照 2→100000", OvertimeLogic.GetHouseUpgradeCost(2), 100000);
}

// ============================================================
// 第十二组:多模块联动整体流程(集成测试)——
// 把排序→取费→现金判定→扣款→减半/完工→广播→客机收包弹 HUD 串成完整状态机
// ============================================================
{
    // ---- 场景 A:主机,农场建筑在建(剩 5 天),房屋无升级 ----
    var kindA = OvertimeLogic.SelectOvertimeTarget(hasActiveBuilding: true, houseUpgradeInProgress: false);
    CheckEq("integ:A 排序 → 问建筑", kindA, OvertimeTargetKind.Building);
    int daysA = 5;
    Check("integ:A 5 天不立即完工", !OvertimeLogic.ShouldFinishImmediately(daysA));
    int feeA = OvertimeLogic.CalcFee(50000, 2.0f);
    CheckEq("integ:A 加班费 50000×2=100000", feeA, 100000);
    Check("integ:A 现金够付", OvertimeLogic.CanAfford(150000, feeA));
    CheckEq("integ:A 扣款后现金 50000", 150000 - feeA, 50000);
    daysA = OvertimeLogic.ReduceRemainingDays(daysA);
    CheckEq("integ:A 减半 5→2 天", daysA, 2);
    Check("integ:A 主机付款成功 → 广播", OvertimeLogic.ShouldBroadcastResult(true));

    // ---- 场景 B:房屋升级进行中(第 2 次,原价 6.5 万,剩 3 天),客机视角 ----
    CheckEq("integ:B 排序 → 问房屋升级", OvertimeLogic.SelectOvertimeTarget(false, true), OvertimeTargetKind.HouseUpgrade);
    int feeB = OvertimeLogic.CalcFee(OvertimeLogic.GetHouseUpgradeCost(1), 2.0f);
    CheckEq("integ:B 65000×2=130000", feeB, 130000);
    Check("integ:B 客机不广播", !OvertimeLogic.ShouldBroadcastResult(false));
    Check("integ:B 客机收到弹 HUD", OvertimeLogic.ShouldShowReceivedHud(true, false));
    Check("integ:B 主机不重复弹", !OvertimeLogic.ShouldShowReceivedHud(true, true));
    CheckEq("integ:B 剩 3 天减半 → 1", OvertimeLogic.ReduceRemainingDays(3), 1);

    // ---- 场景 C:钱不够 → 拒绝(流程语义:CanAfford=false 时 ModEntry 直接 return,不扣款、不广播、天数不变) ----
    Check("integ:C 现金不足拒绝", !OvertimeLogic.CanAfford(100, 20000));
    int daysC = 4; // ModEntry 流程在 CanAfford=false 时直接 return,不调 ReduceRemainingDays
    CheckEq("integ:C 拒绝后天数保持 4", daysC, 4);

    // ---- 场景 D:剩最后一天 → 立即完工 + finished 消息贯通主机→客机 ----
    Check("integ:D 剩 1 天立即完工", OvertimeLogic.ShouldFinishImmediately(1));
    CheckEq("integ:D 完工 HUD 键 finishedEarly", OvertimeLogic.GetResultHudKey(true), "finishedEarly");
    CheckEq("integ:D 减半 HUD 键 reducedTime", OvertimeLogic.GetResultHudKey(false), "reducedTime");

    var hostMsg = new OvertimeResultMessage { ProjectName = "Deluxe Coop", Fee = 260000, Finished = true };
    Check("integ:D 主机广播判定", OvertimeLogic.ShouldBroadcastResult(true));
    string wire = JsonConvert.SerializeObject(hostMsg); // SMAPI SendMessage 同款序列化
    var guestMsg = JsonConvert.DeserializeObject<OvertimeResultMessage>(wire);
    CheckEq("integ:D 客机收到 项目名", guestMsg.ProjectName, "Deluxe Coop");
    CheckEq("integ:D 客机收到 费用", guestMsg.Fee, 260000);
    Check("integ:D 客机收到 完工标记", guestMsg.Finished);
    Check("integ:D 客机过滤通过", OvertimeLogic.IsOvertimeResultMessage("XiePe.RobinOvertime", "XiePe.RobinOvertime", OvertimeMessage.Type));
    Check("integ:D 客机 HUD 用完工键", OvertimeLogic.GetResultHudKey(guestMsg.Finished) == "finishedEarly");
    Check("integ:D 减半消息 HUD 用减半键",
        OvertimeLogic.GetResultHudKey(JsonConvert.DeserializeObject<OvertimeResultMessage>(
            JsonConvert.SerializeObject(new OvertimeResultMessage { ProjectName = "Coop", Fee = 10000, Finished = false })).Finished) == "reducedTime");
}

// ============================================================
// 第十三组:补丁接线结构校验(反射)—— 防重构时 ModEntry/OvertimeLogic 失配
// ============================================================
{
    Assembly asm = typeof(ModEntry).Assembly;
    Type patcher = asm.GetType("RobinOvertime.RobinDialoguePatcher");
    Check("wire: RobinDialoguePatcher 存在", patcher != null);
    // 两个补丁方法都是 internal static(不是 public),必须用 NonPublic 才能查到
    MethodInfo prefix = patcher?.GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
    Check("wire: Prefix 存在且返回 bool(拦截语义)", prefix != null && prefix.ReturnType == typeof(bool));
    Check("wire: BypassOriginal 字段存在(防递归)", patcher?.GetField("BypassOriginal", BindingFlags.NonPublic | BindingFlags.Static) != null);
    Type cpm = asm.GetType("RobinOvertime.CarpenterMenuPatcher");
    Check("wire: RobinConstructionPostfix 存在", cpm?.GetMethod("RobinConstructionPostfix", BindingFlags.NonPublic | BindingFlags.Static) != null);

    Type msgType = asm.GetType("RobinOvertime.OvertimeResultMessage");
    Check("wire: OvertimeResultMessage 存在", msgType != null);
    Check("wire: 载荷有 ProjectName", msgType?.GetProperty("ProjectName") != null);
    Check("wire: 载荷有 Fee", msgType?.GetProperty("Fee") != null);
    Check("wire: 载荷有 Finished", msgType?.GetProperty("Finished") != null);

    const BindingFlags stat = BindingFlags.NonPublic | BindingFlags.Static;
    Check("wire: ModEntry.Config 静态配置字段", typeof(ModEntry).GetField("Config", stat) != null);
    Check("wire: GetFee 接线", typeof(ModEntry).GetMethod("GetFee", stat) != null);
    Check("wire: GetHouseUpgradeFee 接线", typeof(ModEntry).GetMethod("GetHouseUpgradeFee", stat) != null);
    Check("wire: GetBuildingDisplayName 接线", typeof(ModEntry).GetMethod("GetBuildingDisplayName", stat) != null);
    Check("wire: FindActiveConstruction 接线", typeof(ModEntry).GetMethod("FindActiveConstruction", stat) != null);
    Check("wire: FindBuildingForBlueprint 接线", typeof(ModEntry).GetMethod("FindBuildingForBlueprint", stat) != null);
    Check("wire: IsRobinConstruction 接线", typeof(ModEntry).GetMethod("IsRobinConstruction", stat) != null);
    Check("wire: IsHouseUpgradeInProgress 接线", typeof(ModEntry).GetMethod("IsHouseUpgradeInProgress", stat) != null);
    Check("wire: Entry 入口", typeof(ModEntry).GetMethod("Entry", BindingFlags.Public | BindingFlags.Instance) != null);
    Check("wire: OnModMessageReceived 入口", typeof(ModEntry).GetMethod("OnModMessageReceived", BindingFlags.NonPublic | BindingFlags.Instance) != null);
}

// ============================================================
// 第十四组:i18n 键完整性 —— ModEntry 用到的键必须同时存在于
// default.json 与 zh.json,且两种语言键集合一致(防漏译/防错键)
// ============================================================
{
    string defPath = FindI18nFile("default.json");
    string zhPath = FindI18nFile("zh.json");
    Check("i18n: 找到 default.json", defPath != null, defPath ?? "(未找到)");
    Check("i18n: 找到 zh.json", zhPath != null, zhPath ?? "(未找到)");
    if (defPath != null && zhPath != null)
    {
        var def = LoadI18n(defPath);
        var zh = LoadI18n(zhPath);
        string modEntryPath = Path.Combine(Path.GetDirectoryName(defPath), "..", "ModEntry.cs");
        var used = ExtractTranslationKeys(File.ReadAllText(modEntryPath));
        CheckEq("i18n: ModEntry 引用键数", used.Count, 13);
        bool allInDef = used.All(k => def.ContainsKey(k));
        bool allInZh = used.All(k => zh.ContainsKey(k));
        Check("i18n: 用到的键全部在 default.json", allInDef, allInDef ? null : string.Join(",", used.Where(k => !def.ContainsKey(k))));
        Check("i18n: 用到的键全部在 zh.json", allInZh, allInZh ? null : string.Join(",", used.Where(k => !zh.ContainsKey(k))));
        Check("i18n: default 与 zh 键集合一致(无漏译)", def.Keys.OrderBy(k => k).SequenceEqual(zh.Keys.OrderBy(k => k)));
        CheckEq("i18n: 键总数", def.Count, zh.Count);
    }
}

Console.WriteLine($"\n总计: PASS={pass} FAIL={fails}");
return fails == 0 ? 0 : 1;
