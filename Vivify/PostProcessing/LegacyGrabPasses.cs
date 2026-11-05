#if BEAT_SABER_1453
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Vivify.PostProcessing;

// Unity exposes no runtime information about GrabPass passes, so the texture names they write are read
// from the serialized shaders in the bundle. Only uncompressed and LZ4 bundles are supported.
internal static class LegacyGrabPasses
{
    private const int ShaderClassId = 48;
    private const int GrabPassType = 2;
    private const int AlignFlag = 0x4000;

    private const string CommonStrings =
        "AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA\0" +
        "Component\0data\0deque\0double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0GameObject\0Generic Mono\0" +
        "GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0map\0Matrix4x4f\0MdFour\0MonoBehaviour\0MonoScript\0" +
        "m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0m_Enabled\0m_ExtensionPtr\0m_GameObject\0" +
        "m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_Name\0m_ObjectHideFlags\0m_PrefabInternal\0m_PrefabParentObject\0" +
        "m_Script\0m_StaticEditorFlags\0m_Type\0m_Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0" +
        "PPtr<Material>\0PPtr<MonoBehaviour>\0PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0" +
        "PPtr<TextAsset>\0PPtr<Texture>\0PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0Rectf\0RectInt\0" +
        "RectOffset\0second\0set\0short\0size\0SInt16\0SInt32\0SInt64\0SInt8\0staticvector\0string\0TextAsset\0" +
        "TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0UInt64\0UInt8\0unsigned int\0" +
        "unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0Vector4f\0m_ScriptingClassIdentifier\0" +
        "Gradient\0Type*\0int2_storage\0int3_storage\0BoundsInt\0m_CorrespondingSourceObject\0m_PrefabInstance\0" +
        "m_PrefabAsset\0FileSize\0Hash128\0";

    private static readonly Dictionary<string, Dictionary<int, string>> _grabPasses = new();
    private static readonly Dictionary<int, string> _commonStrings = BuildCommonStrings();

    internal static int Count { get; private set; }

    internal static void Load(string? bundlePath)
    {
        _grabPasses.Clear();
        Count = 0;
        if (bundlePath == null || !File.Exists(bundlePath))
        {
            return;
        }

        try
        {
            foreach (byte[] file in ReadSerializedFiles(File.ReadAllBytes(bundlePath)))
            {
                ReadShaders(file);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Debug($"Could not read GrabPass names from the bundle: {e.Message}");
        }
    }

    // Passes are indexed within the first subshader, matching how the extra passes are drawn.
    internal static bool TryGetTextureName(Shader shader, int pass, out string textureName)
    {
        textureName = string.Empty;
        return _grabPasses.TryGetValue(shader.name, out Dictionary<int, string> passes) &&
               passes.TryGetValue(pass, out textureName);
    }

    private static Dictionary<int, string> BuildCommonStrings()
    {
        Dictionary<int, string> strings = new();
        int offset = 0;
        foreach (string value in CommonStrings.Split('\0'))
        {
            strings[offset] = value;
            offset += value.Length + 1;
        }

        return strings;
    }

    private static IEnumerable<byte[]> ReadSerializedFiles(byte[] bundle)
    {
        Reader reader = new(bundle, true);
        if (reader.CString() != "UnityFS")
        {
            yield break;
        }

        int version = reader.Int32();
        reader.CString();
        reader.CString();
        reader.Int64();
        int compressedInfoSize = reader.Int32();
        int infoSize = reader.Int32();
        int flags = reader.Int32();
        if (version >= 7)
        {
            reader.Align(16);
        }

        int infoStart = (flags & 0x80) != 0 ? bundle.Length - compressedInfoSize : reader.Position;
        byte[] info = Decompress(bundle, infoStart, compressedInfoSize, infoSize, flags & 0x3F);
        if ((flags & 0x80) == 0)
        {
            reader.Position = infoStart + compressedInfoSize;
            if ((flags & 0x200) != 0)
            {
                reader.Align(16);
            }
        }

        Reader infoReader = new(info, true) { Position = 16 };
        int blockCount = infoReader.Int32();
        int dataSize = 0;
        (int Size, int CompressedSize, int Flags)[] blocks = new (int, int, int)[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            blocks[i] = (infoReader.Int32(), infoReader.Int32(), infoReader.Int16());
            dataSize += blocks[i].Size;
        }

        byte[] data = new byte[dataSize];
        int dataOffset = 0;
        int position = reader.Position;
        foreach ((int size, int compressedSize, int blockFlags) in blocks)
        {
            byte[] block = Decompress(bundle, position, compressedSize, size, blockFlags & 0x3F);
            Buffer.BlockCopy(block, 0, data, dataOffset, size);
            position += compressedSize;
            dataOffset += size;
        }

        int nodeCount = infoReader.Int32();
        for (int i = 0; i < nodeCount; i++)
        {
            long offset = infoReader.Int64();
            long size = infoReader.Int64();
            int nodeFlags = infoReader.Int32();
            infoReader.CString();
            if ((nodeFlags & 4) == 0)
            {
                continue;
            }

            byte[] file = new byte[size];
            Buffer.BlockCopy(data, (int)offset, file, 0, (int)size);
            yield return file;
        }
    }

    private static byte[] Decompress(byte[] source, int offset, int compressedSize, int size, int compression)
    {
        byte[] result = new byte[size];
        switch (compression)
        {
            case 0:
                Buffer.BlockCopy(source, offset, result, 0, size);
                break;
            case 2:
            case 3:
                DecodeLz4(source, offset, compressedSize, result);
                break;
            default:
                throw new NotSupportedException($"Compression type {compression} is not supported.");
        }

        return result;
    }

    private static void DecodeLz4(byte[] source, int offset, int length, byte[] destination)
    {
        int s = offset;
        int end = offset + length;
        int d = 0;
        while (s < end)
        {
            int token = source[s++];
            int literals = token >> 4;
            if (literals == 15)
            {
                int extra;
                do
                {
                    extra = source[s++];
                    literals += extra;
                }
                while (extra == 255);
            }

            Buffer.BlockCopy(source, s, destination, d, literals);
            s += literals;
            d += literals;
            if (s >= end)
            {
                break;
            }

            int distance = source[s] | (source[s + 1] << 8);
            s += 2;
            int match = token & 15;
            if (match == 15)
            {
                int extra;
                do
                {
                    extra = source[s++];
                    match += extra;
                }
                while (extra == 255);
            }

            match += 4;
            for (int m = d - distance; match > 0; match--)
            {
                destination[d++] = destination[m++];
            }
        }
    }

    private static void ReadShaders(byte[] file)
    {
        Reader header = new(file, true);
        header.Int32();
        header.Int32();
        int version = header.Int32();
        long dataOffset = header.Int32();
        if (version < 19)
        {
            return;
        }

        bool bigEndian = header.Byte() != 0;
        header.Position += 3;
        if (version >= 22)
        {
            header.Int32();
            header.Int64();
            dataOffset = header.Int64();
            header.Int64();
        }

        Reader reader = new(file, bigEndian) { Position = header.Position };
        reader.CString();
        reader.Int32();
        bool typeTree = reader.Byte() != 0;
        if (!typeTree)
        {
            return;
        }

        int typeCount = reader.Int32();
        (int ClassId, Node[] Nodes)[] types = new (int, Node[])[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            int classId = reader.Int32();
            reader.Byte();
            reader.Int16();
            if (classId == 114)
            {
                reader.Position += 16;
            }

            reader.Position += 16;
            types[i] = (classId, ReadNodes(reader));
            if (version >= 21)
            {
                reader.Position += reader.Int32() * 4;
            }
        }

        int objectCount = reader.Int32();
        for (int i = 0; i < objectCount; i++)
        {
            reader.Align(4);
            reader.Int64();
            long start = version >= 22 ? reader.Int64() : reader.UInt32();
            reader.Int32();
            int typeIndex = reader.Int32();
            if (types[typeIndex].ClassId != ShaderClassId)
            {
                continue;
            }

            Reader objectReader = new(file, bigEndian) { Position = (int)(dataOffset + start) };
            Node[] nodes = types[typeIndex].Nodes;
            int parsedForm = FindChild(nodes, 0, "m_ParsedForm");
            if (parsedForm < 0)
            {
                continue;
            }

            for (int child = 1; child < parsedForm; child = NextSibling(nodes, child))
            {
                Skip(objectReader, nodes, child);
            }

            if (ReadValue(objectReader, nodes, parsedForm) is Dictionary<string, object?> shader)
            {
                AddShader(shader);
            }
        }
    }

    private static void AddShader(Dictionary<string, object?> shader)
    {
        if (Get(shader, "m_Name") is not string name ||
            Get(shader, "m_SubShaders") is not List<object?> { Count: > 0 } subShaders ||
            subShaders[0] is not Dictionary<string, object?> subShader ||
            Get(subShader, "m_Passes") is not List<object?> passes)
        {
            return;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is Dictionary<string, object?> pass &&
                Get(pass, "m_Type") is GrabPassType)
            {
                string textureName = Get(pass, "m_TextureName") as string ?? string.Empty;
                if (!_grabPasses.TryGetValue(name, out Dictionary<int, string> grabPasses))
                {
                    grabPasses = new Dictionary<int, string>();
                    _grabPasses[name] = grabPasses;
                }

                grabPasses[i] = string.IsNullOrEmpty(textureName) ? "_GrabTexture" : textureName;
                Count++;
            }
        }
    }

    private static object? Get(Dictionary<string, object?> fields, string name) =>
        fields.TryGetValue(name, out object? value) ? value : null;

    private static Node[] ReadNodes(Reader reader)
    {
        int count = reader.Int32();
        int stringSize = reader.Int32();
        Node[] nodes = new Node[count];
        int start = reader.Position;
        int strings = start + (count * 32);
        for (int i = 0; i < count; i++)
        {
            reader.Position = start + (i * 32);
            reader.Int16();
            int level = reader.Byte();
            reader.Byte();
            int typeOffset = reader.Int32();
            int nameOffset = reader.Int32();
            int byteSize = reader.Int32();
            reader.Int32();
            int metaFlag = reader.Int32();
            nodes[i] = new Node(
                level,
                GetString(reader, strings, typeOffset),
                GetString(reader, strings, nameOffset),
                byteSize,
                metaFlag);
        }

        reader.Position = strings + stringSize;
        return nodes;
    }

    private static string GetString(Reader reader, int strings, int offset)
    {
        if ((offset & 0x80000000) != 0)
        {
            return _commonStrings.TryGetValue(offset & 0x7FFFFFFF, out string common) ? common : string.Empty;
        }

        int saved = reader.Position;
        reader.Position = strings + offset;
        string value = reader.CString();
        reader.Position = saved;
        return value;
    }

    private static int FindChild(Node[] nodes, int parent, string name)
    {
        for (int child = parent + 1; child < nodes.Length && nodes[child].Level > nodes[parent].Level; child = NextSibling(nodes, child))
        {
            if (nodes[child].Name == name)
            {
                return child;
            }
        }

        return -1;
    }

    private static int NextSibling(Node[] nodes, int index)
    {
        int next = index + 1;
        while (next < nodes.Length && nodes[next].Level > nodes[index].Level)
        {
            next++;
        }

        return next;
    }

    private static bool HasChildren(Node[] nodes, int index) =>
        index + 1 < nodes.Length && nodes[index + 1].Level > nodes[index].Level;

    private static void Skip(Reader reader, Node[] nodes, int index) => ReadValue(reader, nodes, index, false);

    // Values are only materialized for the fields read from the shader; everything else is skipped.
    private static object? ReadValue(Reader reader, Node[] nodes, int index, bool keep = true)
    {
        Node node = nodes[index];
        object? value = null;
        if (node.Type == "string")
        {
            int length = reader.Int32();
            value = keep ? Encoding.UTF8.GetString(reader.Data, reader.Position, length) : null;
            reader.Position += length;
            if (HasChildren(nodes, index) && (nodes[index + 1].MetaFlag & AlignFlag) != 0)
            {
                reader.Align(4);
            }
        }
        else if (node.Type == "TypelessData")
        {
            reader.Position += reader.Int32();
        }
        else if (HasChildren(nodes, index) && nodes[index + 1].Type == "Array")
        {
            int array = index + 1;
            int element = NextSibling(nodes, array + 1);
            int size = reader.Int32();
            if (!HasChildren(nodes, element) && nodes[element].ByteSize > 0)
            {
                reader.Position += size * nodes[element].ByteSize;
            }
            else
            {
                List<object?>? list = keep ? new List<object?>(size) : null;
                for (int i = 0; i < size; i++)
                {
                    object? item = ReadValue(reader, nodes, element, keep);
                    list?.Add(item);
                }

                value = list;
            }

            if ((nodes[array].MetaFlag & AlignFlag) != 0)
            {
                reader.Align(4);
            }
        }
        else if (HasChildren(nodes, index))
        {
            Dictionary<string, object?>? fields = keep ? new Dictionary<string, object?>() : null;
            for (int child = index + 1; child < nodes.Length && nodes[child].Level > node.Level; child = NextSibling(nodes, child))
            {
                bool wanted = keep && nodes[child].Name is "m_Name" or "m_SubShaders" or "m_Passes" or "m_Type" or "m_TextureName";
                object? item = ReadValue(reader, nodes, child, wanted);
                if (fields != null && wanted)
                {
                    fields[nodes[child].Name] = item;
                }
            }

            value = fields;
        }
        else
        {
            if (keep && node.ByteSize == 4)
            {
                value = reader.Int32();
            }
            else
            {
                reader.Position += node.ByteSize;
            }
        }

        if ((node.MetaFlag & AlignFlag) != 0)
        {
            reader.Align(4);
        }

        return value;
    }

    private readonly struct Node
    {
        internal readonly int Level;
        internal readonly string Type;
        internal readonly string Name;
        internal readonly int ByteSize;
        internal readonly int MetaFlag;

        internal Node(int level, string type, string name, int byteSize, int metaFlag)
        {
            Level = level;
            Type = type;
            Name = name;
            ByteSize = byteSize;
            MetaFlag = metaFlag;
        }
    }

    private sealed class Reader
    {
        private readonly bool _bigEndian;

        internal Reader(byte[] data, bool bigEndian)
        {
            Data = data;
            _bigEndian = bigEndian;
        }

        internal byte[] Data { get; }

        internal int Position { get; set; }

        internal byte Byte() => Data[Position++];

        internal short Int16() => (short)ReadInteger(2);

        internal int Int32() => (int)ReadInteger(4);

        internal uint UInt32() => (uint)ReadInteger(4);

        internal long Int64() => (long)ReadInteger(8);

        internal void Align(int alignment) => Position = (Position + alignment - 1) / alignment * alignment;

        internal string CString()
        {
            int end = Array.IndexOf(Data, (byte)0, Position);
            string value = Encoding.UTF8.GetString(Data, Position, end - Position);
            Position = end + 1;
            return value;
        }

        private ulong ReadInteger(int size)
        {
            ulong value = 0;
            for (int i = 0; i < size; i++)
            {
                ulong part = Data[Position + i];
                value |= _bigEndian ? part << ((size - 1 - i) * 8) : part << (i * 8);
            }

            Position += size;
            return value;
        }
    }
}
#endif
