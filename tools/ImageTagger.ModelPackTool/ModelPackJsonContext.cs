using System.Text.Json.Serialization;

namespace ImageTagger.ModelPackTool;

[JsonSerializable(typeof(ModelManifest))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
internal sealed partial class ModelPackJsonContext : JsonSerializerContext;
