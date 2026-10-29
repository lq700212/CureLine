// =========================================================================
// CuringPolicyTests：工艺策略（三级覆盖解析 / 校验规则 / 存储自愈）
// 锁规则：仓位 > 层 > 全局；值 ≤0 视为没配；策略名库内唯一。
// =========================================================================

using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class CuringPolicyTests
{
    // ---------- 三级覆盖解析 ----------

    [Fact]
    public void Resolve_SlotOverridesLevelOverridesGlobal()
    {
        var policy = new CuringPolicy
        {
            Name = "T",
            GlobalMinutes = 120,
            LevelMinutes = { [2] = 90 },
            SlotMinutes = { [CuringPolicy.SlotKey(3, 2)] = 45 },
        };
        Assert.Equal(45, policy.ResolveMinutes(3, 2));  // 仓位覆盖最优先
        Assert.Equal(90, policy.ResolveMinutes(1, 2));  // 没仓位覆盖 → 层覆盖
        Assert.Equal(120, policy.ResolveMinutes(1, 1)); // 都没有 → 全局
    }

    [Fact]
    public void Resolve_MissingSlotFallsBackLevelThenGlobal()
    {
        var policy = new CuringPolicy
        {
            Name = "T",
            GlobalMinutes = 60,
            LevelMinutes = { [3] = 30 },
        };
        Assert.Equal(30, policy.ResolveMinutes(7, 3));
        Assert.Equal(60, policy.ResolveMinutes(7, 1));
    }

    [Fact]
    public void Resolve_NonPositiveTreatedAsMissing()
    {
        // 值 ≤0 视为没配（脏数据不生效），逐级回退而不是放行 0 分钟
        var policy = new CuringPolicy
        {
            Name = "T",
            GlobalMinutes = 60,
            LevelMinutes = { [1] = 0 },
            SlotMinutes = { ["2-1"] = -5 },
        };
        Assert.Equal(60, policy.ResolveMinutes(2, 1));
        Assert.Equal(60, policy.ResolveMinutes(1, 1));
    }

    // ---------- 校验 ----------

    [Fact]
    public void Validate_RejectsBadPolicy()
    {
        var policy = new CuringPolicy
        {
            Name = "",
            GlobalMinutes = 0,
            LevelMinutes = { [5] = 30, [1] = 0 },       // 层号越界 + 时长非正
            SlotMinutes = { ["abc"] = 30, ["200-2"] = 10, ["1-1"] = -1 }, // 键坏 + 架越界 + 时长非正
        };
        var check = CuringPolicyValidator.Validate(policy);
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("策略名"));
        Assert.Contains(check.Errors, e => e.Contains("全局时长"));
        Assert.Contains(check.Errors, e => e.Contains("层号 5"));
        Assert.Contains(check.Errors, e => e.Contains("层1"));
        Assert.Contains(check.Errors, e => e.Contains("abc"));
        Assert.Contains(check.Errors, e => e.Contains("200-2"));
        Assert.Contains(check.Errors, e => e.Contains("1-1"));
    }

    [Fact]
    public void ValidateBook_RejectsDuplicateName()
    {
        var book = new CuringPolicyBook
        {
            Policies = { new CuringPolicy { Name = "A" }, new CuringPolicy { Name = "a " } },
        };
        var check = CuringPolicyValidator.ValidateBook(book);
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("重复"));
    }

    [Fact]
    public void ValidateBook_EmptyLibrary_Rejected()
    {
        var check = CuringPolicyValidator.ValidateBook(new CuringPolicyBook());
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("至少要有一个"));
    }

    // ---------- 激活解析 ----------

    [Fact]
    public void ResolveActive_MatchesNameIgnoringCaseAndSpaces()
    {
        var book = new CuringPolicyBook
        {
            Policies = { new CuringPolicy { Name = "标准固化" }, new CuringPolicy { Name = "快固" } },
            ActiveName = " 快固 ",
        };
        Assert.Equal("快固", book.ResolveActive()!.Name);
    }

    [Fact]
    public void ResolveActive_MissingNameFallsBackToFirst()
    {
        var book = new CuringPolicyBook
        {
            Policies = { new CuringPolicy { Name = "甲" }, new CuringPolicy { Name = "乙" } },
            ActiveName = "不存在的",
        };
        Assert.Equal("甲", book.ResolveActive()!.Name);
        Assert.Null(new CuringPolicyBook().ResolveActive()); // 空库回 null（调用方兜底）
    }

    // ---------- 存储（TestScope 隔离临时目录） ----------

    [Fact]
    public void Store_RoundTrip_KeepsPoliciesAndActiveName()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var book = new CuringPolicyBook
        {
            Policies =
            {
                new CuringPolicy
                {
                    Name = "标准固化",
                    GlobalMinutes = 120,
                    LevelMinutes = { [1] = 90 },
                    SlotMinutes = { ["3-2"] = 45 },
                    GrillGate = true,
                },
                new CuringPolicy { Name = "快固", GlobalMinutes = 30 },
            },
            ActiveName = "快固",
        };
        CuringPolicyStore.Save(book);

        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Policies.Count);
        Assert.Equal("快固", loaded.ActiveName);
        var p = loaded.Policies[0];
        Assert.Equal(120, p.GlobalMinutes);
        Assert.Equal(90, p.LevelMinutes[1]);
        Assert.Equal(45, p.SlotMinutes["3-2"]);
        Assert.True(p.GrillGate);
    }

    [Fact]
    public void Store_LoadMissing_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var reasons = new List<string>();
        Assert.Null(CuringPolicyStore.Load(reasons: reasons));
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void Store_LoadCorrupted_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(CuringPolicyStore.DefaultPath, "{ 不是 JSON");
        var reasons = new List<string>();
        Assert.Null(CuringPolicyStore.Load(reasons: reasons));
        Assert.Contains(reasons, r => r.Contains("损坏"));
    }
}
