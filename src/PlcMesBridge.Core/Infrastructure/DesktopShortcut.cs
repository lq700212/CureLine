// =========================================================================
// DesktopShortcut：桌面快捷方式（.lnk）的纯逻辑 + 薄封装（供设置窗 + 测试共用）
//
// 干什么：给程序在桌面建一个快捷方式（双击直启 exe，图标取 exe 内嵌图标）。
// 为什么独立成类：WPF 层只做"事件→控件"搬运（项目约定），业务进 Core 可测；
//   路径拼接是纯函数，单元测试不碰真实桌面；真正写 .lnk 的只有 Create 一处。
// 怎么改：
//   - 快捷方式名默认取 exe 文件名（不带扩展，如 CimGateway.lnk），改名即改 exe 名；
//     如需固定中文名，调 BuildShortcutPath 后自行改文件名再 Create。
//   - 图标不单独指定文件：IconLocation 指向 exe 自身（",0" 取第一个内嵌图标，
//     即 csproj 的 ApplicationIcon = Assets/app.ico），换图标只换 Assets/app.ico
//     重新编译即可，快捷方式自动跟新，不用重建。
//   - 已存在不覆盖（用户可能手动改过目标/备注），返回 AlreadyExists，界面提示即可。
//   - Create 走 WScript.Shell 的 late-bound COM（不引用 IWshRuntimeLibrary，
//     net8 下免 COM 引用，干净）；仅支持 Windows（本项目 net8.0-windows 本来就是）。
// =========================================================================

namespace PlcMesBridge.Core.Infrastructure;

public static class DesktopShortcut
{
    /// <summary>
    /// 拼快捷方式完整路径。纯函数（不碰磁盘，方便单测）。
    /// 例：folder="C:\Users\a\Desktop", exe="D:\app\CimGateway.exe"
    ///   → "C:\Users\a\Desktop\CimGateway.lnk"。
    /// </summary>
    public static string BuildShortcutPath(string folder, string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrWhiteSpace(name))
            name = "CimGateway"; // exe 路径异常时的兜底名，保证总能拼出合法路径
        return Path.Combine(folder, name + ".lnk");
    }

    /// <summary>
    /// 取图标定位串（指向 exe 自身第一个内嵌图标）。纯函数。
    /// ",0" = exe 内第 0 个图标资源（即 ApplicationIcon），快捷方式建一次终身跟随。
    /// </summary>
    public static string BuildIconLocation(string exePath) => exePath + ",0";

    /// <summary>
    /// 一站式：在桌面建快捷方式。WPF 层只调这一个。
    /// 已存在 → (AlreadyExists, path)，不覆盖；新建 → (Created, path)。
    /// exePath 传 Environment.ProcessPath（当前 exe 全路径），
    /// desktopDir 传 Environment.GetFolderPath(Desktop)。
    /// COM 异常（WScript 被禁等）直接上抛，调用方弹框提示（本方法不吞异常，方便排查）。
    /// </summary>
    public static (ShortcutResult Result, string Path) EnsureOnDesktop(
        string exePath, string desktopDir, string? description = null)
    {
        string lnk = BuildShortcutPath(desktopDir, exePath);
        if (File.Exists(lnk))
            return (ShortcutResult.AlreadyExists, lnk);
        Create(exePath, lnk, description);
        return (ShortcutResult.Created, lnk);
    }

    /// <summary>
    /// 真正写 .lnk 文件（唯一碰磁盘/COM 的地方）。
    /// description 为空则默认 "PLC通信"（与主窗标题一致）。
    /// </summary>
    public static void Create(string exePath, string shortcutPath, string? description = null)
    {
        // late-bound：免 PIAs 引用；dynamic 调用 CreateShortcut/保存。
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            throw new InvalidOperationException("WScript.Shell 不可用（仅支持 Windows）。");
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
                shortcut.IconLocation = BuildIconLocation(exePath);
                shortcut.Description = description ?? "PLC通信";
                shortcut.Save();
            }
            finally
            {
                // WScript COM 对象用完即放，避免 RuntimeCallableWrapper 悬空占文件句柄。
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}

/// <summary>EnsureOnDesktop 的结果：新建 / 本来就有（未覆盖）。</summary>
public enum ShortcutResult
{
    Created,
    AlreadyExists,
}
