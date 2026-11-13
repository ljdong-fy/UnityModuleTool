namespace UnityModuleTool;

sealed partial class ToolWindow
{
    private enum ModuleCategory { Android, Platforms, Extras }
    private static readonly Color Canvas = Color.FromArgb(239, 242, 247);
    private static readonly Color Ink = Color.FromArgb(26, 34, 49);
    private static readonly Color Muted = Color.FromArgb(80, 94, 115);
    private static readonly Color Accent = Color.FromArgb(43, 90, 213);
    private static readonly Color Navy = Color.FromArgb(25, 32, 46);
    private static readonly Color Line = Color.FromArgb(216, 223, 234);
    private ModuleCategory category;
    private readonly Dictionary<ModuleCategory, Button> nav = [];
    private readonly Label sectionTitle = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label sectionHint = new() { Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true };
    private readonly Label empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted };
    private readonly CheckBox keepArchives = new() { Text = "安装后保留安装包", AutoSize = true, Margin = new Padding(9, 9, 3, 0) };
    private FlowLayoutPanel actions = null!;
    private Control[] androidActions = [], platformActions = [], extraActions = [];

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Line;
        button.BackColor = Color.White;
        button.ForeColor = Ink;
        button.UseVisualStyleBackColor = false;
    }

    private static Button Primary(Button button)
    {
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private static ModuleCategory CategoryFor(Module module) => ModuleService.IsAndroidZip(module) ? ModuleCategory.Android
        : module.IsManualSupport ? ModuleCategory.Platforms : ModuleCategory.Extras;

    private static string CategoryName(ModuleCategory value) => value switch
    {
        ModuleCategory.Android => "Android 工具",
        ModuleCategory.Platforms => "平台支持",
        _ => "其他组件"
    };

    private void BuildLayout()
    {
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(frame);

        var rail = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Navy, Padding = new Padding(15, 23, 15, 10), Margin = Padding.Empty };
        frame.Controls.Add(rail, 0, 0);
        rail.Controls.Add(new Label { Text = "Unity 模块助手", ForeColor = Color.White, Width = 158, Height = 37,
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold) });
        rail.Controls.Add(new Label { Text = "模块分类", ForeColor = Color.FromArgb(167, 179, 199), Width = 158, Height = 32,
            Margin = new Padding(4, 17, 0, 0) });
        foreach (var value in Enum.GetValues<ModuleCategory>())
        {
            var button = Button(CategoryName(value), () =>
            {
                if (category != value)
                {
                    category = value;
                    RefreshModuleRows(false);
                }
                return Task.CompletedTask;
            });
            button.AutoSize = false;
            button.Size = new Size(158, 42);
            button.Margin = new Padding(0, 2, 0, 4);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(10, 0, 5, 0);
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 72, 101);
            nav[value] = button;
            rail.Controls.Add(button);
        }

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(19, 13, 19, 9),
            ColumnCount = 1, RowCount = 8, Margin = Padding.Empty };
        foreach (int height in new[] { 38, 38, 35 })
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 42, 34, 9, 26 })
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        frame.Controls.Add(body, 1, 0);
        body.Controls.Add(Field("本机编辑器", editors,
            Button("选择目录…", ChooseEditor), Button("重新查找", Scan)));
        body.Controls.Add(Buttons(new Label { Text = "官方版本", AutoSize = true, Margin = new Padding(5, 9, 22, 0) },
            version, architecture, Primary(Button("加载官方模块", LoadCatalog)),
            Button("刷新安装状态", () => { RefreshModuleRows(true); return Task.CompletedTask; })));
        body.Controls.Add(Field("下载目录", cache,
            Button("更改…", () => { ChooseCache(); return Task.CompletedTask; }),
            Button("打开目录", () => { OpenCache(); return Task.CompletedTask; })));

        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(14, 7, 14, 8), RowCount = 3, ColumnCount = 1, Margin = new Padding(3, 9, 3, 4) };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sectionTitle.Font = new Font(Font.FontFamily, 15, FontStyle.Bold);
        sectionTitle.ForeColor = Ink;
        card.Controls.Add(sectionTitle);
        card.Controls.Add(sectionHint);
        var listHost = new Panel { Dock = DockStyle.Fill };
        card.Controls.Add(listHost);
        listHost.Controls.Add(modules);
        listHost.Controls.Add(empty);
        modules.ForeColor = Ink;
        modules.Columns.Add("组件", 410);
        modules.Columns.Add("状态", 88);
        modules.Columns.Add("获取方式", 115);
        var rowHeight = new ImageList { ImageSize = new Size(1, 25) };
        modules.SmallImageList = rowHeight;
        Disposed += (_, _) => rowHeight.Dispose();
        modules.Resize += (_, _) =>
        {
            if (modules.Columns.Count > 0)
                modules.Columns[0].Width = Math.Max(250, modules.ClientSize.Width - modules.Columns[1].Width - modules.Columns[2].Width - SystemInformation.VerticalScrollBarWidth - 3);
        };
        body.Controls.Add(card);

        var download = Button("下载勾选项", () => ProcessSelected(false));
        var website = Primary(Button("官网安装器页面", () => { OpenReleasePage(); return Task.CompletedTask; }));
        androidActions = [Primary(Button("安装勾选的依赖", () => ProcessSelected(true))),
            Button("勾选缺项", () => { SelectAndroid(); return Task.CompletedTask; }), download, keepArchives];
        var preference = Path.Combine(AppData, "keep-install-packages.txt");
        try { keepArchives.Checked = File.Exists(preference) && File.ReadAllText(preference) == "true"; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        keepArchives.CheckedChanged += (_, _) =>
        {
            try { Directory.CreateDirectory(AppData); File.WriteAllText(preference, keepArchives.Checked ? "true" : "false"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report($"无法保存保留安装包的偏好：{ex.Message}"); }
        };
        lockWhileBusy.Add(keepArchives);
        platformActions = [website];
        extraActions = [download, Button("打开其他安装器", OpenInstaller)];
        StyleButton(cancel);
        actions = (FlowLayoutPanel)Buttons();
        body.Controls.Add(actions);

        var logButton = Button("任务日志", ShowLog);
        var footer = Field("任务状态", status, logButton);
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.ForeColor = Muted;
        body.Controls.Add(footer);
        body.Controls.Add(bar);
        body.Controls.Add(new Label { Text = "安装成功后默认清理本次新下载的安装包 · 单独下载的安装包会保留", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        UpdateCategoryView([], 0);
    }

    private Task ShowLog()
    {
        using var window = new Form { Text = "任务日志", StartPosition = FormStartPosition.CenterParent, Size = new Size(760, 420), Font = Font };
        var detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = log.Text.Length == 0 ? "暂无任务记录。" : log.Text };
        window.Controls.Add(detail);
        detail.SelectionStart = detail.TextLength;
        detail.ScrollToCaret();
        window.ShowDialog(this);
        return Task.CompletedTask;
    }

    private IEnumerable<ModuleRow> CheckedRows() => modules.CheckedItems.Cast<ListViewItem>().Select(item => (ModuleRow)item.Tag!);

    private static ListViewItem CreateModuleItem(ModuleRow row)
    {
        var item = new ListViewItem(row.Module.Name) { Tag = row, UseItemStyleForSubItems = false };
        var (text, color) = row.State switch
        {
            InstallStatus.Present => ("已安装", Color.FromArgb(22, 112, 74)),
            InstallStatus.Missing => ("缺少", Color.FromArgb(42, 83, 192)),
            InstallStatus.Review => ("需检查", Color.FromArgb(151, 81, 16)),
            _ => ("未确认", Muted)
        };
        item.SubItems.Add(text).ForeColor = color;
        item.SubItems.Add(row.Module.IsManualSupport ? "官网手动安装" : ModuleService.IsAndroidZip(row.Module) ? "工具内安装" : "独立下载").ForeColor = Muted;
        return item;
    }

    private void UpdateCategoryView(Module[] all, int count)
    {
        foreach (var (value, button) in nav)
        {
            button.Text = CategoryName(value) + (catalog is null ? "" : $"   {all.Count(m => CategoryFor(m) == value)}");
            button.BackColor = value == category ? Color.FromArgb(53, 75, 117) : Navy;
            button.ForeColor = Color.White;
        }
        sectionTitle.Text = CategoryName(category);
        sectionHint.Text = category switch
        {
            ModuleCategory.Android => "安装前需有 Android Build Support。JDK、SDK、NDK 只补缺项。",
            ModuleCategory.Platforms => "从官网手动安装。官方链接可能重定向到中国节点并返回 404。",
            _ => "Visual Studio、语言包和文档等可选组件。"
        };
        empty.Text = catalog is null ? "选择 Unity 版本，再点击「加载官方模块」" : "此版本没有这一类组件";
        empty.Visible = count == 0;
        modules.Visible = count > 0;
        modules.CheckBoxes = category != ModuleCategory.Platforms;
        actions.SuspendLayout();
        try
        {
            actions.Controls.Clear();
            actions.Controls.AddRange(category switch
            {
                ModuleCategory.Android => androidActions,
                ModuleCategory.Platforms => platformActions,
                _ => extraActions
            });
            actions.Controls.Add(cancel);
        }
        finally { actions.ResumeLayout(); }
    }
}
