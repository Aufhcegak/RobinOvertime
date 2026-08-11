# RobinOvertime 测试进度 (TEST_PROGRESS.md)

> 无头测试 + 找 bug + 修 bug 的续跑记录。后任 agent 先读本文件,再决定从哪续。
> 最后更新:2026-08-11。当前状态:**全部完成,全绿**。

## 当前状态(速览)
- 测试工程:`logic_test/`(Program.cs + test.csproj),控制台无头,不启动游戏。
- 测试结果:**136 PASS / 0 FAIL**(`cd logic_test && dotnet run -c Release`)。
- 主工程 `dotnet build -c Release`:**0 错误**,dll 已自动部署到
  `D:\steam\steamapps\common\Stardew Valley\Mods\RobinOvertime\`(csproj 的 DeployModFiles target)。
- 版本:manifest.json 1.4.0;csproj Version 已对齐 1.4.0。

## 历史阶段记录
1. **[续跑前提] 前 agent 已完成的**:抽出 `OvertimeLogic.cs`(纯逻辑,零游戏依赖)、
   建好 `logic_test/` 工程、ModEntry.cs 已改为调用 OvertimeLogic、csproj 已加 InternalsVisibleTo("logic_test")。
   但:没有 TEST_PROGRESS.md;harness 跑不起来(见下)。
2. **[harness bug 修复] RobinOvertime.csproj**:`logic_test\**` 没从主库编译排除,
   Program.cs 顶级语句被 glob 进库 → CS8805。已加 `<Compile/None/Content Remove="logic_test\**">`。
3. **[harness bug 修复] logic_test/test.csproj**:运行时缺 StardewModdingAPI.dll 的解析
   (FileNotFoundException v4.5.1.0)。根因:SMAPI 只是主工程的间接引用,不进 deps.json,
   默认 ALC 不解析(dll 在输出目录也没用)。修复:test.csproj 直接
   `<Reference>` StardewModdingAPI.dll + smapi-internal\SMAPI.Toolkit.CoreInterfaces.dll
   (作为 "reference" 类型进 deps.json);CopyGameDlls target 补上 SMAPI.Toolkit.CoreInterfaces.dll。
4. **[真 bug 修复] CalcFee 半分向上差 1 元**:`12345 × 1.3f` 得 16048,期望 16049。
   根因:1.3f 实际存 1.29999995…,直接 double 乘后 16048.4994… 被舍成 16048。
   修复:滑块档位内(误差<1e-4)先量化到 0.1 十分位整数再乘(与 GMCM 显示一致);
   档位外(手改 config 如 2.05)按原始数学值算,不吸附。回归测试 `fee: 半分向上`。
5. **[可测性抽取] OvertimeLogic.GetResultHudKey(bool finished)**:客机 HUD 键选择
   (finishedEarly/reducedTime)原来内联在 ModEntry.OnModMessageReceived,抽出供测试,
   ModEntry 改为调用(一处,最小侵入)。i18n、主机本地 HUD、广播 HUD 三处共用。
6. **[测试扩展] Program.cs 扩到 14 组共 136 用例**,覆盖见下。
7. **[对齐] csproj Version 1.0.0 → 1.4.0**(与 manifest 一致;SMAPI 只读 manifest,无行为影响)。

## 测试覆盖(14 组,136 用例)
1. ModConfig 默认值(2.0 / 右键开)
2. 加班费:×2.0、边界 1.0/3.0、零成本、奇数成本、float 截断回归(65000×1.9=123500)、半分向上
3. 倍数钳制 [1.0,3.0]:0.5/5.0/负数/NaN 兜底
4. 工期减半:2→1、3→1(奇数)、4→2、5→2、7→3、0/负数不变
5. 剩最后一天立即完工:只认恰好 1
6. 房屋升级分级费用 10000/65000/100000(已对照 sdv-src GameLocation.cs:12823-12870 houseUpgradeAccept 硬编码)+ 原版升级工期 3 天减半到 1
7. 多项目排序:建筑优先 > 房屋升级 > 放行原版
8. 组合语义:3 天→1、1 天完工、4 天三连加班→0
9. 联机消息契约:Newtonsoft round-trip(SMAPI 同款序列化器)、中文项目名、字段名稳定、
   过滤矩阵(异 mod/异 type/空发件)、主机广播/客机不广播、客机弹 HUD/主机不重复弹/世界未就绪不弹
10. GMCM 配置面一致性:Min/Max/Interval 与 ClampFeeMultiplier 互查、21 档逐一核对费无 1 元误差
11. BuildingData 纯函数:Builder=Robin 判定(null 安全、Wizard 排除、默认值)
12. 多模块集成流程:场景 A(建筑,主机,5 天:排序→取费→现金→扣款→减半→广播)、
    B(房屋升级 6.5 万,客机视角)、C(钱不够拒绝,天数不变)、D(剩 1 天完工,finished 消息
    主机→JSON→客机→HUD 键贯通)
13. 反射接线校验:Harmony 补丁类/方法存在、BypassOriginal、OvertimeResultMessage 三属性、
    ModEntry 各 internal 接线入口、Entry/OnModMessageReceived
14. i18n 键完整性:ModEntry 引用的 13 个键全部存在于 default.json 与 zh.json,两语言键集合一致

## 无法无头测试的功能(原因)
- 游戏交互:Harmony 实际注入、createQuestionDialogue、HUD 弹出、扣款、FinishConstruction、
  FinishHouseUpgradeNow(需 Game1 运行时/存档)。已用反射接线校验 + 纯逻辑集成流程兜住决策面。
- FindActiveConstruction/FindBuildingForBlueprint/IsRobinConstruction/IsHouseUpgradeInProgress/
  GetHouseUpgradeFee/TryGetUpgradeData(需 Game1.locations / Game1.player Net 字段 / Game1.buildingData)。
- GetBuildCost/GetFee(需 Building 实例,其构造依赖游戏数据加载)。
- 真实 GMCM 页注册(需 GMCM 运行时);真实联机收发(需双客户端)。GMCM 兼容点用
  配置常量一致性测试覆盖;LookupAnything 兼容点用"异 mod 消息被过滤"断言覆盖。

## 遗留风险
- FinishHouseUpgradeNow 是原版 dayupdate 完成块的镜像,未在真实存档执行验证(逻辑与
  sdv-src Farmer.cs 完成块逐行对照过)。
- 联机载荷 JSON 字段名稳定(round-trip 已验证),SMAPI 跨版本二进制兼容未验证。
- 档位外手改 config 值(如 2.05)按原始数学值计算、不吸附档位——已文档化并有测试。
- 主工程构建有 Mono.Cecil 版本冲突警告(MSB3277,ModBuildConfig vs SMAPI 引用),非错误,照常产出。

## 续跑指引
- 全部完成。后任如需继续:改完逻辑后跑
  `cd C:\Users\xiepe\stardew-tools\RobinOvertime\logic_test && dotnet run -c Release`(必须全绿),
  再 `cd C:\Users\xiepe\stardew-tools\RobinOvertime && dotnet build -c Release`(0 错误,自动部署)。
- 新增纯逻辑一律放 OvertimeLogic.cs(internal static,零游戏依赖),ModEntry 只调用。
- 改 csproj 注意:`logic_test\**` 已从主库排除;test.csproj 已直接引用 SMAPI 两个 dll。
