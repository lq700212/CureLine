// 全工程测试串行：Core 大量静态全局（MesConfig/MesParamStore/Language），
// 并行跑会互相污染。测试体量小，串行无压力。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
