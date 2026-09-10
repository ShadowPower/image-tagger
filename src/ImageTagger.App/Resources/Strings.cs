namespace ImageTagger.App.Resources;

/// <summary>
/// 集中管理的简体中文界面字符串（DESIGN 17）。
/// 与 <c>Resources/Strings.zh-CN.resx</c> 保持同键同值；
/// ViewModel 与服务只引用此处常量，不散落硬编码中文。
/// </summary>
public static class Strings
{
    public const string AppTitle = "Image Tagger";

    public const string Status_Ready = "就绪";
    public const string Status_Importing = "正在读取图片信息…";
    public const string Status_Recognizing = "正在识别…";
    public const string Status_RecognizeDone = "识别完成";
    public const string Status_RecognizeCanceled = "识别已取消";

    public const string Toast_CopySuccess = "已复制";
    public const string Toast_ExportSuccess = "导出成功";
    public const string Toast_SkippedDuplicates = "已跳过重复文件";
    public const string Toast_NonBlockingError = "操作未完成，已记录详细信息";
    public const string Toast_BatchErrorTitle = "批量任务摘要";

    public const string Dialog_ClearSessionTitle = "清空图片会话";
    public const string Dialog_ClearSessionMessage = "将清空当前图片列表与识别结果，该操作不删除磁盘文件。正在运行的任务将被取消。是否继续？";
    public const string Dialog_OverwriteTitle = "覆盖同名文本";
    public const string Dialog_OverwriteMessage = "目标文本文件已存在，是否覆盖？";
    public const string Dialog_LargeImageTitle = "超大图片确认";
    public const string Dialog_LargeImageMessage = "该图片像素数超过上限，继续处理可能占用大量内存。是否继续？";
    public const string Dialog_Confirm = "确认";
    public const string Dialog_Cancel = "取消";

    public const string Settings_Title = "设置";
    public const string Settings_ModelSection = "模型";
    public const string Settings_AppearanceSection = "外观与行为";
    public const string Settings_PerformanceSection = "性能";
    public const string Settings_Recalibrate = "重新检测性能";
    public const string Settings_Recalibrated = "已清除性能调优结果，下次识别时重新测量";
    public const string Settings_ClearCache = "清除内存缓存";
    public const string Settings_CacheCleared = "已清除缩略图与会话推理缓存";
    public const string Settings_NextTaskEffective = "加速策略将在下次识别任务生效";
    public const string Settings_ImmediateEffective = "设置已即时生效";
    public const string Settings_ThemeFollowSystem = "跟随系统";
    public const string Settings_ThemeLight = "浅色";
    public const string Settings_ThemeDark = "深色";
    public const string Settings_AccelerationAuto = "自动";
    public const string Settings_AccelerationPowerSaver = "节能优先";
    public const string Settings_AccelerationCpuOnly = "仅 CPU";

    public const string Error_ModelPackInvalid = "模型包无效，无法加载";
    public const string Error_ModelPackCorrupt = "模型文件已损坏，建议重新安装应用";
    public const string Error_ModelIncompatible = "模型与当前应用不兼容";
    public const string Error_ImageUnsupported = "图片格式不受支持";
    public const string Error_ImageCorrupt = "图片已损坏，无法读取";
    public const string Error_InferenceFailed = "识别失败，请重试";
    public const string Error_OutOfMemory = "内存不足，已停止当前任务";
    public const string Error_ProviderUnavailable = "硬件加速不可用，已回退到 CPU";
    public const string Error_SettingsCorrupt = "设置已损坏，已恢复默认";
    public const string Error_IoError = "文件读写失败";
    public const string Error_Canceled = "操作已取消";
    public const string Error_Unknown = "发生未知错误";

    public const string Shortcut_OpenImages = "打开图片";
    public const string Shortcut_OpenFolder = "打开文件夹";
    public const string Shortcut_RecognizeCurrent = "识别当前图片";
    public const string Shortcut_RecognizeAll = "识别全部图片";
    public const string Shortcut_Cancel = "取消当前识别";
    public const string Shortcut_SelectPrevious = "选择上一张图片";
    public const string Shortcut_SelectNext = "选择下一张图片";
    public const string Shortcut_RemoveCurrent = "从列表移除当前图片";
    public const string Shortcut_ToggleRightPane = "展开或折叠 Prompt 构建器";
    public const string Shortcut_CopyPrompt = "复制当前 Prompt";
    public const string Shortcut_OpenSettings = "打开设置";

    public const string Button_OpenImages_ToolTip = "打开一张或多张图片 (Ctrl+O)";
    public const string Button_OpenImages_Name = "打开图片";
    public const string Button_OpenFolder_ToolTip = "打开图片文件夹 (Ctrl+Shift+O)";
    public const string Button_OpenFolder_Name = "打开文件夹";
    public const string Button_RecognizeCurrent_ToolTip = "识别当前图片 (Ctrl+Enter)";
    public const string Button_RecognizeCurrent_Name = "识别当前";
    public const string Button_RecognizeAll_ToolTip = "识别所有待处理图片 (Ctrl+Shift+Enter)";
    public const string Button_RecognizeAll_Name = "识别全部";
    public const string Button_Cancel_ToolTip = "取消当前任务 (Esc)";
    public const string Button_Cancel_Name = "取消";
    public const string Button_ToggleRightPane_ToolTip = "展开或折叠 Prompt 构建器 (Ctrl+Shift+P)";
    public const string Button_ToggleRightPane_Name = "Prompt 栏";
    public const string Button_CopyPrompt_ToolTip = "复制当前 Prompt (Ctrl+Alt+C)";
    public const string Button_CopyPrompt_Name = "复制 Prompt";
    public const string Button_OpenSettings_ToolTip = "打开设置 (Ctrl+,)";
    public const string Button_OpenSettings_Name = "设置";
    public const string Button_RemoveCurrent_ToolTip = "从列表移除当前图片 (Delete)";
    public const string Button_RemoveCurrent_Name = "移除图片";
}
