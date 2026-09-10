# Image Tagger 开发任务清单

> 对应设计基线：`DESIGN.md`  
> 平台范围：Windows、macOS  
> 使用方式：严格按“公共前置任务 → 并行开发任务 → 统一收尾任务”执行  
> 完成标准：所有主任务及其验收子项均已勾选

## 1. 执行规则

- 主任务使用稳定 ID。任务描述中的“依赖”未完成前，不应开始该任务。
- 只有任务下所有验收项均满足，才可勾选主任务。
- 公共前置任务全部完成后，才能正式进入并行开发阶段。
- 并行阶段按工作流划分文件所有权；跨工作流只通过公共契约协作，不直接复制实现。
- 每次合并必须保持解决方案可构建、现有测试通过，不提交无法运行的占位实现。
- 源码、项目文件、manifest、fixture、脚本和文档不得包含开发机绝对路径。
- Linux 不在首版范围内，不创建 Linux 条件代码、依赖、安装包或测试任务。
- 通用图像、格式、数值、缓存和推理能力优先使用设计文档批准的标准库或第三方库；新增自定义算法必须先有 ADR。
- 任务执行中若需要改变设计范围，先更新 `DESIGN.md`，再同步修改本清单及覆盖矩阵。

## 2. 公共前置任务

这些任务建立所有并行工作流共同依赖的工程、模型、契约和测试基础，应按编号顺序推进。

### P-01 冻结首版范围与仓库规则

- [x] `P-01` 确认设计基线、非目标和仓库约束。

  依赖：无。

  工作内容：

  - 将 `DESIGN.md` 和本清单作为首版范围基线。
  - 明确首版只支持 Windows/macOS、只内置 quality 模型、只有单套 Prompt 规则、不含搜索框和中间大图预览。
  - 定义提交约定、分支/合并约定、文件编码 UTF-8、换行规则和命名规范。

  验收：

  - [x] `DESIGN.md` 与 `TASKS.md` 均通过 Markdown 基本检查且代码围栏配对。
  - [x] 仓库贡献说明明确禁止本机绝对路径、浮动依赖和未批准的自定义算法。
  - [x] 范围说明明确排除 Linux、数据库、插件程序集、云服务和预测结果持久化。

### P-02 创建解决方案与工程骨架

- [x] `P-02` 创建可构建的 .NET/Avalonia 解决方案骨架。

  依赖：`P-01`。

  工作内容：

  - 创建 `ImageTagger.sln`。
  - 创建 `ImageTagger.Core`、`ImageTagger.Infrastructure`、`ImageTagger.App`、`ImageTagger.Tests` 和开发期 `ImageTagger.ModelPackTool`。
  - 设置依赖方向：App → Core/Infrastructure，Infrastructure → Core，ModelPackTool → Core/Infrastructure，Tests → 被测项目。
  - 启用 nullable、隐式 using、统一语言版本、Release 优化和 Avalonia compiled bindings。
  - 添加 `.editorconfig`、`.gitignore`、`Directory.Build.props`、`Directory.Packages.props` 和 NuGet lock files。

  验收：

  - [x] Debug 和 Release 配置均能完成 restore/build。
  - [x] Core 不引用 Avalonia、ShadUI、ONNX Runtime或平台文件选择 API。
  - [x] ModelPackTool 不进入 App 发布输出。
  - [x] `.gitignore` 覆盖构建输出、IDE 文件、日志、缓存和本机设置。

### P-03 完成依赖、许可证与平台可行性验证

- [x] `P-03` 验证并锁定所有基础依赖。

  依赖：`P-02`。

  工作内容：

  - 验证 Avalonia 12.1、ShadUI 0.2.4、.NET 10 和 CommunityToolkit.Mvvm 的兼容性。
  - 验证 ImageSharp、MetadataExtractor、System.Numerics.Tensors、CommunityToolkit.HighPerformance、CsvHelper、JsonSchema.Net、Microsoft.Extensions、Serilog 和测试依赖。
  - 在 Windows 验证 `Microsoft.WindowsAppSDK.ML` 能由 Avalonia 桌面进程调用。
  - 在 macOS 验证 C# 可注册 CoreML EP；若标准包不含 CoreML，记录采用官方 ORT 构建产物的方案。
  - 检查每个依赖的许可证、维护状态、原生架构和再分发要求。

  验收：

  - [x] 新增 `docs/adr/0001-dependencies-and-runtimes.md`，记录版本、选择原因和已知限制。
  - [x] ImageSharp 许可证适用于项目预期发布方式，或已选择许可证兼容的替代库。
  - [x] Windows ML 与 macOS CoreML 均有最小 Session 创建验证结果。
  - [x] 所有包使用集中固定版本，无浮动版本。

### P-04 生成标准内置 Model Pack

- [x] `P-04` 将源模型资产整理为首版标准 Model Pack。

  依赖：`P-02`、`P-03`。

  工作内容：

  - 在 ModelPackTool 中实现 `pack` 和 `validate` 命令；输入路径只通过命令参数传入，不写入项目。
  - `pack` 只生成标准目录，不创建额外归档格式。
  - 只纳入 quality INT8，并在包内命名为 `model.onnx`。
  - 将源 `selected_tags.csv` 的 category 数字映射为运行时分组（9→rating、4→character、0→general、1→artist、3→copyright、其它→general），并与中文翻译合并为 `tags.csv`；不引入外部 taxonomy 数据集。
  - 生成 `model.json`、`checksums.sha256` 和 `LICENSE.txt`。
  - 将标准包放入仓库相对位置 `Assets/Models/wd-eva02-tagger-2026-canary/`，大文件使用 Git LFS。

  验收：

  - [x] 包目录只有 `model.json`、`model.onnx`、`tags.csv`、`checksums.sha256`、`LICENSE.txt`。
  - [x] `tags.csv` 有 16,473 条连续 ID，原始标签与中文翻译不发生错位。
  - [x] rating/artist/copyright/character/general 分组来自源 category 映射；未知项有构建报告并回落 general。
  - [x] `validate` 能发现缺失文件、错误 hash、重复/断裂 ID、未知分组和路径越界。
  - [x] ModelPackTool 生成的标准目录可被同一工具重新验证。
  - [x] 仓库文本扫描未发现源模型所在机器的绝对路径。

### P-05 建立预处理 golden 与库选型结论

- [x] `P-05` 用参考 Python/Pillow 产出预处理和模型结果基线。

  依赖：`P-03`、`P-04`。

  工作内容：

  - 制作横图、竖图、奇数尺寸、RGB、RGBA、调色板透明、灰度和 8 种 EXIF Orientation fixture。
  - 从参考脚本导出转向后、补白后、缩放后和最终 Tensor golden。
  - 为固定图片导出概率、Top-N 和阈值集合基线。
  - 比较 ImageSharp Bicubic 与 Pillow；不满足门槛时按设计评估 SkiaSharp/NetVips。
  - 记录最终采用的 resampler、允许误差及原因。

  验收：

  - [x] 新增 `docs/adr/0002-preprocessing-compatibility.md`。
  - [x] 所有 fixture、golden 和生成说明都使用仓库相对路径。
  - [x] 无损图片最终 Tensor 达到设计约定的一致性；JPEG 差异有量化报告。
  - [x] 固定图片的端到端标签结果与 Python 参考一致。
  - [x] 若需要自定义兼容层，ADR 已证明现有库无法满足，并把自定义范围限制到最小。

### P-06 冻结公共领域契约与 Model Pack Schema

- [x] `P-06` 定义并测试并行工作流共用的类型和接口。

  依赖：`P-04`、`P-05`。

  工作内容：

  - 定义 `ImageDocument`、`ModelDescriptor`、`PredictionSnapshot`、`TagCatalogEntry`、`TagSelection`、`PromptSettings` 和 `GenerationInfo`。
  - 定义 Model Pack JSON Schema、预处理 step 描述、输入/输出契约和版本策略。
  - 定义设计文档保留的服务接口；内部类不机械增加接口。
  - 定义统一错误类型、取消语义和运行状态枚举。
  - 提供测试用 fake runtime、fake model pack、fake platform service 和设计时数据。

  验收：

  - [x] Core 编译且没有 UI/平台/ONNX 依赖。
  - [x] Schema 能拒绝未知字段、未知版本、非法路径、非法 step 参数和无效输入输出契约。
  - [x] `PredictionSnapshot` 只保存连续概率数组和少量元数据，不复制标签目录对象。
  - [x] 公共接口有 XML 文档或清晰命名，足以让并行工作流独立实现。

### P-07 建立测试基础设施与质量门禁

- [x] `P-07` 配置单元、集成、Headless 和平台测试基础设施。

  依赖：`P-02`、`P-06`。

  工作内容：

  - 配置 xUnit、Avalonia.Headless、fixture 复制和临时目录工具。
  - 定义 `Unit`、`Integration`、`RealModel`、`WindowsRuntime`、`MacRuntime`、`Visual` 测试分类。
  - 加入绝对路径静态扫描、LFS pointer 检查和 Model Pack 校验任务。
  - 为异步命令、取消和 UI Dispatcher 提供确定性测试工具。

  验收：

  - [x] 普通测试不需要 818 MiB 模型即可运行。
  - [x] RealModel 测试可在发行流水线显式执行。
  - [x] 测试失败能明确区分代码错误、缺少真机能力和缺少 LFS 资产。
  - [x] 路径扫描会拒绝盘符路径、用户 home 路径和本机用户名。

### P-08 建立 ShadUI 主窗口与设计令牌

- [x] `P-08` 创建可供各 UI 工作流填充的静态主窗口。

  依赖：`P-02`、`P-03`、`P-06`。

  工作内容：

  - 注册 `ShadTheme`，主窗体使用 ShadUI Window。
  - 建立标题栏、工具栏、三栏 GridSplitter、中间文件栏/Tab、右侧 Prompt 区和状态栏。
  - 定义间距、字体、圆角、边框、语义色和图标使用规则。
  - 接入设计时 ViewModel，展示空状态、成功状态、忙碌状态和错误状态。

  验收：

  - [x] 1440×900 与 1120×720 下布局符合设计且无水平溢出。
  - [x] 中间区域没有图片大图预览，整个界面没有搜索框。
  - [x] 浅色/深色主题均能切换，ShadUI 控件风格统一。
  - [x] XAML 使用 compiled bindings 和 `x:DataType`。

### P-09 建立持续集成基线

- [x] `P-09` 建立提交级和发行级 CI 工作流。

  依赖：`P-03`、`P-07`、`P-08`。

  工作内容：

  - 提交级执行 restore、build、单元测试、Headless 测试、格式/路径扫描和依赖锁验证。
  - 发行级拉取 Git LFS，执行 Model Pack 校验、RealModel 测试和平台测试。
  - 配置依赖漏洞、许可证和 SBOM 生成步骤。

  验收：

  - [x] 干净检出后提交级 CI 可重复通过。
  - [x] LFS 未拉取时发行级 CI 明确失败而不是把 pointer 当 ONNX。
  - [x] CI 产物和日志不包含图片内容、Prompt 或本机绝对路径。

## 3. 并行开发任务

`P-01` 至 `P-09` 全部完成后，下列工作流可以并行。每个工作流内部按编号顺序执行；需要其它工作流成果时，先使用 `P-06` 的 fake，真实接线放到统一收尾阶段。

建议文件所有权：

| 工作流 | 主要目录/组件 |
|---|---|
| A | 图片会话、ImageList、Toolbar、StatusBar |
| B | ModelPacks、TagCatalog、Preprocessing、ModelPackTool |
| C | Inference、WindowsML、CoreML、Batching |
| D | Tags View/ViewModel |
| E | Metadata View/ViewModel/Parsers |
| F | Prompt View/ViewModel/Builder |
| G | Settings、Platform、Logging、Dialogs |

`MainWindow` 的最终组合和跨工作流事件接线只在 `U-01` 完成，避免并行阶段反复修改同一文件。

### 工作流 A：图片会话与左侧列表

- [x] `A-01` 实现图片导入与文件信息读取。

  依赖：公共前置任务。

  工作内容：使用 Avalonia StorageProvider 和 ImageSharp 识别文件，支持多选图片、文件夹和拖放；规范化路径并按平台规则去重。

  验收：

  - [x] 支持设计列出的图片格式，损坏/不支持文件按单项失败处理。
  - [x] 保留用户添加顺序，重复路径只出现一次。
  - [x] 文件名、大小、格式、像素尺寸读取正确。
  - [x] 删除/清空只影响会话，不删除磁盘文件。

- [x] `A-02` 实现缩略图服务与有界缓存。

  依赖：`A-01`。

  工作内容：使用 ImageSharp 生成最长边 128 px 的缩略图，用 MemoryCache 设置大小上限、过期和释放回调。

  验收：

  - [x] 透明图片使用棋盘格背景且方向正确。
  - [x] 列表不长期持有原始大图 Bitmap。
  - [x] 缓存淘汰和取消路径释放资源。
  - [x] 1,000 个文件不会在导入时一次解码全部缩略图。

- [x] `A-03` 实现图片会话状态与列表 ViewModel。

  依赖：`A-01`、`A-02`。

  工作内容：实现当前选择、相邻切换、移除、清空、状态更新和虚拟化列表绑定。

  验收：

  - [x] 每项显示缩略图、文件名、尺寸、大小、格式、识别状态和标签数。
  - [x] 切换当前项时通过图片 ID 原子更新，不显示上一张数据。
  - [x] `↑/↓`、`Delete` 和右键菜单行为正确。
  - [x] 列表滚动不触发明显 UI 卡顿。

- [x] `A-04` 接入工具栏、当前文件栏和状态栏。

  依赖：`A-03`。

  工作内容：连接打开、打开文件夹、识别命令占位契约、取消、阈值、模型选择、右栏开关和全局状态。

  验收：

  - [x] 命令可用状态符合当前选择和忙碌状态。
  - [x] 当前文件栏只显示文本信息，不显示图片预览。
  - [x] 批量状态能显示完成数/总数。
  - [x] 快捷键不抢占文本控件常规编辑行为。

- [x] `A-05` 完成图片会话测试与异常处理。

  依赖：`A-01` 至 `A-04`。

  工作内容：补齐图片导入、缓存、选择和并发状态的单元/Headless 测试，并修复发现的资源释放与竞态问题。

  验收：

  - [x] 覆盖重复文件、损坏图片、超大图片、文件被删除、拖入目录和取消导入。
  - [x] 覆盖切图竞态、清空运行中任务和缩略图晚到不串图。
  - [x] Headless 测试验证列表选择和命令状态。

### 工作流 B：Model Pack、标签目录与动态预处理

- [x] `B-01` 实现 Model Pack 加载与语义校验。

  依赖：公共前置任务。

  工作内容：使用 System.Text.Json、JsonSchema.Net、CsvHelper 和 SHA-256 加载标准包，并核对 manifest、catalog、checksum、许可证与 ONNX 契约。

  验收：

  - [x] 内置包可加载为 `ModelDescriptor` 和共享 `TagCatalog`。
  - [x] 路径越界、符号链接、未知 schema、错误 hash、标签数不符均被拒绝。
  - [x] 加载过程不扫描训练目录或当前工作目录。

- [x] `B-02` 实现外部 Model Pack 目录选择。

  依赖：`B-01`。

  工作内容：用户显式选择外部目录，完整校验后保存该目录配置；不复制、移动或删除外部模型文件。

  验收：

  - [x] 绝对路径异常、目录内未知条目、符号链接和非法资源引用被拒绝。
  - [x] 校验失败不写入无效配置。
  - [x] 清除配置不删除用户的外部模型目录。

- [x] `B-03` 实现预处理算子注册与参数校验。

  依赖：`B-01`。

  工作内容：为首版标准算子建立版本、输入/输出类型、参数 schema 和第三方库薄封装。

  验收：

  - [x] 支持设计列出的 12 个首版算子且不包含未使用的量化算子。
  - [x] 未知参数、非法枚举、数组长度错误和版本不支持有明确错误。
  - [x] crop/resize/alpha/颜色处理调用批准的第三方库，不重复实现核心算法。

- [x] `B-04` 实现类型化流水线编译器。

  依赖：`B-03`。

  工作内容：按 steps 顺序验证类型链、静态 shape、dtype、layout、资源上限并生成不可变顺序执行计划。

  验收：

  - [x] 非法算子顺序、重复 decode、无最终 Tensor 和 ONNX 输入不匹配均被拒绝。
  - [x] 同一规范化流水线只编译一次并按指纹复用。
  - [x] 首版没有通用融合优化器、脚本执行或反射类型加载。

- [x] `B-05` 实现 WD 精确预处理执行计划。

  依赖：`B-04`、`P-05`。

  工作内容：实现设计中的 EXIF、透明白底、floor-center 补白、Pillow-compatible bicubic、BGR、float32 归一化和 HWC→CHW。

  验收：

  - [x] 所有逐阶段 golden 测试通过。
  - [x] 最终 Tensor shape/dtype/layout 与模型一致。
  - [x] 最终结果直接写入目标 batch slice，不再复制完整单图 Tensor。
  - [x] 所有池化 buffer 在成功、失败和取消路径归还。

- [x] `B-06` 实现标签目录与可见标签投影。

  依赖：`B-01`。

  工作内容：一次性加载共享 TagCatalog；按概率数组、阈值和分组投影当前图片可见标签。

  验收：

  - [x] 标签 ID、原文、翻译和分组正确。
  - [x] 每图不复制 TagCatalog，不创建全部 16,473 个标签对象。
  - [x] 概率相同时按输出索引稳定排序。

- [x] `B-07` 完成 Model Pack 与预处理测试。

  依赖：`B-01` 至 `B-06`。

  工作内容：用正常、恶意和边界 Model Pack fixture 验证加载、安装、流水线、catalog 与性能基线。

  验收：

  - [x] 两个不同 mock pipeline 证明顺序和参数由 Model Pack 控制。
  - [x] 覆盖 schema、路径、hash、catalog、类型链、shape 和取消。
  - [x] 固定图片预处理性能和分配量形成可比较基线。

### 工作流 C：推理运行时、硬件加速与批处理

- [x] `C-01` 实现通用 Session 生命周期和运行时抽象。

  依赖：公共前置任务。

  工作内容：实现单 Session 创建、warm-up、销毁、实际 provider/设备报告和 CPU fallback 契约。

  验收：

  - [x] Session 创建/销毁不在 UI 线程。
  - [x] 输入/输出元数据与 ModelDescriptor 反向核对。
  - [x] provider 失败不会留下可用状态为真的半初始化 Session。

- [ ] `C-02` 实现 Windows ML 运行时。

  依赖：`C-01`。

  工作内容：使用 Windows ML 官方 API发现并注册兼容 NPU/GPU/CPU EP，创建 Session 并报告真实设备。

  验收：

  - [ ] Windows x64/ARM64 构建只包含 Windows 所需原生依赖。
  - [ ] 无兼容硬件时自动使用 Windows ML CPU。
  - [ ] provider 初始化失败能降级且有一次清晰通知。

- [ ] `C-03` 实现 macOS CoreML/CPU 运行时。

  依赖：`C-01`。

  工作内容：注册 CoreML EP，配置 MLProgram/ComputeUnits，支持 Apple Silicon 与 Intel，并保留 ORT CPU。

  验收：

  - [ ] arm64/x64 原生库独立打包且可枚举 CoreML。
  - [ ] Session 创建、首次编译或真实运行失败时回退 ORT CPU。
  - [ ] 缓存写入平台 Cache，而不是只读 `.app` 资源。

- [x] `C-04` 实现输出后处理和紧凑预测快照。

  依赖：`C-01`、`P-06`。

  工作内容：稳定计算 sigmoid，将每张图片结果保存为 `float[] Probabilities` 和少量运行元数据。

  验收：

  - [x] 极大正负 logits 不产生溢出/NaN。
  - [x] 输出长度不等于 labelCount 时拒绝结果。
  - [x] 快照原子提交且不复制标签文本。

- [x] `C-05` 实现 provider 自动选择。

  依赖：`C-02` 或 `C-03`、`C-04`。

  工作内容：对最多两个兼容硬件候选进行有预算的 warm-up/短基准，以端到端 images/sec 选择并缓存结果。

  验收：

  - [x] 总前台调优预算不超过 20 秒。
  - [x] 缓存键包含模型、provider、设备和运行时版本，环境变化后失效。
  - [x] 仅 provider 可创建但端到端更慢时选择 CPU。

- [x] `C-06` 实现识别当前与批量任务队列。

  依赖：`C-01`、`C-04`。

  工作内容：使用有界 Channel 实现单图优先、批量排队、逐图错误隔离、进度和 CancellationToken。

  验收：

  - [x] “识别当前”使用 batch 1。
  - [x] “识别全部”跳过当前模型下已成功且未过期的图片。
  - [x] 单图失败不终止整批，取消后不提交迟到结果。
  - [x] 切图期间结果按图片 ID 写回，不串图。

- [x] `C-07` 实现自适应 micro-batch。

  依赖：`C-05`、`C-06`。

  工作内容：按 `1→2→4→8` 在线试探，CPU 封顶 4、硬件 EP 封顶 8，依据吞吐、延迟和内存压力升级或回退。

  验收：

  - [x] 静态 batch 1 模型不会错误启用 batching。
  - [x] 吞吐提升不足 8% 时不升级。
  - [x] OOM/内存压力时回退并只重试受影响批次一次。
  - [x] 尾批使用真实数量，结果顺序正确。

- [x] `C-08` 完成推理、provider 和 batch 测试。

  依赖：`C-01` 至 `C-07`。

  工作内容：为通用 runtime、平台 factory、provider 选择、队列和 BatchSizeOptimizer 建立自动测试。

  验收：

  - [x] 小型 ONNX fixture 覆盖 Session、输出和 batch 1/2/4 等价性。
  - [x] fake provider 覆盖超时、失败、变慢、OOM 和缓存失效。
  - [x] Windows/macOS 平台测试可分别筛选执行。

### 工作流 D：识别标签选项卡

- [x] `D-01` 实现标签分组和阈值 ViewModel。

  依赖：公共前置任务。

  工作内容：从 fake/真实概率数组与 TagCatalog 按 manifest 声明的分组生成只读视图（WD Canary：分级、角色、通用）。

  验收：

  - [x] 组内严格按置信度降序，平分按索引升序。
  - [x] 分级只显示最高概率的一项并标记最高项（平分按索引升序），落选的低置信度分级不再展示；其它组应用 `>= threshold`。
  - [x] 阈值变化不重跑模型，100 ms 内刷新标签和 Prompt 输入。

- [x] `D-02` 实现动态宽度标签块控件。

  依赖：`D-01`、`P-08`。

  工作内容：用 ItemsControl/ItemsRepeater 和 WrapLayout 实现内容自适应宽度、自动换行、最大宽度与 ToolTip。

  验收：

  - [x] 每块显示原始标签、中文翻译和两位小数置信度。
  - [x] 不存在固定列数，窗口变窄时不重叠或改变数据排序。
  - [x] 缺失翻译显示“暂无翻译”。

- [x] `D-03` 实现标签手动排除状态。

  依赖：`D-01`、`D-02`。

  工作内容：点击标签块维护 `ForceExcludedIndices`；支持清除手动覆盖和重新识别后的保留。

  验收：

  - [x] 取消选择只影响 Prompt，不删除标签结果。
  - [x] 阈值变化不会意外清除手动排除。
  - [x] 模型变更后旧选择不会错误应用到新 catalog。

- [x] `D-04` 实现标签页摘要、空闲和错误状态。

  依赖：`D-01` 至 `D-03`。

  工作内容：连接摘要信息、识别命令和各 AnalysisState 对应的 ShadUI 视图。

  验收：

  - [x] 显示命中数、总输出数、阈值、耗时、runtime/provider/device。
  - [x] 未识别、加载、首次识别、重新识别、取消、失败和过期状态符合设计。
  - [x] 不增加搜索框。

- [ ] `D-05` 完成标签 UI 与排序测试。

  依赖：`D-01` 至 `D-04`。

  工作内容：为分组/排序/选择纯逻辑和动态标签块的 Headless/截图行为建立回归测试。

  验收：

  - [ ] 覆盖阈值边界、平分、缺翻译、空组、500 个可见标签和长文本。
  - [ ] Headless 测试验证选择状态与切图不串数据。
  - [ ] 两种窗口宽度的截图验证流式布局。

### 工作流 E：AI 生成元数据

- [x] `E-01` 实现通用元数据读取器。

  依赖：公共前置任务。

  工作内容：使用 MetadataExtractor 和 ImageSharp metadata API 读取 PNG/JPEG/TIFF/WebP 的文本、EXIF 和 XMP 候选值。

  验收：

  - [x] 不自行解析已有库支持的图片容器。
  - [x] 单字段、JSON 深度和总大小限制生效。
  - [x] 损坏字段不丢弃其它可用元数据。

- [x] `E-02` 实现 AUTOMATIC1111/Forge 解析器。

  依赖：`E-01`。

  工作内容：把 A1111/Forge parameters 文本解析为统一 GenerationInfo，并保留无法规范化的原值。

  验收：

  - [x] 正向/负向提示词和常见尾部参数正确拆分。
  - [x] 支持 Steps、Sampler、Scheduler、CFG、Seed、Size、Model、VAE、Clip skip、Hires。
  - [x] 未识别字段保留原文。

- [x] `E-03` 实现 ComfyUI 解析器。

  依赖：`E-01`。

  工作内容：使用 System.Text.Json 遍历 ComfyUI prompt/workflow 节点，提取能够确定来源的生成参数。

  验收：

  - [x] 解析 prompt/workflow JSON 中的文本编码、checkpoint、VAE、LoRA、KSampler、尺寸和 seed。
  - [x] 复杂工作流只显示可证实值，不猜测最终组合顺序。
  - [x] 不执行节点路径、脚本或表达式。

- [x] `E-04` 实现 NovelAI 与未知来源解析。

  依赖：`E-01`。

  工作内容：解析 NovelAI 常见 JSON，并为无法识别的来源建立不丢数据的原始信息回退。

  验收：

  - [x] NovelAI Comment JSON、Software 和 Source 能规范化。
  - [x] 未识别来源仍展示发现的键和原始文本。

- [x] `E-05` 实现懒加载生命周期和生成信息 UI。

  依赖：`E-01` 至 `E-04`、`P-08`。

  工作内容：图片首次成为当前项时异步解析并会话缓存；实现来源 Badge、提示词区、参数网格、资源列表和原始元数据折叠区。

  验收：

  - [x] 批量导入不会立即解析所有文件。
  - [x] 切图后迟到结果只写回原图片。
  - [x] 所有文本按纯文本显示且可复制。

- [x] `E-06` 完成元数据 fixture 与安全测试。

  依赖：`E-01` 至 `E-05`。

  工作内容：建立各来源图片/元数据 fixture，覆盖解析正确性、限制、纯文本显示和懒加载竞态。

  验收：

  - [x] 覆盖三类工具的正常、部分、重复、损坏和超限样例。
  - [x] 覆盖一个文件含多套来源信息。
  - [x] Headless 测试验证空状态和部分解析警告。

### 工作流 F：Prompt 构建器

- [x] `F-01` 定义单套 PromptSettings 和默认值。

  依赖：公共前置任务。

  工作内容：定义分组顺序/启用状态（由 Model Pack manifest 声明驱动）、阈值覆盖、下划线转换、排除、替换、权重、分隔符、前后缀和最大标签数。

  验收：

  - [x] 默认顺序和默认值符合设计。
  - [x] 设置对象不可变或通过受控更新产生新值。
  - [x] 首版没有多方案 CRUD 或脚本规则。

- [x] `F-02` 实现纯函数 PromptBuilder。

  依赖：`F-01`。

  工作内容：严格按设计中的 12 步顺序构建 Prompt。

  验收：

  - [x] 分组顺序、阈值、手动排除、替换、去重、权重、截断和前后缀输出确定。
  - [x] 相同输入始终产生相同字符串。
  - [x] 空输入不产生可复制的空 Prompt。

- [x] `F-03` 实现 Prompt 规则持久化和恢复默认。

  依赖：`F-01`、公共 `ISettingsStore` 契约。

  工作内容：通过设置存储保存单套 PromptSettings，并实现防抖、损坏恢复和独立重置。

  验收：

  - [x] 规则保存到 `prompt-settings.json`，300 ms 防抖并原子替换。
  - [x] 文件损坏时保留损坏副本并恢复默认。
  - [x] “恢复默认”不影响其它应用设置。

- [ ] `F-04` 实现右侧规则编辑 UI。

  依赖：`F-01`、`P-08`。

  工作内容：实现分组启用/顺序、组内排序、阈值覆盖、转换/排除/替换/权重和输出规则控件。

  验收：

  - [ ] 上移/下移按钮能改变顺序。
  - [ ] 控件使用 ShadUI 且信息密度符合桌面 UI。
  - [ ] 右栏可折叠并恢复宽度。

- [x] `F-05` 实现 Prompt 响应更新、复制和文本导出。

  依赖：`F-02` 至 `F-04`。

  工作内容：把当前图片、标签选择、阈值和规则合并为防抖更新流，并接入平台剪贴板/导出服务。

  验收：

  - [x] 切图、阈值、标签勾选和规则变化在 100 ms 防抖后更新。
  - [x] 显示标签数和字符数。
  - [x] 复制成功有 Toast；同名 `.txt` 导出需用户确认路径且不覆盖图片。

- [ ] `F-06` 完成 Prompt 快照和 UI 测试。

  依赖：`F-01` 至 `F-05`。

  工作内容：用确定输入建立 Prompt 快照测试，并覆盖规则编辑、自动保存、切图和导出 UI。

  验收：

  - [ ] 覆盖所有规则组合、边界阈值、重复替换、权重 1.00 和最大标签数。
  - [ ] 切图测试证明不会沿用上一张 Prompt。
  - [ ] 设置损坏、恢复默认和自动保存测试通过。

### 工作流 G：设置、平台服务、日志与通用体验

- [x] `G-01` 实现平台资源和数据目录定位。

  依赖：公共前置任务。

  工作内容：使用平台 API定位内置 Models、Application Support/LocalApplicationData 和 Cache，不读取模型路径环境变量。

  验收：

  - [x] Windows/macOS 路径来自平台 API，业务代码不依赖当前工作目录。
  - [x] 设置只保存 Model Pack ID，不保存模型绝对路径。

- [x] `G-02` 实现原子设置存储。

  依赖：`G-01`。

  工作内容：保存主题、窗口布局、当前模型 ID、加速策略、文件夹递归选项和翻译显示选项。

  验收：

  - [x] 临时文件、flush、原子替换流程正确。
  - [x] 设置文件包含 schema version，并有从未知/旧版本安全回退的策略。
  - [x] 损坏设置保留备份并恢复默认。
  - [x] 保存失败不破坏上一份有效设置。

- [x] `G-03` 实现跨平台桌面服务。

  依赖：`G-01`。

  工作内容：封装文件/文件夹选择器、剪贴板、在文件管理器显示、调用默认看图应用和文本导出。

  验收：

  - [x] Windows 与 macOS 行为均使用平台/Avalonia API。
  - [x] 用户取消选择不作为错误。
  - [x] 外部打开失败只显示非阻断通知。

- [x] `G-04` 实现设置对话框。

  依赖：`G-02`、`P-08`。

  工作内容：实现模型目录选择/校验/清除入口、主题/行为设置、加速策略、重新检测性能和清除内存缓存。

  验收：

  - [x] 不出现模型路径输入框、环境变量说明或手动 batch/线程/cache 数值旋钮。
  - [x] 不实现模型变体控件；每个 Model Pack 只对应一个 ONNX。
  - [x] 设置变化即时生效或明确标注下次任务生效。

- [x] `G-05` 实现日志、Toast、Dialog 和错误映射。

  依赖：`G-01`、`G-02`。

  工作内容：配置 Serilog 滚动文件，并将领域/平台错误统一映射为 Toast、Dialog 或日志详情。

  验收：

  - [x] 日志每日滚动、有总量上限且默认不记录完整路径、Prompt、元数据或图片内容。
  - [x] 重复批量错误合并，不连续轰炸 Toast。
  - [x] 用户错误有可理解结论，技术详情可展开或写日志。

- [ ] `G-06` 完成快捷键、主题和无障碍支持。

  依赖：`G-03`、`G-04`、`G-05`。

  工作内容：集中注册快捷键、主题资源、自动化名称、焦点顺序和中文字符串资源。

  验收：

  - [ ] 设计快捷键全部生效且文本编辑焦点下不冲突。
  - [ ] 图标按钮有 ToolTip 和 AutomationProperties.Name。
  - [ ] 焦点、状态和错误不只依赖颜色表达。
  - [ ] 中文字符串集中管理，不散落在 ViewModel。

## 4. 统一收尾任务

以下任务在并行工作流完成后统一执行，用真实实现替换 fake，完成端到端验证和发行。优先按编号推进；依赖允许时 `U-04/U-05`、`U-06/U-08` 可以并行。

### U-01 统一依赖注入与真实实现接线

- [ ] `U-01` 在 Composition Root 中接入所有真实服务和 ViewModel。

  依赖：`A-05`、`B-07`、`C-08`、`D-05`、`E-06`、`F-06`、`G-06`。

  工作内容：移除生产路径的 fake，用 Microsoft.Extensions.DependencyInjection 完成最终对象图和主窗口组合。

  验收：

  - [ ] 生产启动路径不再使用 fake/design service。
  - [ ] 单例 Session、ModelPackService、缓存和 ViewModel 生命周期正确。
  - [ ] 不使用 Service Locator，依赖均通过构造函数注入。

### U-02 完成端到端图片状态与竞态整合

- [ ] `U-02` 验证导入、切图、识别、元数据和 Prompt 的统一状态流。

  依赖：`U-01`。

  工作内容：以真实服务执行主要用户流程和高频竞态场景，统一修正状态所有权与取消边界。

  验收：

  - [ ] 快速切图、重复识别、批量识别、取消和移除图片不会串数据。
  - [ ] 模型切换使旧结果过期，重新识别后恢复正常。
  - [ ] 标签、生成信息和 Prompt 始终对应同一当前图片 ID。
  - [ ] 应用关闭时运行中任务按设计确认和取消。

### U-03 执行真实模型正确性验证

- [ ] `U-03` 用内置 Model Pack 完成预处理和推理对照。

  依赖：`U-01`、`U-02`。

  工作内容：运行 RealModel 测试集，对照 Python/Pillow golden 和真实标签基线。

  验收：

  - [ ] 全部 golden fixture 通过。
  - [ ] 固定图片概率、Top-N 和阈值集合与 Python 参考在约定范围内一致。
  - [ ] 标签数、翻译、category 映射分组和默认阈值正确。
  - [ ] batch 1 与批量推理逐图片输出一致。

### U-04 完成 Windows ML 真机验证

- [ ] `U-04` 在 Windows x64 和 ARM64 真机或受支持设备池上验证 Windows ML。

  依赖：`U-03`。

  工作内容：在干净 Windows 环境运行 provider、batch、fallback 和离线端到端测试并记录设备信息。

  验收：

  - [ ] 能显示实际 NPU/GPU/CPU EP 与设备。
  - [ ] 硬件 EP 有收益时被选中，无收益或失败时回退 CPU。
  - [ ] 自适应 batch、OOM 回退和缓存失效行为正确。
  - [ ] 干净安装后离线可识别，无环境变量或额外模型配置。

### U-05 完成 macOS CoreML 真机验证

- [ ] `U-05` 在 Apple Silicon 和 Intel Mac 上验证 CoreML/CPU。

  依赖：`U-03`。

  工作内容：在两类 Mac 真机运行 CoreML 编译、profiling、batch、cache、fallback 和离线端到端测试。

  验收：

  - [ ] CoreML 可枚举、首次编译和缓存位置正确。
  - [ ] profiling 显示实际节点覆盖；端到端更慢时选择 CPU。
  - [ ] 动态 batch、内存压力回退和模型缓存失效正确。
  - [ ] `.app` 离线运行且不写只读资源目录。

### U-06 完成 UI 视觉、响应式与无障碍审查

- [ ] `U-06` 对最终真实数据界面进行视觉 QA。

  依赖：`U-02`。

  工作内容：用真实长文件名、标签、错误和元数据填充界面，完成截图、缩放、主题和键盘审查。

  验收：

  - [ ] 1440×900、1120×720、浅色、深色截图均通过评审。
  - [ ] 最终应用图标、窗口图标和必要空状态图形清晰且风格一致。
  - [ ] 标签块动态宽度、超长文本、500 个可见标签和空组布局正确。
  - [ ] 没有大图预览、搜索框、网页式大卡片堆叠或不一致图标。
  - [ ] 100%、125%、150%、200% 缩放和键盘导航可用。

### U-07 完成性能与内存验收

- [ ] `U-07` 建立并通过最终性能基线。

  依赖：`U-03`、`U-04`、`U-05`。

  工作内容：使用固定设备/fixture 测量启动、切图、阈值更新、批量吞吐、阶段耗时、分配和峰值内存。

  验收：

  - [ ] 空载窗口目标 2 秒内可交互，模型异步加载不阻塞首屏。
  - [ ] 已有缩略图切图 <100 ms；阈值到标签/Prompt 更新 <150 ms。
  - [ ] 1,000 文件列表保持可用，不一次解码全部缩略图。
  - [ ] 批量任务内存有界、只有一个 Session、无持续增长或池化泄漏。
  - [ ] decode、geometry、tensor、copy、inference 分段指标可查看；相比基线回退不超过 10%。

### U-08 完成安全、隐私和依赖审计

- [ ] `U-08` 对文件输入、Model Pack、日志和第三方依赖进行发布前审计。

  依赖：`U-01`、`P-09`。

  工作内容：执行安全 fixture、隐私日志检查、路径扫描、SBOM、漏洞/许可证和 ADR 审计。

  验收：

  - [ ] Model Pack 路径越界、未知条目、损坏 hash 和未知 step 测试通过。
  - [ ] 图片像素上限、JSON 深度/大小和纯文本显示限制生效。
  - [ ] 仓库扫描无本机绝对路径，日志无敏感内容。
  - [ ] SBOM、漏洞扫描、许可证清单完成；自定义算法都有 ADR。

### U-09 生成并验证 Windows 发行物

- [ ] `U-09` 生成 Windows x64/ARM64 安装包或自包含发行包。

  依赖：`U-04`、`U-06`、`U-08`。

  工作内容：配置 Windows 发布项目、资源、版本和安装/卸载流程，并在干净环境执行冒烟测试。

  验收：

  - [ ] 发行物包含内置标准 Model Pack 且不包含 balanced/FP32/训练资产。
  - [ ] Windows ML 原生依赖、应用图标、版本信息和卸载流程正确。
  - [ ] 新用户环境首次启动、打开图片、识别、复制 Prompt 的冒烟测试通过。
  - [ ] 安装体积和文件清单有记录。

### U-10 生成并验证 macOS 发行物

- [ ] `U-10` 生成独立的 macOS arm64/x64 `.app` 发行物。

  依赖：`U-05`、`U-06`、`U-08`。

  工作内容：配置两架构发布、资源布局、签名/notarization，并在干净 Mac 上执行 Gatekeeper 和冒烟测试。

  验收：

  - [ ] 两个架构包均包含内置模型和正确 ORT/CoreML 原生库。
  - [ ] `.app` 内资源只读，缓存/设置写入平台目录。
  - [ ] code signing、notarization、Gatekeeper 和离线冒烟测试通过。
  - [ ] 不生成包含两份模型的 Universal 巨型包。

### U-11 完成用户与维护文档

- [ ] `U-11` 编写与最终实现一致的使用和维护说明。

  依赖：`U-09`、`U-10`。

  工作内容：根据最终 UI、命令、包结构和平台发行流程编写用户/开发者文档。

  验收：

  - [ ] README 包含支持平台、安装、打开/识别、标签、元数据、Prompt 和 Model Pack 目录配置说明。
  - [ ] 包含隐私说明、日志位置、模型许可证和故障排查。
  - [ ] 开发文档包含构建、测试分类、LFS、ModelPackTool 和平台发行步骤。
  - [ ] 文档没有本机绝对路径或 Linux 支持承诺。

### U-12 最终回归与发布签核

- [ ] `U-12` 按设计验收标准完成最终发布签核。

  依赖：`U-01` 至 `U-11`。

  工作内容：汇总自动测试、人工 QA、性能、安全、安装和文档证据，逐项对照设计第 21 节签核。

  验收：

  - [ ] 全部任务及子验收项已勾选，未用“以后补”替代首版要求。
  - [ ] Debug/Release 构建、全部非平台测试和两平台发行测试通过。
  - [ ] 设计第 21 节的功能、数据正确性和体验验收逐项有测试或人工记录。
  - [ ] 无未解释 TODO/FIXME、死代码、未使用依赖、占位 UI 或被跳过的必需测试。
  - [ ] 发布版本、变更记录、checksum、SBOM 和安装包均已归档。

## 5. 任务清单 Review

### 5.1 需求覆盖矩阵

| 设计范围 | 主要任务 | 最终验收 |
|---|---|---|
| ShadUI、三栏桌面界面、无搜索/无大图预览 | `P-08`、`A-04`、`U-06` | `U-12` |
| 图片导入、缩略图、列表和切图 | `A-01`–`A-05` | `U-02`、`U-07` |
| 内置模型与简单 Model Pack | `P-04`、`B-01`、`B-02` | `U-03`、`U-09`、`U-10` |
| 多模型与动态预处理顺序/参数 | `P-06`、`B-03`–`B-05` | `B-07`、`U-03` |
| 精确 WD 预处理 | `P-05`、`B-05` | `U-03` |
| Windows ML | `C-02`、`C-05` | `U-04`、`U-09` |
| macOS CoreML/CPU | `C-03`、`C-05` | `U-05`、`U-10` |
| 自适应批处理与取消 | `C-06`、`C-07` | `U-02`、`U-07` |
| 标签分组、翻译、置信度、动态标签块 | `B-06`、`D-01`–`D-05` | `U-03`、`U-06` |
| SD WebUI/ComfyUI/NovelAI 元数据 | `E-01`–`E-06` | `U-02`、`U-08` |
| Prompt 规则、排序、复制和导出 | `F-01`–`F-06` | `U-02`、`U-06` |
| 设置、主题、平台服务、日志、无障碍 | `G-01`–`G-06` | `U-06`、`U-08` |
| Library-first、许可证与供应链 | `P-03`、`P-09` | `U-08` |
| 双平台安装与发行 | `U-09`、`U-10` | `U-12` |

覆盖结论：设计中的所有首版功能、正确性要求、非功能要求和发行要求均有实现任务及最终验收，没有仅出现在设计而未进入任务的功能。

### 5.2 依赖与并行可行性 Review

- 公共阶段先冻结依赖、模型包、golden、Schema、fake 和静态 UI，避免并行开发期间反复改公共契约。
- A–G 七个工作流主要修改不同目录，可同时开始；跨工作流依赖通过 `P-06` fake 隔离。
- Windows ML 与 macOS CoreML 可以分别在对应机器并行开发，共享 `C-01` 的运行时契约。
- 推理工作流不等待真实 UI；标签和 Prompt UI 不等待真实模型；统一在 `U-01` 接线。
- 最长关键路径为 `P-01→P-03→P-04→P-05→P-06→B-05/C-07→U-03→U-04/U-05→U-09/U-10→U-12`，依赖关系无循环。
- Model Pack 标签目录构建和 Pillow 兼容性是已知阻断项，已放在并行开发前解决，不会在收尾阶段才暴露。
- 平台签名/notarization 需要外部证书与 Apple 服务，执行前应确保凭据可用；缺少凭据不能把功能开发标记失败，但会阻止 `U-10` 和最终发布签核。

可行性结论：按顺序完成公共任务后并行推进 A–G，再依次完成 U-01–U-12，即可从空目录得到符合 `DESIGN.md` 的完整 Windows/macOS 应用和发行物。

### 5.3 最终遗漏检查

- [x] 设计第 1–25 节均已映射到至少一个实现或验收任务。
- [x] 每个外部输入面都有正常、损坏、超限和取消测试。
- [x] 每个异步流程都按图片 ID 防止迟到结果串图。
- [x] 每个可缓存对象都有失效键、容量上限和释放路径。
- [x] 每个平台运行时都有硬件路径、CPU fallback 和真机任务。
- [x] 每个发布包都包含内置模型、许可证、checksum 和离线冒烟测试。
- [x] 没有 Linux、数据库、搜索框、大图预览、多 Prompt 方案或手动性能旋钮任务。
- [x] 没有依赖开发机绝对路径、环境变量或源模型目录结构的任务。
