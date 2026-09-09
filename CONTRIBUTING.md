# Image Tagger 贡献与仓库规则

> 范围基线：`DESIGN.md`（2026-09-01 实现前设计基线，含 2026-09-01 分类数据策略范围变更）与 `TASKS.md`。
> 本文件是 P-01 冻结的仓库约束，所有提交必须遵守。

## 首版范围（冻结）

- 平台仅限 Windows 与 macOS 桌面。**明确排除 Linux**：不创建 Linux 条件代码、依赖、运行时、安装包、provider 适配、测试矩阵或兼容性承诺。
- 只内置 `wd-eva02-tagger-2026-canary` quality INT8 ONNX 一个模型。
- Prompt 规则只有一套（可恢复默认），无多方案管理。
- 不提供搜索框；中间区域无当前图片大图预览。
- **明确排除**：数据库、插件程序集/动态程序集执行、云服务、账号、同步、遥测、预测结果持久化、自动标签补全、在线 Danbooru 查询、复杂节点图编辑器、Linux 支持。
- 分类数据完全来自模型包自带标签目录（源 category 数字映射），不引入外部 taxonomy；不按标签名称猜测分组。
- batch、线程、预处理并行度、缓存大小全部自动管理，不向用户暴露底层性能旋钮。

## 依赖规则

- 所有 NuGet 包版本集中在 `Directory.Packages.props`（Central Package Management），**禁止浮动版本**（不允许 `*` 或未固定的范围），提交 `packages.lock.json`。
- 新增依赖必须先复核许可证、维护状态、原生架构与再分发要求，并记录到 `docs/adr/`。
- Library-first：图像编解码、EXIF/XMP、alpha blending、crop/resize、CSV/JSON/ZIP、SHA-256、LRU、日志滚动、MVVM、ONNX 执行等通用能力必须用标准库或已批准的第三方库，禁止自行实现。新增自定义算法前必须先写 ADR，否则不得合并。

## 路径隔离（强制）

- `.csproj`、源码、manifest、设计文档、示例配置、测试快照和发行脚本**不得包含开发机绝对路径**（盘符路径如 `X:\...`、`/Users/<name>`、`C:\Users\<name>`、本机用户名等）。
- manifest 内路径必须是 Model Pack 根目录下的简单相对文件名；禁止 `..`、绝对路径、符号链接和跨包引用。
- `settings.json` 只保存 Model Pack ID，不保存模型绝对路径。
- 测试使用仓库相对 fixture 或测试框架临时目录，不依赖盘符、用户名或 home 路径。
- 日志默认只记录 Model Pack ID、文件名和 hash，不记录完整绝对路径。

## 文件与提交约定

- 所有文本文件使用 UTF-8（无 BOM），换行 LF（`.gitattributes` 强制 `* text=auto eol=lf`，`.bat` 除外）。
- C# 命名遵循 .NET 官方约定；XAML 全部启用 compiled bindings 并设置 `x:DataType`。
- 提交信息：祈使句、一行主题 ≤ 72 字符；格式 `<area>: <summary>`，area 取 `core`、`infra`、`app`、`tests`、`tools`、`docs`、`build` 之一。
- 分支：`main` 保持可构建、测试通过；功能分支 `feat/<task-id>-<slug>`，修复分支 `fix/<slug>`。合并采用 squash 或 fast-forward，禁止在 `main` 上直接提交未验证代码。
- 每次合并必须保持解决方案可构建、现有测试通过；不提交无法运行的占位实现。

## Git LFS

- 大于 10 MiB 的二进制资产必须走 Git LFS（见 `.gitattributes`）。
- 内置 Model Pack 的 `model.onnx` 必须是真实文件而非 LFS pointer；CI 校验 LFS 已拉取并对照 `checksums.sha256`。

## 设计变更流程

- 需要改变设计范围时：先更新 `DESIGN.md`，再同步 `TASKS.md` 及覆盖矩阵，两者不得漂移。
- 每完成一个 TASKS.md 主任务，立即勾选该任务及其全部验收子项。
