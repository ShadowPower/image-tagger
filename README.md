# Image Tagger

Image Tagger 是一款本地图片标签工具。它使用用户指定的 ONNX 模型包完成图片识别，并提供生成信息读取和 Prompt 构建功能。

图片、标签和 Prompt 均在本机处理，软件不包含模型，也不会上传用户数据。

## 功能

- 批量导入图片或文件夹，支持拖放与递归扫描
- 使用 ONNX 模型识别图片标签
- 按模型声明的分组和策略展示结果
- 显示标签翻译与置信度，支持手动排除标签
- 读取 AUTOMATIC1111、Forge、ComfyUI 和 NovelAI 生成信息
- 按规则生成、复制和导出 Prompt
- 提供浅色、深色和跟随系统主题
- 提供 GUI 和命令行两种使用方式

## 模型包

软件不附带模型。首次使用时，请在“设置 → 模型”中选择一个模型包目录。

模型包目录结构：

```text
my-model/
├─ model.json
├─ model.onnx
├─ tags.csv
├─ checksums.sha256
└─ LICENSE.txt
```

目录名必须与 `model.json` 中的 `id` 一致。软件会校验清单、文件哈希、标签数量、输入输出契约和预处理流水线。重新选择目录会替换当前配置，外部模型文件不会被复制或修改。

模型可以在 `model.json` 中声明：

- 输入尺寸、布局和数据类型
- 预处理步骤及参数
- 输出激活方式和标签数量
- 标签分组、显示名称和分组策略
- 默认识别阈值

预处理由统一的声明式流水线执行，不包含针对特定模型的专用路径。

## GUI 使用

1. 打开“设置 → 模型”，选择模型包目录。
2. 打开图片、文件夹，或将内容拖入窗口。
3. 使用“识别当前”或“识别全部”。
4. 在中间区域查看标签和图片生成信息。
5. 在右侧调整规则并复制或导出 Prompt。

设置会立即保存，下次启动时继续使用。未配置模型时，主窗口会显示“未配置模型，请在设置中配置”。

## CLI 使用

```powershell
dotnet run --project src/ImageTagger.Cli -- tag <图片或目录> `
  --model-pack <模型包目录> [--recursive] [--threshold <0..1>]
```

输出 JSON Lines：

```powershell
dotnet run --project src/ImageTagger.Cli -- tag <图片或目录> `
  --model-pack <模型包目录> --jsonl [--output <文件>]
```

未提供 `--model-pack` 时，CLI 会尝试使用 GUI 保存的模型目录配置。

## 支持格式

图片格式：JPEG、PNG、WebP、BMP、GIF、TIFF。

当前目标平台：

- Windows x64 / ARM64
- macOS x64 / arm64

## 本地数据

Windows：

```text
%LOCALAPPDATA%\ImageTagger\
```

macOS：

```text
~/Library/Application Support/ImageTagger/
```

其中包含设置、Prompt 规则、缓存和日志。设置页提供“清理本地数据”操作；该操作不会删除用户图片或外部模型目录。

## 开发

需要 .NET 10 SDK。

```powershell
dotnet restore ImageTagger.slnx
dotnet build ImageTagger.slnx -c Release
dotnet test ImageTagger.slnx -c Release
```

依赖版本集中在 `Directory.Packages.props`，NuGet 锁文件需要随代码提交。

GUI 截图测试默认跳过，需要时可显式启用：

```powershell
$env:IMAGETAGGER_RENDER_UI = "1"
$env:IMAGETAGGER_SCREENSHOT_WINDOW = "main"
$env:IMAGETAGGER_SCREENSHOT_STATE = "recognized"
$env:IMAGETAGGER_SCREENSHOT_WIDTH = "1080"
$env:IMAGETAGGER_SCREENSHOT_HEIGHT = "680"

dotnet test ImageTagger.slnx --filter FullyQualifiedName~UiScreenshotTests
```
