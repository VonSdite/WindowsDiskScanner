# Windows 磁盘空间扫描器

这是一个面向 Windows 本地目录的空间分析工具。程序启动后等待用户选择扫描目录，扫描完成后可逐级展开目录，查看子目录和文件的大小、占总目录比例及修改时间，并可使用 OpenAI Chat 兼容模型生成清理分析报告。

## 功能

- 固定工作线程并发扫描，避免为海量文件创建独立任务。
- 单次枚举同时读取名称、类型、长度和修改时间，减少文件系统调用。
- 扫描阶段构建完整目录树，展开节点时不再访问磁盘。
- 目录优先、按占用空间从大到小排列。
- 实时显示已发现的目录、文件、空间和扫描耗时。
- 支持取消扫描，并忽略无权访问的目录。
- 显示文件链接和目录链接，但不递归进入目录链接，避免循环遍历和重复统计。
- 支持 Ctrl 多选和 Shift 连续多选，可批量移到 Windows 回收站。
- 右键可在资源管理器中显示单个项目，或让 AI 解释一个或多个文件、目录的来源和删除风险。
- 支持管理多个 OpenAI Chat 兼容 Provider、模型列表、代理模式和 SSL 校验。
- 支持拉取模型、标记已存在模型、全选、模型连通性测试以及 Provider/模型拖拽排序。
- 支持根据完整扫描结果生成大目录、大文件、软件用途和清理建议报告。
- AI 报告和文件解释使用独立窗口流式输出，并将 Markdown 渲染为对话气泡。报告作为第一条 AI 消息，后续追问和回答按用户、AI 消息排列；上游返回 reasoning/thinking 扩展字段或在回答中使用 `<think>...</think>` 时，思考内容在对应 AI 气泡内默认展开并流式显示，也可折叠。流式更新原地刷新现有气泡和 Markdown 控件。思考区默认跟随最新内容，用户离开底部查看历史内容时保持当前滚动位置。鼠标位于回答正文时滚动整个对话，位于思考正文时优先滚动思考内容。
- 支持查看、搜索、筛选和删除 AI 报告及询问历史记录。

工具统计文件的逻辑长度。NTFS 压缩、稀疏文件、簇大小和硬链接可能使统计值与磁盘属性窗口中的物理占用略有不同。

## 扫描实现

扫描器使用固定数量的后台工作线程和一个共享目录队列。工作线程每次取出一个目录，通过 `FileSystemEnumerable` 单层枚举目录项，在同一次枚举中读取名称、路径、类型、文件长度和修改时间。发现子目录后将其加入队列，发现文件后直接记录逻辑长度。目录节点只由负责枚举它的工作线程写入，跨线程计数使用原子操作更新。

所有目录完成后，扫描器使用非递归的后序遍历从文件向上汇总目录大小，再按“目录优先、大小降序”排列节点并计算占根目录比例。界面保存完整扫描树，展开和折叠只更新内存中的可见行，不会再次读取磁盘。

为降低大量文件场景的内存占用，只有目录节点保存完整路径，文件节点共享父目录路径并在界面需要时组合完整路径。空目录不创建子节点列表，占根目录比例也只在生成可见行时计算。

默认工作线程数根据处理器数量确定，范围为 2 到 8。进度通知限制为约每 120 毫秒一次，避免频繁刷新阻塞界面。扫描会记录重解析点，但不会把目录重解析点加入扫描队列。扫描过程支持取消。

当前实现通过标准文件系统接口扫描，不直接读取 NTFS MFT，因此可以扫描 NTFS 之外的本地文件系统，也不要求管理员权限。全盘扫描速度取决于文件数量、磁盘性能和实时防护软件，不等同于直接读取 MFT 的专用工具。

## LLM Provider

主窗口右上角的“Provider 管理”打开 Provider 配置窗口。每个 Provider 包含名称、OpenAI Chat API 地址、API Key、出站代理模式、自定义代理地址、SSL 校验和有序模型列表。

出站代理支持以下模式：

- 直连：请求不使用代理。
- 系统代理：请求使用 Windows 系统代理。
- 自定义代理：请求使用 Provider 中填写的代理地址。

模型拉取会根据 Chat API 地址尝试 `/v1/models` 和 `/models`。拉取结果窗口标记当前清单中已存在的模型，并支持搜索和全选当前结果。Provider 和模型都支持拖拽调整顺序，主窗口默认选择顺序最靠前的模型。

Provider 配置保存在可执行程序所在目录的 `providers.json`。配置文件包含 API Key。

成功完成的 AI 报告和询问记录保存在可执行程序所在目录的 `ai-history` 文件夹。每条记录包含类型、标题、模型、生成时间和 Markdown 原文，不包含 API Key。历史窗口支持单条删除和批量删除。

扫描完成后可选择模型生成 AI 报告。文件列表右键菜单中的“询问 AI”使用当前选中的模型解释所选路径。AI 输出用于辅助判断，删除系统目录、程序组件和用户数据前需要核对实际用途。

## 运行

需要 Windows 10 或更高版本以及 .NET 8 SDK。

```powershell
dotnet run --project src/WindowsDiskScanner.App
```

程序启动后点击扫描目录输入框选择目录，再点击“开始扫描”。普通权限下无法读取的系统目录会被跳过，需要完整结果时可使用管理员权限运行。

## 构建

```powershell
dotnet build WindowsDiskScanner.sln -c Release
```

## 发布

项目以 Windows x64 自包含单文件形式发布。发布件包含 .NET 运行时，用户不需要单独安装 .NET。

在项目根目录执行：

```powershell
$version = "0.1.1"
$name = "WindowsDiskScanner-$version-win-x64"
$out = "artifacts\$name"

dotnet publish .\src\WindowsDiskScanner.App\WindowsDiskScanner.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $out

Compress-Archive `
  -Path "$out\WindowsDiskScanner.exe" `
  -DestinationPath "artifacts\$name.zip" `
  -Force

Get-FileHash "artifacts\$name.zip" -Algorithm SHA256
```

发布文件位于 `artifacts\WindowsDiskScanner-0.1.0-win-x64.zip`。发布新版本时更新 `$version`。

用户需要先解压 ZIP，再运行 `WindowsDiskScanner.exe`。程序会在可执行文件所在目录保存 `providers.json` 和 `ai-history`，因此程序应放在用户具有写入权限的目录中。

`providers.json` 包含 API Key。发布包只包含打包命令生成的 `WindowsDiskScanner.exe`，不包含开发环境中已有的 `providers.json` 和 `ai-history`。
