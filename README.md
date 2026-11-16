# Unity 模块助手

为现有 Windows Unity 编辑器下载官方模块，补齐 Android SDK、NDK 和 OpenJDK。此工具为非官方项目，与 Unity 无隶属关系。

## 使用

1. 从 [Releases](https://github.com/ljdong-fy/UnityModuleTool/releases) 下载并双击 UnityModuleTool.exe。可把这个 EXE 单独复制到其他 Windows 10/11 x64 电脑，无需安装 .NET 或 Unity Hub。
2. 在「本机编辑器」选择版本。工具查找常见安装目录、Windows 卸载注册信息，以及已有的 Hub 编辑器记录。如果没有找到，点击「手动选目录」，选择 Unity 安装根目录或 `Editor` 目录。
3. 点击「加载官方模块」。左侧可切换「Android 工具」「平台支持」「其他组件」，每页只显示本类组件和相关操作。「任务日志」可查看下载详情。也可以输入完整版本号和架构，只下载模块供另一台电脑使用。对于公开查询没有返回的版本，工具会尝试查询 XLTS 清单；这不会改变 Unity 的许可证要求。
4. **Android Build Support、WebGL、iOS 等平台支持**：工具显示安装状态。需要补装时，点「官网安装器页面」，在对应版本的 **Component installers → Windows** 下载并手动运行，在向导中核对目标目录。部分网络下 Unity 会把链接重定向到中国下载节点，该节点可能返回 404；工具不下载或启动这些安装器。
5. **Android JDK、SDK、NDK**：先安装同版本的 Android Build Support，关闭该版本的编辑器，再在「Android 工具」页点「勾选缺项」→「安装勾选的依赖」。装完后重新打开 Unity 即可；使用 Unity 默认路径时，无需改设置。构建时如提示 SDK 许可，请按 Unity 提示处理。

## 范围

- 版本和安装包地址来自 [Unity Releases API](https://docs.unity.com/en-us/oas-release/1.0.0)。需要联网获取清单；下载时还需要能访问清单中对应的服务器。
- ZIP 自动安装只针对官方清单中明确指向 AndroidPlayer 的工具包。其他 ZIP 和语言文件可下载；平台支持安装器由用户从官网下载和运行。
- 「已安装」表示检测到了组件的关键文件和可核对的版本信息，非完整性扫描。安装操作会跳过已装和需检查的组件，只补没有组件目录的项。若有并发安装或冲突，工具仍不会覆盖已有的不同文件。
- 「下载勾选项」是单独的操作，可用来准备离线包，不会修改编辑器。
- 安装成功后默认删除本次新下载并已用于安装的 ZIP。勾选「安装后保留安装包」可保留，工具会记住选项。已有缓存、单独下载的包，以及失败或取消的任务所下载的包不会被自动清理。
- 下载失败时，日志会列出具体模块、地址和 HTTP 状态。批量下载会继续处理其他勾选项；安装 Android 依赖时，如果有包未下载成功，这一批不会写入 Unity。遇到 Unity 安装器 404，可用 [Unity 下载档案](https://unity.com/releases/editor/archive) 核对对应版本的 Windows 安装器。
- 目标目录需要可写。如果 Unity 位于受保护的目录，可能需要以管理员身份运行。
- 如果 Unity 以前设过自定义的 JDK、SDK 或 NDK 路径，并且你想改用刚装好的依赖，请到 `Edit → Preferences → External Tools`，为对应项勾选 `Installed with Unity (recommended)`。已能正常构建的自定义路径可以继续使用。
- 适用于该发布接口列出的 Windows 编辑器。团结引擎、接口没有收录的版本，以及 macOS/Linux 编辑器不在此版本的支持范围。

## 从源码打包

使用 .NET 8 SDK，在项目目录运行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist
```
