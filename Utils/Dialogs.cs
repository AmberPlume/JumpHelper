using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace JumpHelper.Utils;

/// <summary>
/// WinForms 文件对话框工具：游戏主线程非 STA，所有对话框必须在新 STA 线程上弹窗。
/// 调用在游戏主线程阻塞等待结果（弹窗期间 UI 冻结属预期——多数 Dalamud 插件同模式）。
/// </summary>
public static class Dialogs
{
    /// <summary>选择文件（打开对话框）。</summary>
    public static string? PickFile(string title, string filter)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            using var dlg = new OpenFileDialog { Title = title, Filter = filter };
            if (dlg.ShowDialog() == DialogResult.OK)
                result = dlg.FileName;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    /// <summary>选择保存路径（保存对话框）。</summary>
    public static string? SaveFile(string title, string filter, string defaultName)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            using var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
            if (dlg.ShowDialog() == DialogResult.OK)
                result = dlg.FileName;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    /// <summary>选择目录（文件夹选择对话框）。</summary>
    public static string? PickFolder(string title)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
            if (dlg.ShowDialog() == DialogResult.OK)
                result = dlg.SelectedPath;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }
}
