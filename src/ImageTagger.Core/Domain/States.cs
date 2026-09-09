namespace ImageTagger.Core.Domain;

/// <summary>Thumbnail pipeline state for one image in the session list.</summary>
public enum ThumbnailState
{
    NotStarted,
    Queued,
    Loaded,
    Failed,
}

/// <summary>Per-image inference state; <see cref="Stale"/> marks results from a previous model fingerprint.</summary>
public enum AnalysisState
{
    NotRun,
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
    Stale,
}

/// <summary>Global application state shown in the status bar (design 12.1).</summary>
public enum AppState
{
    Ready,
    LoadingModel,
    Inferring,
    Canceled,
    Error,
}
