using System.Diagnostics;
using System.Text.Json;

namespace UnityModuleTool;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ToolWindow());
    }
}

sealed partial class ToolWindow : Form
{
    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityModuleTool");
    private static readonly string History = Path.Combine(AppData, "editors.json");
    private readonly ComboBox editors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox version = new() { Width = 132, PlaceholderText = "2022.3.62f3" };
    private readonly ComboBox architecture = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox cache = new() { Dock = DockStyle.Fill, Text = Path.Combine(AppData, "Downloads") };
    private readonly ListView modules = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Height = 8 };
    private readonly Label status = new() { Text = "正在查找本机 Unity…", AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly Button cancel = new() { Text = "取消任务", AutoSize = true, Enabled = false };
    private readonly List<Control> lockWhileBusy = [];
    private readonly HashSet<string> remembered = new(StringComparer.OrdinalIgnoreCase);
    private Release? catalog;
    private string catalogArchitecture = "";
    private CancellationTokenSource? operation;

    internal ToolWindow()
    {
        Text = "Unity 模块助手";
        ClientSize = new Size(1090, 705);
        MinimumSize = new Size(920, 610);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Canvas;
        architecture.Items.AddRange(["X86_64", "ARM64"]);
        architecture.SelectedIndex = 0;
        BuildLayout();
        lockWhileBusy.AddRange([editors, version, architecture, cache, modules]);
        try { remembered.UnionWith(JsonSerializer.Deserialize<string[]>(File.ReadAllText(History)) ?? []); } catch { }
        editors.SelectedIndexChanged += (_, _) =>
        {
            if (editors.SelectedItem is EditorInstall editor)
            {
                version.Text = editor.Version;
                architecture.SelectedItem = editor.Architecture;
            }
            RefreshModuleRows(preserveChecks: false);
        };
        version.TextChanged += (_, _) => ClearCatalog();
        architecture.SelectedIndexChanged += (_, _) => ClearCatalog();
        cancel.Click += (_, _) => operation?.Cancel();
        Shown += async (_, _) => await Scan();
        FormClosing += (_, e) =>
        {
            if (operation is null) return;
            e.Cancel = true;
            operation.Cancel();
        };
    }

    private Button Button(string label, Func<Task> action)
    {
        var button = new Button { Text = label, AutoSize = true, Height = 30, Padding = new Padding(5, 0, 5, 0) };
        StyleButton(button);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { Report($"失败：{ex.Message}"); }
        };
        lockWhileBusy.Add(button);
        return button;
    }

    private static Control Buttons(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Control Field(string label, Control input, params Control[] buttons)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 + buttons.Length };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 93));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true });
        row.Controls.Add(input);
        foreach (var button in buttons) { row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.Controls.Add(button); }
        return row;
    }

    private void Report(string message)
    {
        status.Text = message;
        AppendLog(message);
    }

    private void AppendLog(string message)
    {
        log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private async Task Run(Func<CancellationToken, Task> work)
    {
        if (operation is not null) return;
        using var cts = new CancellationTokenSource();
        operation = cts;
        foreach (var control in lockWhileBusy) control.Enabled = false;
        cancel.Enabled = true;
        bar.Style = ProgressBarStyle.Marquee;
        try { await work(cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { Report("任务已取消；已下载完成的文件会保留。"); }
        catch (OperationCanceledException) { Report("请求超时，请检查网络后重试。"); }
        catch (UnauthorizedAccessException ex) { Report($"没有写入权限：{ex.Message}。若 Unity 安装在 Program Files，可尝试以管理员身份运行。"); }
        catch (Exception ex) { Report($"失败：{ex.Message}"); }
        finally
        {
            operation = null;
            foreach (var control in lockWhileBusy) control.Enabled = true;
            cancel.Enabled = false;
            bar.Style = ProgressBarStyle.Blocks;
            bar.Value = 0;
        }
    }

    private Task Scan() => Run(async ct =>
    {
        var selected = (editors.SelectedItem as EditorInstall)?.Exe;
        var list = await Task.Run(() => EditorInstall.Discover(remembered), ct);
        editors.Items.Clear();
        editors.Items.AddRange(list);
        if (list.Length > 0)
            editors.SelectedIndex = Math.Max(0, Array.FindIndex(list, e => e.Exe == selected));
        Report(list.Length == 0 ? "没有自动找到 Unity；请点击「手动选目录」。" : $"找到 {list.Length} 个编辑器。选择版本后点击「加载官方模块」。");
    });

    private async Task ChooseEditor()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择 Unity 安装根目录或 Editor 目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var editor = EditorInstall.Read(dialog.SelectedPath);
        remembered.Add(editor.Exe);
        Directory.CreateDirectory(AppData);
        File.WriteAllText(History, JsonSerializer.Serialize(remembered));
        var existing = editors.Items.Cast<EditorInstall>().FirstOrDefault(e => e.Exe.Equals(editor.Exe, StringComparison.OrdinalIgnoreCase));
        if (existing is null) editors.Items.Add(editor);
        editors.SelectedItem = existing ?? editor;
        await LoadCatalog();
    }

    private void ChooseCache()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择保存模块安装包的目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) cache.Text = dialog.SelectedPath;
    }

    private string CacheRoot() => Path.GetFullPath(cache.Text.Trim());
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void OpenCache() { Directory.CreateDirectory(CacheRoot()); OpenPath(CacheRoot()); }
    private void OpenReleasePage()
    {
        var release = catalog ?? throw new IOException("请先加载官方模块。");
        OpenPath($"https://unity.com/releases/editor/whats-new/{Uri.EscapeDataString(release.Version)}#component-installers");
    }
    private void ClearCatalog() { catalog = null; RefreshModuleRows(false); }

    private Task LoadCatalog() => Run(async ct =>
    {
        ClearCatalog();
        var arch = (string)architecture.SelectedItem!;
        var release = await ModuleService.Fetch(version.Text.Trim(), arch, ct);
        var items = ModuleService.Modules(release, arch);
        if (items.Length == 0) throw new IOException("这个版本的官方清单没有模块。");
        catalog = release;
        catalogArchitecture = arch;
        RefreshModuleRows(preserveChecks: false);
        Report($"已加载 {release.Version} · {arch} 的 {items.Length} 个模块。");
    });

    private EditorInstall? MatchingEditor()
    {
        if (catalog is null || editors.SelectedItem is not EditorInstall editor) return null;
        try { ModuleService.MatchEditor(editor, catalog, catalogArchitecture); return editor; }
        catch (IOException) { return null; }
    }

    private void RefreshModuleRows(bool preserveChecks)
    {
        var editor = MatchingEditor();
        var selectedId = (modules.SelectedItems.Count == 0 ? null : modules.SelectedItems[0].Tag as ModuleRow)?.Module.Id;
        var checkedIds = preserveChecks
            ? CheckedRows().Select(row => row.Module.Id).ToHashSet()
            : [];
        modules.BeginUpdate();
        try
        {
            modules.Items.Clear();
            var all = catalog is null ? [] : ModuleService.Modules(catalog, catalogArchitecture);
            var visible = all.Where(m => CategoryFor(m) == category).ToArray();
            foreach (var module in visible)
            {
                var state = editor is null ? InstallStatus.Unknown : InstallCheck.Inspect(editor, module);
                var item = CreateModuleItem(new ModuleRow(module, state));
                modules.Items.Add(item);
                item.Checked = category != ModuleCategory.Platforms && checkedIds.Contains(module.Id);
                if (module.Id == selectedId) item.Selected = true;
            }
            UpdateCategoryView(all, visible.Length);
        }
        finally { modules.EndUpdate(); }
    }

    private void SelectAndroid()
    {
        if (catalog is null) throw new IOException("请先加载官方模块。");
        if (MatchingEditor() is null) throw new IOException("请先选择与清单匹配的本机 Unity 编辑器。");
        RefreshModuleRows(preserveChecks: false);
        var missing = 0;
        var present = 0;
        var review = 0;
        foreach (ListViewItem item in modules.Items)
            if (item.Tag is ModuleRow row && ModuleService.IsAndroidZip(row.Module))
            {
                if (row.State == InstallStatus.Missing) { item.Checked = true; missing++; }
                else if (row.State == InstallStatus.Present) present++;
                else review++;
            }
        Report($"Android 依赖：缺少 {missing} 项（已勾选），已安装 {present} 项，需检查 {review} 项。");
    }

    private Task ProcessSelected(bool install)
    {
        var release = catalog ?? throw new IOException("请先加载官方模块。");
        var selected = CheckedRows().Select(r => r.Module).ToArray();
        if (selected.Length == 0) throw new IOException("请先勾选要处理的模块。");
        var manual = selected.Where(m => m.IsManualSupport).ToArray();
        if (manual.Length > 0)
        {
            Report($"Build Support 需从官网手动安装，已跳过：{string.Join("、", manual.Select(m => m.Name))}。");
            selected = selected.Where(m => !m.IsManualSupport).ToArray();
            if (selected.Length == 0) return Task.CompletedTask;
        }
        EditorInstall? editor = null;
        if (install)
        {
            editor = editors.SelectedItem as EditorInstall ?? throw new IOException("请先选择本机 Unity 安装目录。");
            ModuleService.MatchEditor(editor, release, catalogArchitecture);
            selected = selected.Where(ModuleService.IsAndroidZip).ToArray();
            if (selected.Length == 0) throw new IOException("没有勾选 Android 工具 ZIP。");
            if (ModuleService.Modules(release, catalogArchitecture).FirstOrDefault(m => m.Id == "android") is not { } androidModule
                || InstallCheck.Inspect(editor, androidModule) != InstallStatus.Present)
                throw new IOException("请先点「官网安装器页面」，下载同版本 Windows Android Build Support，手动安装到该 Unity 根目录后刷新状态。");
            var statuses = selected.Select(m => (Module: m, State: InstallCheck.Inspect(editor, m))).ToArray();
            foreach (var item in statuses.Where(i => i.State != InstallStatus.Missing))
                Report(item.State == InstallStatus.Present ? $"已安装，跳过：{item.Module.Name}" :
                    $"目录或版本需要检查，已跳过：{item.Module.Name}");
            selected = statuses.Where(i => i.State == InstallStatus.Missing).Select(i => i.Module).ToArray();
            if (selected.Length == 0)
            {
                RefreshModuleRows(preserveChecks: true);
                Report("没有可自动补装的勾选项。未下载或修改文件。");
                return Task.CompletedTask;
            }
            if (!ConfirmAndroidLicense()) return Task.CompletedTask;
        }
        var root = CacheRoot();
        var keepPackages = keepArchives.Checked;
        return Run(async ct =>
        {
            var arch = catalogArchitecture;
            var progress = new Progress<(string Label, int Percent)>(p =>
            {
                if (operation is null) return;
                status.Text = p.Percent < 0 ? p.Label : $"{p.Label}  {p.Percent}%";
                bar.Style = p.Percent < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
                if (p.Percent >= 0) bar.Value = p.Percent;
            });
            var files = new List<(Module Module, ModuleService.DownloadedFile File)>();
            var failures = 0;
            foreach (var module in selected)
            {
                try
                {
                    var file = await ModuleService.DownloadFile(root, release, arch, module, progress, ct);
                    files.Add((module, file));
                    Report($"已就绪：{module.Name}");
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException ||
                    ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    failures++;
                    Report($"下载失败：{module.Name} — {ex.Message}");
                    if (install)
                    {
                        Report("所选 Android 依赖没有全部下载成功，本次未修改 Unity 安装目录。");
                        return;
                    }
                }
            }
            if (editor is not null)
            {
                ModuleService.MatchEditor(EditorInstall.Read(editor.Exe), release, arch);
                var stillMissing = files.Where(f => InstallCheck.Inspect(editor, f.Module) == InstallStatus.Missing).ToArray();
                if (stillMissing.Length != files.Count) Report("有组件状态在下载期间发生变化，已跳过这些组件。");
                if (stillMissing.Length > 0)
                {
                    await Task.Run(() => ModuleService.InstallAndroid(editor,
                        stillMissing.Select(f => (f.Module, f.File.Path)).ToArray(), progress, ct), ct);
                    if (!keepPackages && !ct.IsCancellationRequested)
                    {
                        int removed = 0;
                        foreach (var item in stillMissing.Where(f => f.File.NewFile))
                        {
                            try
                            {
                                File.Delete(item.File.Path);
                                removed++;
                                File.Delete(item.File.Path + ".source");
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            { Report($"安装已完成，无法清理 {Path.GetFileName(item.File.Path)}：{ex.Message}"); }
                        }
                        if (removed > 0) Report($"已清理本次新下载的 {removed} 个安装包。");
                    }
                    AppendLog($"安装目录：{editor.Android}");
                    AppendLog("若 Unity 仍使用自定义工具路径，可在 Edit → Preferences → External Tools 中切换到 Installed with Unity。");
                    Report($"安装完成 · 已安装 {stillMissing.Length} 项 Android 依赖");
                }
                RefreshModuleRows(preserveChecks: false);
            }
            else Report($"下载完成：成功 {files.Count} 项，失败 {failures} 项。目录：{Path.Combine(root, release.Version, arch)}");
        });
    }

    private bool ConfirmAndroidLicense()
    {
        using var dialog = new Form { Text = "Android SDK 使用条款", ClientSize = new Size(520, 112),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, Font = Font };
        var terms = new LinkLabel { Text = "查看 Google Android SDK 使用条款", Left = 16, Top = 16, AutoSize = true };
        terms.LinkClicked += (_, _) => OpenPath(ModuleService.AndroidLicense);
        var accept = new Button { Text = "接受并继续", DialogResult = DialogResult.OK, Width = 108, Left = 270, Top = 58 };
        var decline = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 108, Left = 386, Top = 58 };
        dialog.Controls.AddRange([terms, accept, decline]);
        dialog.CancelButton = decline;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private Task OpenInstaller()
    {
        if (catalog is null || modules.SelectedItems.Count == 0 ||
            modules.SelectedItems[0].Tag is not ModuleRow { Module.IsInstaller: true } row)
            throw new IOException("请点选列表中的 EXE 安装器。Build Support 请用「官网安装器页面」。");
        var release = catalog;
        var module = row.Module;
        if (module.IsManualSupport)
            throw new IOException("Build Support 由用户从官网手动下载和安装。请点「官网安装器页面」。");
        var editor = MatchingEditor() ?? throw new IOException("请先选择与清单匹配的本机 Unity 编辑器；只下载可用「下载勾选项」。");
        var state = InstallCheck.Inspect(editor, module);
        if (state != InstallStatus.Missing)
        {
            RefreshModuleRows(preserveChecks: true);
            Report(state == InstallStatus.Present ? $"已安装，未打开安装器：{module.Name}" :
                $"无法确认此模块尚未安装。工具不会打开安装器；仍可用「下载勾选项」保存文件。");
            return Task.CompletedTask;
        }
        if (MessageBox.Show(this, $"模块：{module.Name}\n版本：{release.Version}\n\n请在安装向导中核对目标路径：\n{editor.Root}",
            "打开官方安装器", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return Task.CompletedTask;
        return Run(async ct =>
        {
            var progress = new Progress<(string Label, int Percent)>(p => { if (operation is not null) status.Text = $"{p.Label} {Math.Max(0, p.Percent)}%"; });
            var file = await ModuleService.DownloadFile(CacheRoot(), release, catalogArchitecture, module, progress, ct);
            ct.ThrowIfCancellationRequested();
            ModuleService.MatchEditor(EditorInstall.Read(editor.Exe), release, catalogArchitecture);
            if (InstallCheck.Inspect(editor, module) != InstallStatus.Missing)
            {
                RefreshModuleRows(preserveChecks: true);
                Report($"安装状态已变化，未打开安装器：{module.Name}");
                return;
            }
            if (!Path.GetExtension(file.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("下载文件没有 .exe 扩展名，已保存在下载目录。请检查来源后手动打开。");
            OpenPath(file.Path);
            Report($"已打开安装器：{module.Name}。请在安装向导中完成安装。");
        });
    }

    private record ModuleRow(Module Module, InstallStatus State);
}
