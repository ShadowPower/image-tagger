using System.Text.Json.Serialization;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Infrastructure.ModelPacks;
using ImageTagger.Infrastructure.Settings;

namespace ImageTagger.Infrastructure;

[JsonSerializable(typeof(ModelPackReader.ManifestDto))]
[JsonSerializable(typeof(PreprocessingPipelineDescriptor))]
[JsonSerializable(typeof(SettingsStore.SettingsEnvelope))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PromptSettingsStore.PromptSettingsEnvelope))]
[JsonSerializable(typeof(PromptSettings))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
internal sealed partial class ImageTaggerJsonContext : JsonSerializerContext;
