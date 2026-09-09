using System.Globalization;
using System.Text;

namespace ImageTagger.Tests.TestData;

public sealed record NpyArray(Array Data, string Dtype, int[] Shape)
{
    public int Length => Data.Length;

    public byte[] ToBytes()
    {
        var result = new byte[Data.Length];
        for (int i = 0; i < Data.Length; i++)
            result[i] = Convert.ToByte(Data.GetValue(i)!);
        return result;
    }

    public float[] ToFloats()
    {
        var result = new float[Data.Length];
        for (int i = 0; i < Data.Length; i++)
            result[i] = Convert.ToSingle(Data.GetValue(i)!);
        return result;
    }
}

/// <summary>Minimal .npy (numpy array file) reader for golden baselines; C-order only.</summary>
public static class NpyFile
{
    public static NpyArray Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(6);
        if (magic[0] != 0x93 || Encoding.ASCII.GetString(magic, 1, 5) != "NUMPY")
            throw new InvalidDataException($"{path}: not an npy file");
        int major = reader.ReadByte();
        reader.ReadByte(); // minor
        int headerLength = major >= 2 ? reader.ReadInt32() : reader.ReadUInt16();
        var header = Encoding.ASCII.GetString(reader.ReadBytes(headerLength));

        var dtype = Extract(header, "'descr':");
        if (Extract(header, "'fortran_order':") != "False")
            throw new InvalidDataException($"{path}: fortran order not supported");
        var shapeMatch = System.Text.RegularExpressions.Regex.Match(header, @"'shape':\s*\(([^)]*)\)");
        if (!shapeMatch.Success)
            throw new InvalidDataException($"{path}: header missing shape: {header}");
        var shape = shapeMatch.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

        int elementSize = dtype switch
        {
            "|u1" or "|b1" => 1,
            "<i2" or "<u2" => 2,
            "<i4" or "<u4" or "<f4" => 4,
            "<i8" or "<u8" or "<f8" => 8,
            _ => throw new InvalidDataException($"{path}: unsupported dtype {dtype}"),
        };
        int total = shape.Aggregate(1, (a, b) => a * b);
        var bytes = reader.ReadBytes(total * elementSize);
        var data = Array.CreateInstance(dtype switch
        {
            "|u1" => typeof(byte),
            "<i4" => typeof(int),
            "<f4" => typeof(float),
            "<f8" => typeof(double),
            _ => throw new InvalidDataException($"{path}: unsupported dtype {dtype}"),
        }, total);
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
        return new NpyArray(data, dtype, shape);
    }

    private static string Extract(string header, string key)
    {
        int start = header.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException($"header missing {key}: {header}");
        int valueStart = start + key.Length;
        while (valueStart < header.Length && header[valueStart] is ' ') valueStart++;
        if (valueStart < header.Length && header[valueStart] == '\'')
        {
            int end = header.IndexOf('\'', valueStart + 1);
            return header[(valueStart + 1)..end];
        }
        // Non-string literal: read to , or }
        int stop = valueStart;
        while (stop < header.Length && header[stop] is not (',' or '}')) stop++;
        return header[valueStart..stop].Trim();
    }
}
