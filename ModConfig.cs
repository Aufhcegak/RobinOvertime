namespace RobinOvertime
{
    /// <summary>可配置项(GenericModConfigMenu 可调)。</summary>
    internal class ModConfig
    {
        /// <summary>建造/升级后回到农场时自动弹窗询问是否加班。</summary>
        public bool AutoPromptEnabled { get; set; } = true;

        /// <summary>右键罗宾时询问是否补款减工期。</summary>
        public bool RobinRightClickEnabled { get; set; } = true;

        /// <summary>加班费 = 建筑(或升级项)建造费 × 该倍数。</summary>
        public float FeeMultiplier { get; set; } = 2.0f;

        /// <summary>自动弹窗只在玩家位于农场(主农场/姜岛)时触发,避免矿洞/镇上突然定身。</summary>
        public bool FarmOnlyPrompt { get; set; } = true;
    }
}
