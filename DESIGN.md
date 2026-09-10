# Image Tagger 桌面应用设计说明

> 文档状态：实现前设计基线（已完成 KISS 与可实施性审查）  
> 编写日期：2026-09-01  
> 目标平台：Windows、macOS 桌面  
> UI 框架：Avalonia 12.1 + ShadUI 0.2.4  
> 架构风格：MVVM、离线优先、KISS

## 1. 文档目的

本文档定义一个跨平台 Avalonia 桌面应用，用本地 ONNX 模型为图片生成 Danbooru 标签，读取图片内嵌的 AI 生成元数据，并将识别标签按可配置规则转换为 AI 绘图 Prompt。

本文档是后续实现、测试和验收的唯一设计基线。本轮不创建项目代码。

## 2. 产品定位

### 2.1 核心目标

应用应让用户在一个桌面工作台中完成以下闭环：

1. 打开一张或多张本地图片。
2. 查看缩略图与文件信息，快速切换当前图片。
3. 用应用内置的 ONNX 模型识别当前图片或批量识别全部图片，首次启动无需配置。
4. 按 Model Pack 声明的分组（分级、角色、通用等）分组查看结果。
5. 对每个标签同时查看原始英文、中文翻译与置信度。
6. 查看 SD WebUI、ComfyUI、NovelAI 等工具嵌入图片的生成参数。
7. 配置标签筛选、变换和排列规则，实时生成 AI 绘图 Prompt。
8. 复制 Prompt，或明确选择后保存为同名文本文件。

### 2.2 设计原则

- **桌面优先**：高信息密度、固定工具栏、三栏可调整工作区、键盘快捷键和右键菜单；不做网页落地页式布局。
- **现代而克制**：使用 ShadUI 的中性色、细边框、适度圆角、紧凑间距和明确状态色，不堆叠巨大卡片或装饰性渐变。
- **结果可解释**：标签始终展示原始值、翻译、精确置信度和所属分组。
- **非破坏性**：默认不修改原图片，不覆盖图片元数据；所有导出操作均由用户显式触发。
- **离线优先**：推理、元数据解析、翻译查询和 Prompt 构建均在本机完成，不上传图片或标签。
- **KISS**：使用单一主窗口、清晰服务边界和少量项目；不引入数据库、消息总线或插件系统。

### 2.3 明确不做

- 不提供搜索框。
- 不训练、微调或下载模型。
- 不编辑图片像素，也不回写图片元数据。
- 不提供云端识别、账号、同步或遥测。
- 不支持移动端和浏览器端。
- 首版不支持 Linux，不创建 Linux 运行时、安装包、provider 适配、测试矩阵或兼容性承诺。
- 首版不实现自动标签补全、在线 Danbooru 查询或复杂节点图编辑器。

### 2.4 设计审查后的首版收敛

为保证实现简洁且可按期验收，首版采用以下明确取舍：

- 只内置 quality ONNX，不随包附带 balanced/FP32，安装体积约减少 603 MiB。
- Prompt 构建器只保存一套可恢复默认的规则，不实现多方案管理。
- batch、线程、预处理并行度和缓存大小全部自动管理，不向用户暴露底层性能旋钮。
- Model Pack 仍可动态排列预处理算子，但首版只做类型/参数/shape 校验、顺序编译和执行计划缓存，不实现通用优化器。
- 每张图片只保存连续概率数组；标签文本、翻译和分组由全局目录共享，不创建成千上万个重复对象。
- 只有平台边界或确有多个实现的服务才定义接口；内部协作类优先使用可直接测试的具体类型。
- 不增加数据库、插件系统、脚本语言、后台服务或预测结果持久化。

这些取舍不削减用户明确要求的多模型扩展、动态预处理、硬件加速、自适应 batch、元数据解析和 Prompt 规则能力。

## 3. 已确认的模型事实与约束

当前验证对象是 `wd-eva02-tagger-2026-canary` 的源资产集合；它会在构建阶段整理成应用标准 Model Pack。本文档、源码和仓库配置只引用包内相对路径，不记录源资产的本机绝对位置。

### 3.1 模型资产

| 源资产 | 应用内用途 |
|---|---|
| quality INT8 ONNX | 首版唯一内置模型，约 818.4 MiB |
| balanced INT8 ONNX | 保留为开发评估资产，首版不随应用发行 |
| FP32 ONNX | 仅用于开发校验，不进入发行模型包 |
| 标签与翻译源数据 | 构建时合并为单一运行时标签目录 |
| SHA-256 清单与许可证 | 转换为发行模型包的完整性和授权文件 |

### 3.2 输入与输出契约

- 模型架构：`eva02_large_patch14_448`。
- 输入节点：`images`。
- 输入形状：`[N, 3, 448, 448]`，`float32`，其中 ONNX batch 轴 `N` 为动态；识别当前图片时 `N = 1`。
- 输出：16,473 个 logits。
- 概率计算：逐项执行 sigmoid。
- 推荐全局阈值：`0.6094`。
- quality 模型相对 FP32 的阈值集合 Jaccard 均值约为 95.36%，作为默认选择。
- balanced 模型虽然更快，但精度下降且会让安装包额外增加约 602.8 MiB，因此不进入首版；未来若需要，作为独立 Model Pack 评估。

### 3.3 精确预处理契约

实现必须逐步复现模型仓库 `scripts/model_utils.py` 中的 Python/Pillow 参考逻辑，而不是采用“看起来等价”的通用图片变换：

1. 读取图片并应用 EXIF Orientation。
2. 若图像模式不是 RGB/RGBA：存在透明信息时先转 RGBA，否则转 RGB。
3. RGBA 必须在 `(255, 255, 255, 255)` 纯白画布上执行 alpha composite，再转 RGB；禁止简单丢弃 alpha。
4. 令 `side = max(width, height)`，建立 `side × side` 的纯白 RGB 画布。
5. 原图粘贴坐标严格为 `((side - width) / 2 向下取整, (side - height) / 2 向下取整)`；奇数差值多出的 1 px 必须落在右侧或底部。
6. 使用与 Pillow `Image.Resampling.BICUBIC` 一致的坐标映射、边界处理和 bicubic 核缩放到 `448 × 448`，不能仅因另一个库也把算法命名为 Bicubic 就认为结果等价。
7. 按 `float32` 读取像素并将 RGB 通道反转为 BGR。
8. 每个通道按 `value / 255.0f` 转为 `[0,1]`，再按 `(value - 0.5f) / 0.5f` 转为 `[-1,1]`；所有运算保持 `float32`，不得中途用整数近似。
9. 从 HWC 转换为连续内存的 NCHW，并增加 batch 维，最终形状为 `[1,3,448,448]`。

禁止自动裁剪、拉伸、黑色补边、RGB 输出、ImageNet mean/std、预乘 alpha 残留、ICC 自动色彩校正或未经验证的缩放器替代上述流程，否则结果不可与参考实现对齐。

准确性由测试而不是代码审查主观判断：

- 从参考 Python 脚本生成版本化 golden fixtures，至少覆盖横图、竖图、奇数尺寸、RGB PNG、RGBA PNG、调色板透明 PNG、灰度图和带 8 种 EXIF Orientation 的 JPEG。
- 无损格式在关键阶段分别校验“转向后 RGB 像素”“补白后像素”“缩放后像素”和“最终 Tensor”；最终 Tensor 必须逐元素一致。若底层解码器存在已证实差异，容差必须写入差异报告，不能自行放宽。
- JPEG 解码器可能产生最多 1 LSB 的平台差异，因此除 Tensor 误差外还必须验证固定图片的输出概率、Top-N 顺序和阈值集合与 Python 参考结果一致。
- WD 内置 Model Pack 使用文档所列的版本化 steps；任何算子版本、顺序、参数或解码依赖变化都会改变流水线指纹并使旧结果过期。
- 启动模型时编译并校验完整流水线，最终单样本 Tensor 必须与输入节点的布局、尺寸和 dtype 一致；任何不匹配均拒绝推理，不能静默采用默认值。

### 3.4 多模型扩展边界

首版只交付并完整验证 WD EVA02 2026 Canary，但核心不能把 `448`、BGR、sigmoid、16,473 或 CSV 格式散落硬编码在 UI 和通用推理服务中。

每个可加载模型被视为一个 **Model Pack**，由模型描述、一个 ONNX、合并后的标签目录、校验清单、许可证和预处理流水线组成。一个包只对应一个模型，避免变体分支；另一个 ONNX 使用另一个 Model Pack。

内置 WD Model Pack 不照搬训练/转换仓库结构。发行时使用以下扁平目录，去掉 safetensors、FP32、脚本、校准图片、验证报告、缓存和 Python 环境：

```text
Models/
  wd-eva02-tagger-2026-canary/
    model.json
    model.onnx
    tags.csv
    checksums.sha256
    LICENSE.txt
```

`tags.csv` 使用 UTF-8，固定列为：

```text
id,name,group,translation,count
```

- `id` 必须与模型输出索引一致且从 0 连续。
- `name` 是不可改写的原始标签。
- `group` 是按源 `category` 数字映射得到的 `rating/artist/copyright/character/general`。
- `translation` 是中文翻译，允许为空。
- `count` 保留源标签频次，首版不展示但可供未来稳定排序或模型工具使用。

模型描述至少包含：

```json
{
  "schemaVersion": 1,
  "id": "wd-eva02-tagger-2026-canary",
  "displayName": "WD EVA02 Tagger 2026 Canary",
  "task": "multi-label-image-tagging",
  "model": "model.onnx",
  "groups": [
    { "id": "rating", "name": "Rating", "displayName": "分级" },
    { "id": "character", "name": "Character", "displayName": "角色" },
    { "id": "general", "name": "General", "displayName": "通用" }
  ],
  "input": { "name": "images", "layout": "NCHW", "dtype": "float32", "batch": "dynamic", "width": 448, "height": 448 },
  "preprocessing": {
    "schemaVersion": 1,
    "steps": [
      { "op": "decode", "version": 1, "frame": "first", "colorManagement": "ignore" },
      { "op": "exif-transpose", "version": 1 },
      { "op": "ensure-color", "version": 1, "mode": "rgb-or-rgba-if-transparent" },
      { "op": "alpha-composite", "version": 1, "when": "has-alpha", "background": [255, 255, 255] },
      { "op": "pad-to-square", "version": 1, "anchor": "floor-center", "background": [255, 255, 255] },
      { "op": "resize", "version": 1, "width": 448, "height": 448, "sampler": "pillow-bicubic-v1" },
      { "op": "reorder-channels", "version": 1, "order": [2, 1, 0] },
      { "op": "cast", "version": 1, "dtype": "float32" },
      { "op": "divide", "version": 1, "value": 255.0 },
      { "op": "normalize", "version": 1, "mean": [0.5, 0.5, 0.5], "std": [0.5, 0.5, 0.5] },
      { "op": "permute", "version": 1, "order": [2, 0, 1] }
    ]
  },
  "output": { "name": "logits", "activation": "sigmoid", "labelCount": 16473 },
  "catalog": "tags.csv",
  "defaultThreshold": 0.6094
}
```

- `groups` 由 Model Pack 自带，声明该模型输出的全部分组：`id` 是 `tags.csv` `group` 列引用的稳定标识（小写、唯一、非空），`name` 是英文显示名，`displayName` 是中文显示名。UI 分组和 Prompt 规则完全由该声明驱动，不硬编码 Danbooru 分组；任何多标签模型即使使用不同分组体系也能直接被支持。
- `groups` 数组顺序即“识别标签”选项卡的显示顺序；Prompt 构建器的默认分组顺序为该数组的逆序（主题标签在前、分级最后），用户可在构建器中用“上移/下移”按钮调整。
- JSON 只描述数据，不允许注入脚本、类型名或任意代码。
- Model Pack 内所有路径都是相对自身根目录的简单文件名；首版禁止子目录、`..`、绝对路径、符号链接和跨包引用。
- 预处理由有类型、有版本、有参数 schema 的标准算子动态组成；Model Pack 控制顺序和参数，但不能执行任意脚本。
- 新模型可优先组合已有算子；只有需要全新原子算法或输出语义时，才新增小型算子实现或 `ITaggerModelAdapter`，不修改主窗口、图片会话或 Prompt 构建器。
- Model Pack 验证成功后才出现在模型选择器中；未知 schema、任务类型、算子/version/参数或 activation 必须给出明确错误。
- 构建工具从源模型资产一次性生成上述标准包；应用运行时只理解标准 Model Pack，不兼容或探测训练仓库的任意目录布局。
- 首版不做第三方插件加载和动态程序集执行；扩展点是编译期适配器 + 数据驱动 manifest，兼顾可扩展性、安全性和 KISS。
- 其它模型以同样的标准目录结构提供；用户显式选择目录后，应用直接校验路径、hash、schema 和适配器契约，不复制或改写外部模型文件。

#### 3.4.1 模型特定的预处理架构

缩略图解码与模型输入预处理必须完全分离。推理不能复用 Avalonia 缩略图、屏幕色彩转换后的 Bitmap 或某个所谓“通用 448 图片”，因为不同模型可能要求完全不同的方向、裁剪、插值、颜色、数值范围、layout 和 dtype。

```text
原始图片文件
  ├─► ThumbnailService ─► 仅用于左侧 UI
  └─► PreprocessingPipelineCompiler
        └─► 已编译执行计划（按 Model Pack 的 steps 顺序）
              ├─ Decode/Pixel 算子
              ├─ Geometry/Color 算子
              ├─ Tensor/Numeric/Layout 算子
              └─► 单样本 Tensor ─► BatchComposer ─► ONNX 输入 Tensor
```

核心契约：

```text
PreprocessingPipelineDescriptor
  SchemaVersion, OrderedSteps[]

PreprocessStepDescriptor
  Op, Version, Parameters

IPreprocessOperatorFactory
  Op, SupportedVersions, ParameterSchema
  InputKind, OutputKind
  InferShape(input, parameters)
  Compile(parameters) -> IPreprocessOperator

IPreprocessingPipelineCompiler
  ValidateAndCompile(descriptor, modelInputContract)
  -> CompiledPreprocessingPipeline

CompiledPreprocessingPipeline
  Fingerprint
  PerSampleTensorContract
  PreprocessIntoAsync(ImageSource, destinationSlice, CancellationToken)

ModelInputTensor
  DType, Shape, Layout, ContiguousBuffer
```

**算子与顺序**

- 流水线是线性的有序步骤表，严格按 `steps` 数组执行；同一组算子以不同顺序出现时视为不同流水线和不同指纹。
- 每个算子由稳定的 `op + version` 标识。版本改变表示数值语义可能改变，旧 Model Pack 不会静默升级到新算法。
- 每个算子拥有封闭参数 schema：字段类型、必填项、枚举、数值范围、数组长度和默认值都明确；未知参数直接报错，避免拼写错误被忽略。
- 首版标准算子只包括当前 WD 与常见 RGB/CLIP 输入需要的：`decode`、`exif-transpose`、`ensure-color`、`alpha-composite`、`pad-to-square`、`resize`、`crop`、`reorder-channels`、`cast`、`divide`、`normalize` 和 `permute`。其它算子按真实模型需求再增加。
- 只允许少量声明式条件，例如 `when: has-alpha`；不支持表达式、循环、分支脚本、反射类型名、文件访问或网络访问。

**类型链与契约校验**

- 算子输入/输出属于 `EncodedImage`、`PixelImage` 或 `Tensor` 等显式类型。编译器逐步验证类型链，例如 `resize` 不能出现在 `cast` 之后，`normalize` 不能作用于编码字节。
- 编译器执行静态 shape inference，跟踪宽、高、通道数、dtype 和 layout；无法静态确定时必须由算子给出受限动态维规则。
- 流水线最大 32 步，目标像素数、通道数、Tensor 字节数和解码尺寸都有硬上限，防止恶意 Model Pack 制造内存爆炸。
- 流水线终点是**单样本** Tensor。其 dtype、layout 和非 batch 维必须与 ONNX 实际输入完全一致；`BatchComposer` 只负责在 batch 维连续堆叠，不改变样本内容。
- `decode` 必须是第一个生产图像的步骤且只能出现一次；最终必须恰好产生一个 Tensor。重复 decode、无输出、悬空图像或输入契约不匹配均拒绝安装。
- 未知算子或不支持的版本不回退到“类似”操作；UI 提示该 Model Pack 需要更高版本应用。

**不同模型的表达能力**

- WD Canary 使用 manifest 中列出的顺序，精确表达 EXIF transpose、透明图白底合成、floor-center 方形补白、Pillow bicubic、BGR、除以 255、mean/std 和 HWC→CHW。
- CLIP 风格模型可以组合 `decode → exif-transpose → ensure-color(RGB) → resize(short-edge) → crop(center) → cast → divide → normalize(CLIP mean/std) → permute`。
- 拉伸输入模型可以省略 pad/crop，直接给 `resize` 指定固定宽高。
- 新的预处理需求若能由已有原子算子准确表达，只需新的 manifest；只有出现新插值核、特殊色彩变换等原子能力时才增加一个经过测试的新算子版本。

**高性能实现**

- manifest 只在模型加载时解析一次。验证通过后转换为不可变的顺序执行计划，并按流水线指纹缓存；每张图片热路径不使用 JSON、反射、字符串查找或动态类型。
- 首版不实现通用算子融合优化器。各步骤直接调用第三方库；只保留将最终像素写入 ORT Tensor 所必需的最小数据封送代码。只有 profiling 证明预处理是实际瓶颈时，才针对已验证的具体步骤组合增加优化，并单独记录 ADR。
- `PreprocessIntoAsync` 直接写入池化 batch buffer 的对应样本切片，避免“每图分配 Tensor → 再 concatenate”的额外分配与复制。
- Pixel buffer、临时缩放 buffer 和 Tensor buffer 使用 .NET `ArrayPool/MemoryPool` 或第三方库自带的 allocator；所有成功、异常和取消路径都归还。
- 像素级并行、crop/pad/resize 和 alpha composite 委托 ImageSharp 等选定库；数值阶段优先调用 `TensorPrimitives` 与 CommunityToolkit.HighPerformance。禁止在业务代码中手写 SIMD/intrinsics，除非 Library-first ADR 证明库能力缺失。
- 第三方库的优化路径必须通过同一套 golden；结果不一致时禁用对应优化或更换库版本，不用补丁代码掩盖偏差。
- 预处理并行度与 ONNX provider 线程协调，避免 CPU oversubscription；默认上限为 `min(max(逻辑核心数 - 2, 1), 4, batchSize)`，由运行时内部使用，不暴露给用户。
- pipeline compiler 预先确定 shape、目标 offset 和所需 scratch 大小；循环内不重复解析配置。
- 解码流按需读取并设置像素/文件上限；不为了元数据或缩略图在推理热路径中重复创建 Avalonia Bitmap。

**指纹、生命周期与验证**

- 流水线指纹由规范化后的完整 steps JSON、每个算子实现版本和解码库版本构成，纳入模型结果指纹。
- 同一批任务冻结一份已编译流水线；任务中切换模型时取消剩余队列并重新建队，不能在一个 batch 混合不同输入契约。
- 每个标准算子需要参数 schema 和边界测试；每个 Model Pack 还需要逐阶段 golden 和端到端模型输出对照。
- 满足这些条件前，即使 ONNX Session 能打开，也不能在 UI 中标记为可用模型。

### 3.5 内置模型的交付与定位

- 首版只随应用发行 quality INT8 ONNX，并在标准包内命名为 `model.onnx`；应用离线安装后即可使用。
- 模型作为发行内容文件而不是 .NET assembly embedded resource，避免启动时从单文件程序集解压约 818 MiB；这仍属于软件内置资产。
- Model Pack 文件夹名必须等于 manifest 的规范化 `id`。仓库内统一相对位置为 `Assets/Models/wd-eva02-tagger-2026-canary/`；发布后由 `IAppResourceLocator` 定位平台资源根目录，业务代码不拼接可执行文件当前工作目录。
- Windows 安装包把模型放入应用安装目录的只读 `Models`；macOS 放入 `.app/Contents/Resources/Models`。位置由打包项目产生，不写入业务源码。
- 若仓库纳入大模型文件，必须使用 Git LFS；CI 和发行任务校验文件不是 LFS pointer，并对照 `checksums.sha256`。
- `checksums.sha256` 覆盖 `model.onnx`、`tags.csv`、`model.json` 和 `LICENSE.txt`，自身不参与 hash。
- 任一内置文件缺失或 hash 错误时进入“内置模型损坏”修复页，不要求用户寻找目录；提示重新安装对应发行包。
- 关于对话框显示模型名称、模型/标签数据日期、许可证和校验状态。

### 3.6 分类数据策略（2026-09-01 范围变更）

应用完全依赖当前模型包自带的数据：`selected_tags.csv` 提供标签与原始分类，`translated_tags_zh.jsonl` 提供中文翻译。不引入任何外部 taxonomy 数据集；模型输出什么标签，应用就展示什么标签，即使不是 Danbooru 标签也按同一机制支持。

源 `selected_tags.csv` 的 `category` 数字映射到运行时分组：

| 源 category | 运行时分组 |
|---:|---|
| 9 | `rating` |
| 4 | `character` |
| 0 | `general` |
| 1 | `artist` |
| 3 | `copyright` |
| 其它/缺失 | `general` |

确定性策略：

1. 构建时把 `category` 按上表映射为 `tags.csv` 的 `group` 列；不在构建或运行时按标签名称猜测分类。
2. 中文翻译按标签 ID 合并，同时校验 tag 文本，防止版本错位。
3. 当前模型没有提供 artist/copyright 类输出，这两个分组在 UI 中保持紧凑空状态；若未来某个 Model Pack 的标签目录包含这两类分组，同一机制自动生效。
4. 构建报告记录各分组数量与未映射 category 清单，写入 `model.json` 并显示在“关于模型”对话框中。

## 4. 信息架构与主窗口

### 4.1 窗口规格

- 默认尺寸：`1440 × 900`。
- 最小尺寸：`1120 × 720`。
- 使用 `ShadUI.Controls.Window`，启用原生平台窗口按钮与窗口状态保存。
- 初次启动居中；后续恢复上次尺寸、位置和最大化状态。
- 默认跟随系统明暗主题，标题栏与内容主题一致。
- 主窗口只存在一个；设置、模型信息和确认操作使用 ShadUI Dialog。

### 4.2 整体布局

```text
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ 标题栏  Image Tagger                                     主题  设置  最小化 最大化 关闭 │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ 打开图片  打开文件夹 │ 识别当前  识别全部  取消 │ 模型：WD EVA02 │
├───────────────┬────────────────────────────────────────────┬─────────────────────────┤
│ 图片列表       │ 当前文件名 · 尺寸 · 格式 · 状态               │ Prompt 构建器            │
│ 286 px         ├────────────────────────────────────────────┤ 360 px                  │
│               │ [ 识别标签 ] [ 生成信息 ]                    │ 分组与规则                │
│ [缩略图] 文件1 │                                            │ 分组开关与顺序         │
│ 名称/尺寸/格式 │ 当前选项卡占满中间区域，可滚动                │ 过滤与格式规则             │
│ 状态/进度      │                                            │                         │
│ [缩略图] 文件2 │                                            ├─────────────────────────┤
│ ...           │                                            │ Prompt 预览（固定底部）    │
├───────────────┴────────────────────────────────────────────┴─────────────────────────┤
│ 就绪 │ 2/12 │ 2048×3072 PNG │ Quality / CPU │ 推理 3.42 s                         │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

三栏之间使用 `GridSplitter`：

- 左栏默认 286 px，可调范围 240–420 px。
- 右栏默认 360 px，可调范围 320–480 px。
- 中栏占余下空间，最小 480 px。
- 双击分隔条恢复默认宽度。
- 窗口宽度不足时，优先允许用户用工具栏按钮折叠右栏；不自动将桌面 UI 重排成移动端单列。
- 左栏和右栏的展开状态、宽度均持久化。

### 4.3 视觉语言

- 基础间距使用 4 px 网格；常用间距为 8、12、16、24 px。
- 工具栏高 52 px；状态栏高 28 px；普通按钮高 32–36 px。
- 面板边界使用 1 px 低对比度边框；仅浮层和对话框使用阴影。
- 常规圆角 6 px，卡片/对话框 8 px；避免大面积胶囊形控件。
- 字体使用系统 UI 字体，正文 13 px，辅助信息 11–12 px，标题 14–16 px。
- 英文原始标签使用等宽或半等宽样式，中文翻译使用系统字体。
- 主强调色只用于当前选择、主要按钮和进度；错误、警告、成功使用语义色。
- 深色模式不使用纯黑，浅色模式不使用纯白大面积强对比背景。
- 所有图标统一使用 ShadUI 自带的 Lucide 图标资源，不混用不同风格图标集。

## 5. 顶部工具栏设计

工具栏按操作频率从左至右排列，不包含搜索框。

| 控件 | 行为 | 可用条件 | 快捷键 |
|---|---|---|---|
| 打开图片 | 多选本地图片并追加到列表；自动去重 | 始终 | `Ctrl+O` |
| 打开文件夹 | 加载文件夹第一层支持的图片；设置中可启用递归 | 始终 | `Ctrl+Shift+O` |
| 识别当前 | 对当前图片执行或重新执行识别 | 已选择有效图片且当前未运行 | `Ctrl+Enter` |
| 识别全部 | 将所有未识别、失败或已过期图片加入队列 | 列表非空且当前未运行 | `Ctrl+Shift+Enter` |
| 取消 | 取消当前任务与剩余队列 | 正在识别时显示 | `Esc` |
| 模型选择 | 当前 Model Pack；只有一个模型时显示名称而不展开下拉 | 未运行时 | 无 |
| 右栏开关 | 展开/折叠 Prompt 构建器 | 始终 | `Ctrl+Shift+P` |
| 更多 | 设置、重新加载模型、关于模型 | 始终 | 无 |

置信度阈值唯一来源是当前 Model Pack 的 `DefaultThreshold`（WD Canary 为 60.94%），工具栏不提供调节控件，
切换模型时自动同步为新包的值。阈值只决定可见集合，不改变已保存的原始概率，因此：

- 不重复执行模型，因为内存中保留当前模型的全部输出概率。
- 标签列表、各组计数和当前 Prompt 在 100 ms 防抖后重算。
- 分级的四个概率始终显示，不受阈值隐藏；分级的最高概率项标记为“最可能”。
- 标签摘要与各处阈值以百分数展示；模型包的 `DefaultThreshold` 即是唯一事实来源，不再单独持久化用户阈值。

工具栏忙碌状态：

- 当前正在执行的识别按钮变为 Spinner + “识别中”。
- 其他会改变模型或队列的按钮禁用。
- 取消按钮取代原工具栏分隔区中的静态位置，避免布局跳动。
- 批量任务显示 `3 / 18`，详细进度同时出现在底部状态栏。

## 6. 左侧图片列表

### 6.1 列表头

列表头显示：

- 标题“图片”。
- 当前数量，例如 `12 张`。
- 清空列表按钮。空闲时直接清除会话列表且不删除磁盘文件；有识别任务运行时先确认取消任务，避免维护额外的“未导出脏状态”。

不提供搜索、筛选输入框。

### 6.2 文件项

每项默认高 80 px，结构如下：

```text
┌──────────────────────────────┐
│ ┌──────┐  very_long_name…png │
│ │ 64²  │  2048×3072 · 8.4 MB │
│ │ 缩略图│  PNG · 已识别  47 tags│
│ └──────┘  ● 当前/进度/错误     │
└──────────────────────────────┘
```

必须展示：

- 64 × 64、统一圆角的等比缩略图；透明图片使用棋盘格背景。
- 文件名，单行省略；悬停 ToolTip 显示完整路径。
- 像素尺寸与文件大小，采用易读单位。
- 格式：JPEG、PNG、WebP、BMP、GIF（只取首帧）或 TIFF；具体支持范围以解码器能力为准。
- 识别状态：未识别、排队中、识别中、已识别、已取消、失败、模型已变更。
- 成功时显示高于当前阈值的标签数；失败时显示简短原因并提供重试。

### 6.3 列表行为

- 支持文件选择器、文件夹选择器以及从资源管理器/Finder 拖放图片或目录。
- 使用规范化绝对路径去重；Windows 使用不区分大小写比较，macOS 按实际文件系统的大小写规则处理。
- 默认按用户添加顺序排列，不擅自按文件名重排。
- 单击切换当前图片；切换后中间标签、元数据和右侧 Prompt 必须原子更新，不能短暂显示上一张图的数据。
- `↑/↓` 选择相邻图片，`Delete` 仅从列表移除，不删除磁盘文件。
- 右键菜单：识别/重新识别、复制文件路径、在文件管理器中显示、从列表移除。
- 双击缩略图调用系统默认看图应用，此操作失败时显示 Toast，不影响会话。
- 列表使用虚拟化；缩略图在可见区域附近懒加载。

### 6.4 缩略图与内存

- 缩略图最长边解码到 128 px，不把原图完整位图长期保存在列表中。
- 缩略图缓存使用设置了 `SizeLimit` 的 `Microsoft.Extensions.Caching.Memory`，默认最多 256 张或约 128 MiB，并配置滑动过期与淘汰回调；不自行实现 LRU。
- 单张图片解码像素上限默认 200 MP，超过时询问用户是否继续，防止压缩炸弹或内存耗尽。

## 7. 中间工作区

### 7.1 当前文件栏

中间区域不显示当前图片的大图预览，只在顶部保留 40 px 的当前文件信息栏：

- 左侧显示当前文件名，单行省略，ToolTip 显示完整路径。
- 右侧紧凑显示像素尺寸、格式、文件大小和识别状态。
- 文件栏不显示缩略图；图片视觉信息只存在于左侧列表的 64 × 64 缩略图中。
- 未选择图片时显示“请选择左侧图片”，下方选项卡进入统一空状态。
- 这样让标签与生成信息获得完整垂直空间，尤其适合一次展示大量标签。

### 7.2 选项卡

选项卡固定为两个：

1. **识别标签**：模型识别出的 Danbooru 标签。
2. **生成信息**：图片元数据中解析出的 AI 生成参数。

使用 ShadUI `TabControl`，控件占满当前文件栏以下的全部中间区域。标签标题右侧可显示计数，例如“识别标签 47”；生成信息成功解析时显示小圆点，不增加第三个选项卡。

## 8. “识别标签”选项卡

### 8.1 顶部摘要

摘要行显示：

- `高于阈值 47 / 当前模型全部输出`；对 WD Canary 的具体示例为 `47 / 16,473`，总数由 Model Pack 动态提供。
- 当前阈值 `60.94%`。
- 推理耗时。
- 实际推理运行时、执行提供程序与设备，例如 `Windows ML · NVIDIA GPU`、`CoreML · ANE` 或 `ORT · CPU`。
- 排序状态固定显示“置信度降序”。

提供两个轻量操作：

- “全选可见标签”。
- “清除手动选择”。

不提供搜索框。

### 8.2 分组顺序与行为

分组列表与顺序来自当前 Model Pack manifest 的 `groups` 声明。WD Canary 声明为分级 → 角色 → 通用；若其它 Model Pack 声明了艺术家、版权等更多分组，同一机制自动按声明顺序渲染。

每组使用可折叠 section，而不是相互独立的大卡片。组标题展示分组显示名、可见数量和展开按钮。某分组确实无结果时显示紧凑空状态“当前阈值下无标签”，不隐藏整个组，以便结构稳定。

规则如下：

- 每组内部必须按置信度从大到小排序。
- 置信度相同则按模型输出索引升序，保证结果稳定且可测试。
- 分级组是单选分类，只展示最高概率的一项并标记为“最可能”（平分按模型输出索引升序），不受阈值过滤；落选的低置信度分级不再展示，避免未达阈值的分级淹没结果。
- 其余分组默认只展示大于等于阈值的项。
- 阈值随模型包固定，只决定可见集合，不改变已保存的原始概率。
- 用户可取消勾选某标签，使其不进入 Prompt；重新识别后按标签名尽可能保留用户的手动排除状态。

标签选择采用“自动资格 + 手动覆盖”，而不是为当前模型的全部输出保存大量勾选状态：

- 概率达到有效阈值且所属组启用时，标签默认自动选中。
- 用户取消某项后记录 `ForceExcluded`；该项不再进入 Prompt，直到用户重新勾选。
- 用户重新勾选被排除项时清除 `ForceExcluded`，恢复自动规则。
- “清除手动选择”实际清除全部手动覆盖，使所有标签重新服从阈值与当前 Prompt 规则，不等于取消所有标签。
- 当前阈值以下的标签不展示在普通列表中，也不能靠手动状态绕过阈值。

### 8.3 动态标签块

每个分组内部采用从左到右、从上到下的流式标签布局。每个识别结果是一个独立的可勾选标签块，宽度由内容动态决定，达到可用行宽后自动换行；不使用固定列数、表格行或人为等宽布局。

```text
┌────────────────────────┐  ┌──────────────────────────────────┐
│ ✓ 1girl · 单个女孩 98.73% │  │ ✓ solo · 单人 96.41%              │
└────────────────────────┘  └──────────────────────────────────┘
┌──────────────────────────────────────────────┐
│ ✓ very_long_original_tag · 较长的中文翻译 91.08% │
└──────────────────────────────────────────────┘
```

布局规则：

- 使用可保持数据顺序的 `ItemsControl/ItemsRepeater + WrapLayout`；视觉换行不得改变置信度排序。
- 单个标签块宽度为内容测量值加左右内边距，最小约 116 px、最大 360 px，高度最小 38 px。
- 标签块之间水平和垂直间距均为 8 px，分组左右内边距 12 px。
- 原始标签、中文翻译和置信度优先在一行内展示，用低对比度中点分隔。
- 内容超过最大宽度时，原始标签保持第一行，中文翻译与置信度可进入第二行；不为了凑列宽拉伸较短标签。
- 极长文本在 360 px 内省略，完整内容放入 ToolTip；窗口缩窄时标签块仍不得超过分组可用宽度。
- 首版每组通常只展示阈值以上的有限结果，因此使用流式布局优先于复杂二维虚拟化；若性能测试在 500 个可见标签时不达标，再切换到虚拟化 WrapLayout，视觉规格保持不变。

硬性展示要求：

- **原始标签**：完整 Danbooru tag，保留下划线；可选择并复制。
- **中文翻译**：来自内置 Model Pack 的 `tags.csv`；其构建源是已校验的中文翻译数据，不得覆盖或替换原始标签。
- **置信度**：紧邻内容尾部显示百分数，保留两位小数；标签块底边可使用一条随概率变化的 2 px 低视觉权重进度线。
- **Prompt 选择状态**：整个标签块表现为可切换控件，左侧 Check 图标表示参与 Prompt；点击标签块任意空白处即可切换。

翻译策略：

- 优先按标签 ID 匹配，同时校验 tag 文本，防止两个资源版本错位。
- ID 不一致时可按精确 tag 查找。
- 找不到、为空或与原始标签完全相同时，标签块中的翻译位置显示灰色“暂无翻译”。
- UI 不自动调用在线翻译。

视觉细节：

- 原始标签为主文本，中文翻译为次文本，但两者均保持清晰可读。
- 置信度 ≥ 90% 使用强调色；70%–90% 使用正常前景色；低于 70% 使用次要前景色。颜色只作辅助，数字始终存在。
- 悬停时仅提升标签块背景和边框对比度，不改变尺寸，避免相邻标签跳动。
- 被取消选择的标签块降低到约 55% 不透明度，但仍可读、仍留在原排序位置。
- 长文本省略时，ToolTip 同时展示原始标签、翻译和未四舍五入的概率。

### 8.4 空闲、加载和错误状态

- 未识别：显示模型图标、“尚未识别此图片”和主按钮“识别当前图片”。
- 模型加载中：显示 Skeleton 行和“正在加载模型”，不能显示虚假结果。
- 推理中：保留旧结果但覆盖“正在重新识别”状态；首次识别显示 Skeleton。
- 已取消：保留上一次成功结果；没有旧结果时回到未识别状态。
- 失败：显示简短原因、可展开技术详情和“重试”按钮。
- 模型已切换：旧结果标记为过期，默认不进入 Prompt，直到重新识别。

## 9. “生成信息”选项卡

### 9.1 规范化数据模型

不同工具的元数据先解析为统一的 `GenerationInfo`：

| 字段 | 示例 |
|---|---|
| 来源工具 | AUTOMATIC1111 / Forge / ComfyUI / NovelAI / Unknown |
| 正向提示词 | prompt |
| 负向提示词 | negative prompt |
| Steps | 28 |
| Sampler / Scheduler | DPM++ 2M / Karras |
| CFG Scale | 7.0 |
| Seed | 123456789 |
| 尺寸 | 1024 × 1536 |
| Model / Model hash | checkpoint 名称与 hash |
| VAE | VAE 名称或 hash |
| Clip skip | 2 |
| Hires / Refiner | 放大器、倍数、去噪强度、refiner 参数 |
| LoRA / Embedding | 名称、hash、权重 |
| 软件版本 | 工具和版本号 |
| 原始元数据 | 未识别键值和原始文本/JSON |

所有字段允许为空。解析器保留原始值，不因规范化而丢弃未知字段。

### 9.2 解析范围

首版支持：

- PNG `tEXt/iTXt/zTXt` 中的 `parameters`、`prompt`、`workflow`、`Comment`、`Software`、`Source`。
- JPEG/TIFF EXIF `UserComment`、`ImageDescription`、XPComment，以及可用的 XMP。
- WebP 的 EXIF/XMP 文本块。
- AUTOMATIC1111 / Forge 常见 `Negative prompt:` 与尾部参数行。
- ComfyUI `prompt` 和 `workflow` JSON：读取文本编码节点、checkpoint、VAE、LoRA、KSampler、尺寸和 seed。工作流过于复杂时显示可确认的节点值，并保留原始 JSON，不猜测最终组合顺序。
- NovelAI 常见 Comment JSON 和 Software/Source 标识。

解析顺序为“提取所有候选元数据 → 按格式识别 → 规范化 → 保留原文”，不是互斥的单解析器抢占。一个文件存在多套信息时，按来源分段展示。

元数据在图片首次成为当前项时异步解析，并在本次会话内缓存；打开大量文件时不预先解析所有元数据。解析任务绑定图片 ID，切图后完成的结果只写回对应图片，不阻塞标签识别。

### 9.3 页面布局

页面从上到下展示：

1. 来源 Badge 和元数据完整度提示。
2. 正向提示词区：只读、多行、可选择，右上角复制按钮。
3. 负向提示词区：同上。
4. 参数网格：两列键值布局，窄窗口变为单列。
5. 模型资源列表：checkpoint、VAE、LoRA、embedding。
6. “原始元数据”折叠区：等宽字体、只读，可复制。

没有可识别信息时显示“未发现支持的 AI 生成信息”，并在下方列出发现但未识别的元数据键，便于排查。

### 9.4 安全限制

- JSON 最大解析深度 64，单字段最大展示 2 MiB，超限截断并提示。
- 不执行工作流中的任何路径、脚本或表达式。
- 原始元数据只作为文本显示，所有内容按纯文本处理。
- 格式损坏时返回部分结果和警告，不因一个字段失败而丢弃其余字段。

## 10. 右侧 Prompt 构建器

### 10.1 总体结构

右栏分为可滚动配置区和固定底部的 Prompt 预览区：

1. 标题“Prompt 构建器”、启用开关和“恢复默认”按钮。
2. 分组开关与顺序。
3. 标签变换规则。
4. 输出格式规则。
5. Prompt 只读预览、标签数量、复制按钮。

右栏只处理当前选中图片。切换图片时，标签来源和 Prompt 必须立即切换到新图片；不允许沿用上一张图片的输出。

### 10.2 规则配置

首版只维护一套当前规则，不实现多方案的新建、复制、重命名、导入或导出。用户可以修改规则并用“恢复默认”回到内置的 Danbooru 默认值。改动采用 300 ms 防抖和原子文件替换自动保存。

### 10.3 分组配置与排列顺序

为当前 Model Pack manifest 声明的每个分组显示一行（WD Canary 为通用、角色、分级；其它模型按其声明），
每行包含：

- 启用复选框。
- 分组显示名。
- “上移/下移”按钮（点击改变顺序，自动重新构建 Prompt 并保存）。
- 组内排序切换按钮（字形显示当前模式：↓ 置信度降序 / A↑ 标签名升序，图例见卡片底部说明行）；默认并验收使用置信度降序。

默认顺序为 manifest `groups` 数组的逆序（WD Canary：通用 → 角色 → 分级），主题标签在前、分级最后。所有组默认
启用。

### 10.4 标签规则

首版提供以下规则，均为明确控件，不使用脚本语言（阈值本身不是规则：它唯一来自当前 Model Pack，
界面不提供全局/分组阈值调节入口）：

- **下划线转空格**：默认开启，例如 `blue_hair` → `blue hair`。
- **去重**：始终开启且不可关闭；忽略大小写，保留排列中第一次出现的项。
- **排除列表**：多行文本，每行一个精确原始 tag；不把它设计为搜索框。
- **替换规则**：简单两列表格 `原始 tag -> 输出文本`，精确匹配，不支持正则表达式。
- **概率权重**：默认关闭；开启后将置信度映射为 `(tag:weight)`。

概率权重采用可预测的线性规则：

```text
weight = minWeight + (probability - effectiveThreshold)
         / (1 - effectiveThreshold) * (maxWeight - minWeight)
```

- `probability` 先限制在 `[effectiveThreshold, 1]`。
- 默认 `minWeight = 1.00`，`maxWeight = 1.30`。
- 权重保留两位小数；等于 1.00 时不包裹权重语法。
- 用户可关闭权重以得到纯 Danbooru tag Prompt。

### 10.5 输出规则

- 标签分隔符：默认 `, `，可选换行；不允许空字符串。
- 分组分隔：默认仍为 `, `，可改为换行。
- 前缀与后缀：可选多行文本，作为用户明确提供的固定内容。
- 最大标签数：默认 100，范围 1–500；按最终组顺序截断。
- 尾部分隔符：默认关闭。
- 空 Prompt 时显示占位提示，不复制空字符串。

### 10.6 确定性构建算法

Prompt 每次按以下固定流水线生成：

1. 获取当前图片的有效推理快照；快照过期或不存在则输出空状态。
2. 依据标签目录的 `group` 列确定分组。
3. 应用模型包阈值；分级组取最高概率标签，除非当前规则禁用分级。
4. 移除用户在标签页取消勾选的项。
5. 应用排除列表。
6. 按用户配置的分组顺序排列。
7. 每组按配置排序；默认置信度降序，概率相同按模型索引升序。
8. 应用精确替换和下划线转换。
9. 规范化首尾空白并去重。
10. 应用可选权重。
11. 应用最大标签数。
12. 用分隔符连接，并添加前缀与后缀。

此算法必须实现为无 UI 依赖的纯函数，以便单元测试和快照测试。

### 10.7 Prompt 预览

- 只读多行文本框，默认最小高 150 px。
- 顶部显示 `47 tags · 612 chars`。
- 提供主按钮“复制 Prompt”；成功后显示 Toast“已复制”。
- 次要菜单提供“另存为同名 `.txt`”，默认路径为图片同目录，但写入前必须显示路径并由用户确认。
- Prompt 会响应当前图片、标签勾选和规则变化（阈值只在切换模型时变化）；使用 100 ms 防抖避免频繁刷新。

## 11. 设置设计

设置使用单个 ShadUI Dialog，分为三个小节：

### 11.1 模型

- 设置页通过目录选择器配置一个外部 Model Pack；应用验证后保存目录路径，不复制、移动或删除其中的文件。
- “清除”只移除当前配置，不删除外部 Model Pack 目录。
- 每次加载都会执行文件存在性、hash、manifest、标签数和 ONNX 契约检查。
- 显示输入尺寸、标签数、文件 hash 校验结果和标签目录构建信息。

模型只来自用户在设置页显式选择的一个外部目录。应用不读取模型路径环境变量，也不扫描当前工作目录或任意用户文件夹。

绝对路径隔离规则：

- `.csproj`、源码、manifest、设计文档、示例配置、测试快照和发行脚本不得包含开发机绝对路径。
- manifest 内的 ONNX 和 catalog 路径必须是 Model Pack 根目录下的简单相对文件名。
- `settings.json` 只保存 Model Pack ID，不保存模型绝对路径。
- 测试使用仓库相对 fixture 或测试框架创建的临时目录；不得依赖某个盘符、用户名或 home 路径。
- 日志默认只记录 Model Pack ID、文件名和 hash，不记录完整绝对路径。

### 11.2 外观与行为

- 主题：跟随系统、浅色、深色。
- UI 语言：简体中文；保留未来英文资源结构，但首版只承诺中文。
- 打开文件夹时递归扫描：默认关闭。
- 启动时恢复上次窗口布局：默认开启。
- 显示中文翻译：默认开启；关闭后标签块按原始标签与置信度重新测量动态宽度。

### 11.3 性能

首版不向用户暴露 batch、线程数、预处理并行度或缓存大小等底层旋钮，避免无效组合和支持成本。这些参数全部由运行时按设备、provider、模型和内存压力自动决定。设置页只提供：

- 加速策略：自动（默认）、节能优先、仅 CPU。
- “重新检测性能”：清除当前设备的 provider/batch 调优结果并在下次识别时重新测量。
- “清除内存缓存”：清理缩略图和当前会话推理结果，不删除图片、模型或设置。

## 12. 状态栏、通知与快捷键

### 12.1 状态栏

从左到右显示：

- 全局状态：就绪、正在加载模型、正在识别、已取消、存在错误。
- 当前图片序号与总数。
- 当前图片尺寸和格式。
- 当前模型与实际执行提供程序。
- 最近一次推理耗时。

批量识别时，状态栏中间显示确定进度条和 `已完成/总数`。

### 12.2 Toast 与 Dialog 使用原则

- Toast：复制成功、导出成功、已跳过重复文件、非阻断错误。
- Dialog：安装/校验模型失败、清空有结果的会话、覆盖同名文本、超大图片确认。
- 同一批重复错误合并为一个摘要 Toast，不连续轰炸用户。
- 错误文本包含用户可理解的结论；技术异常和堆栈仅写日志或放在“详细信息”。

### 12.3 快捷键总表

| 快捷键 | 操作 |
|---|---|
| `Ctrl+O` | 打开图片 |
| `Ctrl+Shift+O` | 打开文件夹 |
| `Ctrl+Enter` | 识别当前图片 |
| `Ctrl+Shift+Enter` | 识别全部图片 |
| `Esc` | 取消当前识别/关闭最上层弹窗 |
| `↑ / ↓` | 选择上一张/下一张图片 |
| `Delete` | 从列表移除当前图片 |
| `Ctrl+Shift+P` | 展开/折叠 Prompt 构建器 |
| `Ctrl+Alt+C` | 复制当前 Prompt |
| `Ctrl+,` | 打开设置 |

快捷键在文本编辑控件获得焦点时不得抢占常规编辑行为。

## 13. MVVM 与代码架构

### 13.1 解决方案结构

为保持边界清楚且不过度拆分，使用三个生产项目、一个不随应用发布的模型包工具和一个测试项目：

```text
ImageTagger.sln
src/
  ImageTagger.Core/             纯领域模型、接口、Prompt 规则
  ImageTagger.Infrastructure/   ONNX、图片、元数据、文件与设置实现
  ImageTagger.App/              Avalonia/ShadUI、Views、ViewModels、桌面入口
tests/
  ImageTagger.Tests/            Core、Infrastructure 与 ViewModel 测试
tools/
  ImageTagger.ModelPackTool/    开发期 pack/validate 命令，不进入应用发行物
```

依赖方向固定为：

```text
App ───────────────► Core
App ─► Infrastructure ─► Core
ModelPackTool ──────► Infrastructure ─► Core
Tests ─────────────► 各目标项目
```

Core 不引用 Avalonia、ShadUI、ONNX Runtime 或文件选择器 API。

### 13.2 主要技术选择

| 能力 | 选择 | 理由 |
|---|---|---|
| 桌面框架 | Avalonia 12.1 | Windows/macOS 双桌面平台 |
| 主题与控件 | ShadUI 0.2.4 | 用户指定；与 Avalonia 12.1、net9/net10 匹配 |
| 目标框架 | .NET 10 LTS | ShadUI 0.2.4 原生目标之一；生命周期长 |
| MVVM | CommunityToolkit.Mvvm | 简洁的 ObservableProperty 与 RelayCommand |
| ONNX | Windows：Windows ML；macOS：ONNX Runtime CoreML/CPU | Windows 由系统管理 CPU/GPU/NPU EP，macOS 使用 CoreML 并保留 ORT CPU |
| 图片与像素算子 | SixLabors.ImageSharp | 解码、EXIF、色彩、alpha、pad/crop/resize 和缩略图均委托成熟库；实现前复核许可证 |
| 通用元数据 | MetadataExtractor + ImageSharp metadata API | 不自行解析 EXIF/XMP/PNG chunk/WebP 容器；应用只做 AI 字段归一化 |
| Tensor 数值操作 | System.Numerics.Tensors + CommunityToolkit.HighPerformance | 复用 Tensor/向量化/高性能内存抽象，不自制数值容器 |
| CSV | CsvHelper | 读取/生成 `tags.csv`，处理 UTF-8、引号、逗号和换行边界 |
| JSON/Schema | System.Text.Json + JsonSchema.Net | JSON 序列化与 manifest 结构校验，不自写 JSON parser/validator |
| 缓存与依赖注入 | Microsoft.Extensions.Caching.Memory + DependencyInjection | 不自写通用缓存或服务容器 |
| 哈希 | System.Security.Cryptography | 使用 .NET 标准库处理 SHA-256 |
| 日志 | Serilog + File sink | 成熟的滚动文件能力，配置简单且无需自制 Provider |
| 测试 | xUnit + Avalonia.Headless | 领域、解析和关键 UI 状态测试 |

不引入 ReactiveUI、数据库、AutoMapper、MediatR 或完整插件框架。

#### 13.2.1 Library-first 强制规则

实现优先级固定为：`.NET/Avalonia/Windows 平台标准能力 → 已采用库的现成功能 → 成熟、活跃、许可证兼容的第三方库 → 最小必要自定义逻辑`。

- 禁止自行实现图片编解码、EXIF/XMP 容器、alpha blending、通用 crop/resize 插值、CSV/JSON/ZIP、SHA-256、LRU/cache、日志滚动、MVVM 通知、ONNX 图执行或通用 Tensor 容器。
- 每个预处理原子算子只是对 ImageSharp、System.Numerics.Tensors 或经选型批准的图像库 API 的薄封装，负责参数适配和类型声明，不重写其核心算法。
- 添加新自定义算法前必须形成简短 ADR：需求、检索过的候选库、版本/维护状态、准确性/性能/许可证测试结果，以及为什么不能采用。没有 ADR 不得合并。
- 第三方依赖必须集中锁定版本，提交锁文件，启用漏洞与许可证扫描；禁止浮动版本和来源不明的小包。
- 不为了替代一两行领域胶水而机械引入包；但凡涉及格式、数值、图像、缓存、并发原语等通用能力，必须优先复用标准库或成熟第三方实现。
- 包装层保持薄：不复制第三方库源码，不建立与其一一对应的大型抽象层，只在可替换边界上定义接口。

Pillow bicubic 兼容性必须先做库选型 spike：

1. 首先用 ImageSharp 的 Bicubic 实现跑完整逐阶段 golden。
2. 若未达准确性门槛，再评估 SkiaSharp、NetVips 等成熟跨平台库的现有 resampler。
3. 选择能达到参考 Tensor/模型输出门槛且性能最好的库实现 `pillow-bicubic-v1` 算子。
4. 只有在测试报告证明没有可用库能满足 Pillow 兼容性时，才允许编写最小的坐标/采样兼容层；图片解码、像素存储和其余处理仍由第三方库完成。该例外必须单独 ADR 和 golden/performance 审核。

允许自写的必要业务逻辑仅限：

| 自定义部分 | 无法直接由通用库替代的原因 | 仍复用的库能力 |
|---|---|---|
| Model Pack 语义校验与流水线编排 | 属于本应用自定义 manifest 与 ONNX 契约 | JsonSchema.Net、ImageSharp、Tensor 库 |
| 算子类型链、shape inference 与顺序执行计划 | 顺序和模型输入契约由本项目定义 | 算法本体仍调用库，内存使用标准 Pool |
| TagCatalog 与 PromptBuilder | Danbooru 分组和用户 Prompt 规则属于产品领域 | CsvHelper、不可变集合 |
| 自适应 provider/batch 策略 | 需结合当前模型、设备、内存和实测吞吐 | Windows ML/ORT、Channels、平台指标 API |
| SD 工具生成信息归一化 | A1111/ComfyUI/NovelAI 没有统一跨平台 C# 领域库 | MetadataExtractor、ImageSharp、System.Text.Json |
| ViewModel 协调与选择状态 | 直接对应本应用交互 | CommunityToolkit.Mvvm |

除此之外的自写通用基础设施默认视为设计偏差。

### 13.3 Composition Root

对象注册只在 `App` 启动处完成，使用 `Microsoft.Extensions.DependencyInjection` 管理单例 Session、Model Registry、缓存、服务和 ViewModel 生命周期。所有业务依赖仍通过构造函数传入，不使用 Service Locator；设计时数据使用独立 Design ViewModel。

### 13.4 核心领域对象

```text
ImageDocument
  Id, CanonicalPath, FileName, FileSize, Format, PixelSize
  ThumbnailState, AnalysisState
  PredictionSnapshot?, GenerationInfo?, LastError?

ModelDescriptor
  SchemaVersion, Id, DisplayName, Task, ModelFile
  InputContract, Groups[], PreprocessingPipelineDescriptor, OutputContract
  LabelResources, DefaultThreshold

PredictionSnapshot
  ModelFingerprint, CreatedAt, Duration, Runtime, ExecutionProvider, BatchSize
  float[] Probabilities

TagCatalogEntry（每个 Model Pack 全局共享）
  Index, OriginalName, ChineseTranslation?, DisplayGroup

TagSelection
  HashSet<int> ForceExcludedIndices

PromptSettings
  GroupRules, ThresholdOverrides
  TransformRules, OutputRules

GenerationInfo
  Source, PositivePrompt, NegativePrompt
  Parameters, Resources, RawEntries, Warnings
```

领域对象尽量不可变。每张图片只保存与模型输出对齐的连续 `float[]` 概率，不复制标签文本或创建 16,473 个对象；标签名称、翻译与分组由只加载一次的 `TagCatalog` 共享。ViewModel 只为当前阈值以上的可见标签创建轻量投影。推理成功后一次性替换 `PredictionSnapshot`，避免 UI 看到半完成集合。

### 13.5 服务接口

| 接口 | 职责 |
|---|---|
| `IImageImportService` | 规范化路径、去重、读取基本文件信息 |
| `IThumbnailService` | 异步生成和缓存缩略图 |
| `IAppResourceLocator` | 按平台返回内置资源根与应用托管数据根，不暴露工作目录假设 |
| `IModelPackService` | 校验、读取和加载用户显式选择的标准 Model Pack 目录；内部职责拆类但不机械增加接口 |
| `ITaggerModelAdapter` | 组合特定模型族的预处理、输出激活、标签目录与分组策略 |
| `IPreprocessOperatorFactory` | 注册版本化原子算子、参数 schema、输入输出类型与 shape inference |
| `IPreprocessingPipelineCompiler` | 校验有序 steps 并生成缓存的顺序执行计划 |
| `IInferenceRuntimeFactory` | 按平台创建 Windows ML 或 ONNX Runtime 会话并报告实际设备 |
| `ITagInferenceService` | 管理当前模型的单一 Session 并返回不可变预测快照 |
| `IGenerationInfoParser` | 解析并规范化各工具信息 |
| `ISettingsStore` | 原子读取/保存应用设置与单套 Prompt 规则 |
| `IPlatformService` | 文件选择器、剪贴板、在文件管理器中显示 |

`TagCatalog`、`CompiledPreprocessingPipeline`、`BatchSizeOptimizer`、`GenerationMetadataReader` 和 `PromptBuilder` 使用可直接测试的具体类。接口只用于平台差异、多个真实实现或需要替换的外部边界，不为每个小类机械创建接口。

### 13.6 ViewModel 划分

```text
MainWindowViewModel
  ToolbarViewModel
  ImageListViewModel
    ImageItemViewModel[]
  WorkspaceViewModel
    CurrentFileViewModel
    TagsViewModel
      TagGroupViewModel[]
    MetadataViewModel
  PromptBuilderViewModel
  StatusBarViewModel
```

- `MainWindowViewModel` 只协调当前选择和全局命令，不直接解析图片或运行 ONNX。
- `ImageItemViewModel` 持有展示状态，不持有业务服务。
- `TagsViewModel` 从不可变快照生成排序后的只读分组视图。
- `PromptBuilderViewModel` 监听“当前图片结果、选择状态、阈值、规则”四类输入，并防抖重算。
- Code-behind 只允许纯视图行为，例如拖放事件桥接、焦点和 GridSplitter 双击；业务逻辑必须进入 ViewModel 或服务。
- 所有 XAML 启用 compiled bindings，并设置 `x:DataType`。

## 14. 推理生命周期与并发

### 14.1 模型加载

1. 通过 `IAppResourceLocator` 找到内置资源，并由 `IModelPackService` 读取标准 manifest；运行时不解析源训练目录。
2. 校验 ONNX、`tags.csv`、校验清单与许可证存在；manifest 中每个资源引用都必须是 Model Pack 根目录下的允许文件名。
3. 校验标签索引连续、标签数等于 manifest 的 `labelCount`，并与翻译资源一致；WD Canary 的期望值为 16,473，其他模型使用各自描述值。
4. 校验预处理 schema、全部算子/version/参数/类型链，编译流水线，并确认最终 dtype/layout/shape 与 ONNX 输入一致；同时校验输出名称/形状/activation 受到当前适配器支持。
5. 校验 `checksums.sha256`；首次加载或文件时间/大小变化时执行，结果缓存。
6. 由 `IInferenceRuntimeFactory` 创建一个长期存活的模型会话，并读取 ONNX 的实际元数据反向核对 manifest。
7. 使用确定性的合成 Tensor 进行 warm-up；失败时销毁 Session，尝试下一个硬件 provider，最后回退 CPU 或报告错误。

Session 创建和销毁不在 UI 线程执行。

### 14.2 执行提供程序

双平台策略是“尽量使用可用硬件，CPU 永远可回退”，并通过同一个 `IInferenceRuntimeFactory` 隔离 Windows/macOS 差异。

**Windows**

- Windows 发行版必须使用 Windows ML（`Microsoft.WindowsAppSDK.ML`），不把普通 DirectML ORT 包作为首选实现。
- Windows ML 是 Windows 维护的 ONNX Runtime，可通过 Windows 管理的 execution providers 使用 NPU、GPU 和 CPU；应用使用其官方 provider 获取/注册机制，不自行下载未知 DLL。
- 自动策略枚举 Windows ML 可用且与当前模型兼容的认证 EP。Windows 11 24H2 及以上优先考虑硬件厂商 NPU/GPU EP；未满足条件或专用 EP 不兼容时使用 Windows ML 的 GPU/DirectML，再回退 Windows ML CPU。
- 对本模型这种大型视觉 Transformer，“最快”不能简单等同“优先 NPU”。候选 provider 必须成功建 Session、warm-up，并用相同 Tensor 做短基准；按每秒图片数选择赢家。节能策略可在吞吐接近时优先 NPU。
- 自动选择最多基准测试 Windows ML 推荐顺序中的前两个兼容硬件候选，总调优预算 20 秒；超时即采用目前最快的成功候选。选择过程显示一次“正在优化本机硬件”状态且结果持久缓存。
- Windows ML 采用框架依赖或自包含部署方式在发行阶段决定；无论哪种方式，都必须在 x64 与 ARM64 真机验证。Avalonia UI 不改用 WinUI，Windows ML 仅作为 Windows 推理运行时。

**macOS**

- 分别发布 `osx-arm64` 与 `osx-x64`，不把两套原生库和两份模型合成超大的 Universal 包。两者都内置 WD Model Pack 和 CPU fallback。
- 首选 ONNX Runtime CoreML EP。CoreML 本身负责在 CPU、GPU 与 Apple Neural Engine 间调度；Apple Silicon 使用 `MLProgram` 与 `ComputeUnits=ALL` 候选，Intel Mac 只有 CPU/GPU，不显示不存在的 ANE。
- 官方资料确认 CoreML EP 可由 C# API 注册，但实际 NuGet/native 包必须在依赖 spike 中调用 provider 枚举验证。若标准 macOS 包没有 CoreML，使用 ONNX Runtime 官方源码/构建脚本生成带 `--use_coreml` 的固定版本原生包；不自行实现 CoreML bridge 或计算 kernel。
- 创建 Session 后执行完整 warm-up，并通过 ORT profiling 检查实际节点分配。若 CoreML 只接管少量节点、频繁 CoreML↔CPU 交换或实测慢于 CPU，就选择 ORT CPU，而不是仅因 provider 可创建便宣称硬件加速成功。
- 每个 Model Pack 独立探测与缓存结果；不能把另一个模型的结论复用过来。
- CoreML 首次模型编译和 provider 官方缓存放到平台 Cache 目录，以 `模型指纹 + CoreML/ORT 版本 + 芯片/OS` 隔离；不写入签名只读的 `.app` 资源，也不自行设计编译产物格式。
- Apple Silicon 使用统一内存，batch 优化器读取系统 memory pressure；达到 warning/critical 时立即降低 batch。Intel Mac 同样从 1 开始实测，不套用 Apple Silicon 参数。
- CoreML Session 创建、编译、warm-up 或真实批次失败时销毁该 Session 并回退 ORT CPU；失败缓存只在 OS、ORT 或模型变化后重试。
- `.app` 内所有 ORT/CoreML 相关 `.dylib` 随应用签名并参与 notarization；发布前分别在 Apple Silicon 与 Intel 真机进行 Gatekeeper、首次编译、离线推理和 CPU fallback 测试。

**共同规则**

- 候选短基准结果按 `模型指纹 + provider + 设备 ID + 驱动/运行时版本` 缓存；环境变化后自动失效，避免每次启动重复测试。
- provider 初始化、warm-up 或首个真实批次失败时自动降级并重试一次；OOM 先减小 batch，再考虑降级 provider。
- UI 和日志显示实际运行时、EP、设备、batch size 与是否发生节点回退，不显示用户期望但未成功的 provider。
- Session 启用 ORT 官方最高安全图优化等级；provider-specific 选项仅通过官方 API 配置并纳入运行时指纹。
- 复用官方 `OrtValue`、I/O Binding、pinned buffer 或 provider device tensor 能力，避免每批重复创建输入/输出对象；不自行实现 GPU/NPU 内存管理。
- provider 覆盖率、Host↔Device copy、预处理等待与 inference 时间通过 ORT profiling/平台工具记录，自动选择依据是端到端 images/sec，不是设备名称或单个 kernel 时间。
- provider 调优和 batch 调优共享总预算，界面首次优化等待上限仍为 20 秒。
- Windows 与 macOS 都必须有 CPU 基线，硬件加速不可用时功能仍完整。

### 14.3 单图识别流程

```text
命令校验
  → 标记当前项“识别中”
  → 后台读取与预处理
  → 后台执行 ONNX
  → sigmoid 得到全部概率
  → 与 TagCatalog 按索引合并
  → 创建不可变 PredictionSnapshot
  → UI 线程一次性提交结果
  → 按阈值生成分组
  → 重建当前 Prompt
```

若用户在推理期间切换图片，任务继续执行，但完成结果只写回对应 `ImageDocument`；不得覆盖当前图片 UI。

### 14.4 批量识别

- 使用有界 `Channel<ImageDocument>` 或同等简单队列。
- 当前 WD ONNX 已在导出时为输入 `images` 和输出 `logits` 声明动态 batch 轴，因此“识别全部”使用单 Session 的 micro-batching；“识别当前”固定 batch 1，保持最低交互延迟。
- 识别全部只加入未识别、失败、取消或模型已变更的项；已用当前模型成功识别的项不重复执行。
- 每张图独立失败，队列继续；结束后显示成功/失败/取消摘要。
- `CancellationToken` 贯穿排队、解码、预处理和结果提交。ONNX 原生调用若无法即时中止，则取消剩余队列，并丢弃当前调用完成后的结果。
- 应用关闭时若任务运行，弹窗提供“取消并退出 / 继续等待”。

自适应 batch 算法：

1. 从实际 ONNX 输入元数据与适配器取得支持的 batch 方式；静态 batch 1 的其他模型必须保持逐张处理，不能伪造动态 batch。
2. 自动模式从 batch 1 开始，候选序列固定为 `1 → 2 → 4 → 8`。
3. CPU 候选封顶 4，GPU/NPU 候选封顶 8；首版不提供手动 batch 配置。
4. 每个候选利用真实队列中的最多两个批次计算 images/sec、单批延迟和进程内存增量；这些结果正常回填，不为了调优重复识别图片。平台能报告显存/共享内存时同时读取设备内存余量。
5. 只有相对当前 batch 吞吐提升至少 8%、预计可用内存仍保留至少 25%，且单批预计耗时不超过 10 秒，才接受更大 batch 并继续试探。
6. OOM、provider 错误、内存余量低于 15% 或连续两个批次延迟恶化时立即退回上一个安全 batch；清理失败的 Tensor，必要时重建 Session，并只重试该批一次。
7. 结果按每张图片的稳定 ID 回填，与批内位置一一对应；最后不足一个 batch 的图片直接用实际数量执行，不补无意义的假图片。
8. 队列不足 `2 × 候选 batch` 时不升级到该候选，短任务直接使用较小批量。整次 batch 调优额外等待预算不超过 5 秒。
9. 选出的 batch 参数按模型/设备缓存。后续运行持续记录滑动平均吞吐；明显退化或系统内存压力升高时只向下调整，不在任务中反复向上试探造成抖动。

预处理生产者与推理消费者分离：

- 预处理并行度按内部公式自适应，但始终只有一个推理 Session 消费批次，避免复制约 818 MiB 模型权重。
- 预处理完成的 Tensor 放入有界队列，容量为 `2 × 当前 batch`，形成少量流水线并限制内存。
- Tensor 使用池化缓冲区，结果提交后立即归还；取消或异常路径也必须归还。
- 单图预处理失败只移除该图，其余已完成 Tensor 可以组成较小批次继续执行。

### 14.5 模型指纹与过期结果

模型指纹由以下内容构成：

- ONNX 文件名、长度、最后修改时间和 SHA-256。
- 标准 `tags.csv`、`model.json` 与许可证的 hash。
- 完整预处理指纹，包括规范化后的有序 steps、全部算子版本/参数、解码库和编译优化版本。

模型或预处理变化时，现有结果标记为过期而不是静默复用。

## 15. 数据持久化

首版只持久化应用设置和单套 Prompt 规则，不持久化图片列表或预测结果，避免路径隐私、陈旧缓存和数据库迁移复杂度。

目录遵循平台规范：

- Windows：通过 `Environment.SpecialFolder.LocalApplicationData` 定位应用数据目录。
- macOS：通过平台应用数据 API 定位 `Application Support`，通过平台缓存 API 定位 `Caches`。
上述路径全部由平台 API 解析，应用不要求用户设置任何环境变量，也不把解析后的绝对路径写入项目文件。

文件：

```text
settings.json
prompt-settings.json
logs/image-tagger-yyyyMMdd.log
```

保存采用“写临时文件 → flush → 原子替换”。读取失败时保留损坏文件副本、恢复默认设置并提示用户，不覆盖原损坏内容。

## 16. ShadUI 使用规范

### 16.1 初始化

`App.axaml` 注册：

```xml
<Application.Styles>
    <shadui:ShadTheme />
</Application.Styles>
```

主窗体继承 `ShadUI.Controls.Window`。使用其 `DialogHost`、Toast host、主题资源和窗口状态能力。

### 16.2 控件映射

| 场景 | 控件 |
|---|---|
| 主窗口 | `shadui:Window` |
| 中间切换 | ShadUI `TabControl` / `TabItem` |
| 标签分组 | 紧凑 Header + Avalonia ItemsControl；不滥用 Card |
| 标签类型与来源 | `shadui:Badge` |
| 阈值 | 无调节控件；唯一来源为 Model Pack `DefaultThreshold`，标签摘要中以百分数展示 |
| 开关 | ShadUI `Switch` |
| 配置选择 | ShadUI `ComboBox` |
| 确认与设置 | `DialogHost` |
| 非阻断通知 | Toast host |
| 加载 | `Skeleton` / `Loading` |
| 图标按钮说明 | `ToolTip` |

### 16.3 版本约束

- 固定 `ShadUI` 版本为 `0.2.4`，不使用浮动版本。
- 固定 Avalonia `12.1.x` 的同一补丁版本，避免 ShadUI 资源与 Avalonia 控件版本错配。
- ShadUI 0.2.4 的 NuGet 目标为 net9.0/net10.0，并依赖 Avalonia 12.1.0，因此应用选择 net10.0。
- 若升级 ShadUI，必须先运行主题资源、窗口、Tab、Dialog、Toast、Slider、DataGrid/ItemsControl 的视觉回归检查。

## 17. 可访问性与本地化

- 所有图标按钮都有可读 ToolTip 和 AutomationProperties.Name。
- 键盘可到达工具栏、图片列表、选项卡、标签勾选、规则排序和复制按钮。
- 焦点边框明显，不仅用颜色表达状态。
- 文本与背景遵守 WCAG AA 对比度目标。
- 状态色同时配图标或文字，例如红色圆点旁仍显示“失败”。
- 进度条提供可读的百分比与任务计数。
- 中文 UI 字符串集中到资源文件，不散落硬编码在 ViewModel。
- 标签原始英文永远保留，中文翻译只作并列辅助信息。

## 18. 错误处理与日志

### 18.1 用户级错误

| 场景 | UI 行为 |
|---|---|
| 内置模型损坏或导入的 Model Pack 无效 | 禁止加载并列出失败项；内置模型引导重装，自定义包保留原文件 |
| ONNX 与标签数不一致 | 禁止识别，明确显示两个数量 |
| 图片格式不支持/损坏 | 文件项标红，可从列表移除或重试 |
| 内存不足 | 停止当前任务、释放缓存、降低 batch 并建议关闭其他高内存程序 |
| provider 不可用 | 自动回退 CPU，并显示一次 Toast |
| 元数据损坏 | 展示已解析字段和警告，不影响标签识别 |
| 写入 `.txt` 失败 | 保留 Prompt，显示路径和系统错误摘要 |

### 18.2 日志要求

- 记录应用版本、OS、架构、模型指纹、provider、加载与推理耗时、异常类型。
- 默认不记录图片像素、完整 Prompt、完整元数据或用户目录内容。
- 路径在普通日志中只记录文件名；详细诊断由用户主动开启。
- 日志每日滚动，默认保留 7 天，总量设上限。

## 19. 性能目标

以下是工程目标，不伪造模型硬件性能保证：

- 应用空载启动至窗口可交互：常见桌面设备上目标 < 2 秒；模型异步加载不阻塞首屏。
- 切换已有缩略图的图片：UI 状态切换 < 100 ms。
- 阈值变化到标签和 Prompt 更新：< 150 ms。
- 1,000 个文件的列表滚动保持流畅，不一次解码全部缩略图。
- 模型只创建一个 Session，不因切图重复加载 600–800 MiB ONNX。
- 推理期间 UI 线程不执行图片缩放、Tensor 构造、ONNX 或大型 JSON 解析。
- 预处理流水线只在模型加载时编译一次；同一指纹后续直接复用执行计划，单图热路径不得解析 manifest 或查找算子。
- 最终 Tensor 直接写入 batch slice，不再创建一份完整的单图 Tensor后复制到 batch。
- 批量任务的预处理队列应使推理设备保持供给；基准中若 accelerator 等待输入超过总任务时间的 10%，优先调节预处理并行度和队列深度。
- 性能基准分别记录 decode、geometry、tensor transform、batch compose、host-to-device 和 inference，不能只报告总时间而无法定位瓶颈。
- 针对固定 fixture 建立性能基线；依赖或实现升级后吞吐回退超过 10% 或分配量明显增长时阻止合并。
- 所有池化缓冲区有硬上限；高性能不能以无界缓存、多 Session 或牺牲取消/错误释放为代价。
- 实际推理时间按状态栏如实显示，不设脱离硬件的硬性秒数。

## 20. 测试策略

### 20.1 单元测试

- 预处理：8 种 EXIF 方向、各类 alpha、奇数差值补白位置、Pillow-compatible bicubic、BGR、float32 归一化、连续 NCHW 和各中间阶段 golden。
- sigmoid：极大正负 logits 不溢出。
- TagCatalog：ID/tag 双重校验、翻译回退、category 映射和通用回退。
- Model Pack：schema/path 安全、未知算子、非法顺序/参数、类型链和 shape 不匹配、动态标签数，以及两个不同 mock 流水线，证明通用层没有绑定 WD 常量。
- 流水线编译器：每个算子的 schema/shape inference、线性顺序、执行计划缓存和池化缓冲区异常归还。
- BatchSizeOptimizer：静态 batch、动态 batch、吞吐提升不足、内存压力、OOM 回退、尾批和缓存失效。
- 分组排序：置信度严格降序，平分按索引升序。
- 阈值边界：`probability == threshold` 必须包含。
- PromptBuilder：组顺序、排除、替换、去重、权重、截断、前后缀和稳定输出。
- A1111/Forge、ComfyUI、NovelAI 的正常、部分和损坏元数据样例。
- 设置损坏恢复与原子保存。
- 模型指纹变化导致结果过期。

### 20.2 集成测试

- 用小型固定 ONNX fixture 验证 Session 调用与节点契约，不把 800 MiB 模型加入普通 CI。
- 真实模型测试从仓库相对的内置 Model Pack 资源运行；普通轻量 CI 可排除该测试分类，但正式发行流水线必须拉取 Git LFS 并通过全部真实模型测试。
- 从参考 Python 预处理导出完整版本化 fixture 集，逐阶段比较像素/Tensor，并比较真实模型概率、Top-N 与阈值集合。
- Windows 真机验证 Windows ML 至少发现并运行一个硬件 EP；无兼容硬件时验证 Windows ML CPU 回退。
- macOS 分别在 Apple Silicon 与 Intel 真机验证 CoreML provider 枚举、首次编译、节点覆盖、动态 batch、缓存失效、离线运行和 CPU 回退。
- 用动态 batch 模型验证 batch 1/2/4 的逐图片输出与分别单图推理在容差内一致，且回填顺序不乱。
- 图片导入、去重、取消批量任务、切图时结果归属正确。
- CI 静态扫描仓库文本，拒绝盘符路径、用户 home 目录和其他开发机绝对路径。

### 20.3 UI 与视觉测试

- Avalonia.Headless 检查命令可用状态、空状态、错误状态和切图绑定。
- 对 `1440×900`、`1120×720`、浅色、深色各生成截图基线。
- 人工检查 100%、125%、150%、200% 缩放。
- 检查超长文件名、超长标签、无翻译、500 个可见标签、空元数据和损坏图片。
- 在中栏不同宽度下验证标签块按内容动态宽度流式换行，没有固定列、遮挡、重叠或排序改变。
- Windows 与 macOS 均至少完成一次安装、打开文件、推理、复制 Prompt 的端到端冒烟测试。

## 21. 验收标准

### 21.1 功能验收

- 能打开多张图片和文件夹，列表显示缩略图、文件名、像素尺寸、文件大小与格式。
- 能识别当前图片和全部图片，任务可取消，单图失败不终止整批。
- 正式发行物无需环境变量或目录设置即可加载内置 WD Canary quality ONNX。
- 可以通过设置页选择其它 Model Pack 目录；其 manifest 可动态排列标准预处理算子并配置参数，无需修改 UI 或通用推理层。
- 阈值取自当前 Model Pack（如 WD Canary 为 60.94%），切换模型自动同步，无需重推理即可刷新标签和 Prompt。
- 标签按 Model Pack manifest 声明的分组与顺序展示（WD Canary：分级、角色、通用）。
- 每个标签同时显示原始标签、中文翻译和两位小数置信度。
- 每个分组内标签严格按置信度降序；平分结果稳定。
- 每个标签作为内容宽度自适应的独立标签块在组内流式换行，不存在固定列数。
- 能解析并展示 A1111/Forge、ComfyUI、NovelAI 的核心生成信息及原始回退内容。
- 右侧可以启停分组、调整排列顺序、配置规则并生成 Prompt。
- 切换图片后，标签、元数据和 Prompt 同步更新且不串图。
- 能复制 Prompt，并可显式导出同名文本。
- 全程无搜索框。
- 中间区域没有当前图片大图预览，只有当前文件信息栏、识别标签和生成信息。
- Windows 实际通过 Windows ML 运行，并在硬件允许时采用经验证更快的 NPU/GPU EP。
- macOS Apple Silicon/Intel 发行物都能自动探测 CoreML；只有实际节点覆盖和端到端基准有收益时启用，否则可靠回退 ORT CPU。
- “识别全部”对当前动态 batch 模型启用自适应 micro-batching，并能在收益不足或内存压力下自动选择/回退到合理批量。

### 21.2 数据正确性验收

- C# 预处理与 Python 参考 fixture 在约定浮点容差内一致。
- 更换预处理 steps 的顺序或参数会产生对应的不同 Tensor；非法顺序、未知算子和最终输入契约不匹配的 Model Pack 必须被拒绝。
- WD 顺序执行计划的最终 Tensor 与 Python 参考结果一致，端到端标签结果一致。
- 输出数量必须与当前 Model Pack manifest 的 `labelCount` 严格一致；WD Canary 为 16,473，不一致时拒绝推理结果。
- 中文翻译按 ID/tag 校验，不错位。
- 分组必须来自标签目录的 `group` 列；不能靠名称猜测。
- quality 模型固定测试图片的 Top-N 标签与 Python 参考结果一致。
- 通用图片/数值/格式能力均可追溯到标准库或选定第三方库；任何自定义算法文件必须关联已批准的 Library-first ADR，否则不通过验收。

### 21.3 体验验收

- 1440×900 下三栏信息完整，没有水平滚动条。
- 1120×720 下仍可通过折叠右栏完成所有标签和元数据操作。
- 浅色与深色模式均有清晰层级、足够对比度和统一圆角/间距。
- 推理和大文件解析期间窗口可拖动、切换图片、取消任务，不出现 UI 假死。
- 空状态、加载、取消、失败、过期和成功状态均有明确反馈。
- 批量识别中 UI 保持响应，预处理流水线执行计划复用、内存有界，硬件 provider 不因输入供给不足长期空闲。

## 22. 实施顺序

具体执行顺序、并行工作流、依赖和逐项验收统一维护在仓库根目录的 `TASKS.md`。设计文档不再重复维护任务列表，避免两份计划漂移。执行原则是先完成公共契约和阻断性技术验证，再并行开发独立功能，最后统一集成、性能验证和双平台发行；每个阶段都必须保持可构建、可测试。

## 23. 开发前必须解决的风险

| 风险 | 影响 | 处理 |
|---|---|---|
| 模型包缺少 artist/copyright 类输出 | 对应分组无内容 | 按标签目录分组列展示，无结果的分组显示紧凑空状态；不猜测、不引入外部 taxonomy |
| 内置 ONNX 约 818 MiB | 安装包、仓库与启动校验压力 | 首版只包含 quality INT8、Git LFS、流式 hash、内容文件部署、异步单 Session；不包含 balanced/FP32/训练资产 |
| 动态预处理步骤配置错误 | 输出看似正常但标签失真 | 版本化算子、封闭参数 schema、类型/shape 契约、完整指纹、逐阶段 golden；不提供静默回退 |
| 动态流水线产生过多中间内存 | 批量吞吐低或 OOM | 顺序计划只编译一次、直接写 batch slice、池化缓冲区、有界并行和性能回归门槛 |
| Windows/macOS 原生 provider 打包不同 | 双平台启动风险 | CPU 为强制基线，运行时依赖按 RID 独立验证和发行 |
| CoreML 对 QDQ/动态 batch 节点覆盖不足 | macOS 硬件加速反而更慢 | 对内置模型 profile 和短基准，只在端到端有收益时启用，否则 ORT CPU |
| Windows ML 与 macOS ORT 的原生依赖冲突 | 对应平台无法启动 | 用 `IInferenceRuntimeFactory` 和 RID 条件依赖隔离，Windows ML 只进入 Windows 发行物 |
| 自动 batch 过大 | OOM、延迟过高或吞吐下降 | 小批起步、收益/内存门槛、硬上限、OOM 降级和按设备缓存 |
| ComfyUI 工作流结构任意 | 无法总能还原最终 Prompt | 只输出可证实节点值，保留原始 JSON，不猜测 |
| ImageSharp 许可证适用性 | 发行合规风险 | 开工前按项目授权方式复核；不合适时替换图片层而不影响 Core |
| ShadUI 仍快速迭代 | 控件 API/主题资源可能变化 | 固定 0.2.4，升级走视觉与行为回归 |

## 24. 设计决策摘要

- 采用单主窗口、三栏、可调整分隔条的桌面工作台。
- 中间区域不显示大图预览，只保留当前文件信息栏和占满剩余空间的两个选项卡。
- 标签以内容宽度自适应的独立标签块在组内流式排列，完整展示原始英文、中文翻译和置信度；数据顺序默认且验收要求为置信度降序。
- 阈值只重筛保存的全部概率，不重复运行模型。
- Prompt 构建采用纯函数、确定顺序和简单配置控件，不引入脚本系统。
- 正式发行物内置标准化 WD Model Pack，安装后无需任何模型路径或环境变量配置；仓库和源码不保存本机绝对路径。
- Model Pack 用有序、版本化、类型安全的算子和参数动态组装预处理；执行计划加载时校验、编译并缓存，不引入首版不需要的通用优化器。
- CPU 是所有桌面平台的可靠回退，硬件 provider 只有通过兼容性与短基准验证后才启用。
- Windows 使用 Windows ML 自动利用可用 NPU/GPU/CPU；macOS 使用经过探测和基准验证的 CoreML/ORT CPU。
- “识别全部”在模型支持时使用有上限、有收益门槛、可在内存压力下回退的自适应 micro-batching。
- 不使用数据库；首版仅持久化应用设置和单套 Prompt 规则。
- 分组完全由模型包标签目录决定，未知 category 回落通用，绝不猜测；应用不假设标签体系，任何多标签输出均可支持。

## 25. 参考依据

- 本地模型 `README.md`、`config.json`、`export.json`、`selected_tags.csv`、`translated_tags_zh.jsonl`、`scripts/model_utils.py`、`scripts/infer_onnx.py` 与验证报告；核对日期 2026-09-01。
- ShadUI GitHub 仓库 README 与主分支示例；核对日期 2026-09-01：<https://github.com/accntech/shad-ui>
- ShadUI NuGet 0.2.4 nuspec；确认目标框架 net9.0/net10.0，依赖 Avalonia 12.1.0：<https://www.nuget.org/packages/ShadUI/0.2.4>
- Microsoft Windows ML 官方概览；确认 Windows ML 由 ONNX Runtime 驱动，可使用 Windows 管理的 NPU/GPU/CPU execution providers：<https://learn.microsoft.com/windows/ai/new-windows-ml/overview>
- ONNX Runtime CoreML EP；确认 macOS 可使用 CoreML 的 CPU/GPU/Neural Engine，C# API 可注册该 provider：<https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html>
