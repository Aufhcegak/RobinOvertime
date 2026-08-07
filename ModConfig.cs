namespace RobinOvertime
{
    /// <summary>可配置项(GenericModConfigMenu 可调)。</summary>
    internal class ModConfig
    {
        /// <summary>右键罗宾时询问是否补款减工期。</summary>
        public bool RobinRightClickEnabled { get; set; } = true;

        /// <summary>加班费 = 建筑(或升级项)建造费 × 该倍数。</summary>
        public float FeeMultiplier { get; set; } = 2.0f;
    }
}
