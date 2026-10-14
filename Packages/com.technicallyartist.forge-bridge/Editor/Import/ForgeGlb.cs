using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ForgeBridge.Editor.Import
{
    public static class ForgeGlb
    {
        public static JObject Validate(byte[] bytes)
        {
            try { return ValidateCore(bytes); }
            catch (Exception error) when (error is OverflowException || error is JsonException || error is DecoderFallbackException || error is InvalidCastException)
            { throw new InvalidDataException("The glb contains malformed or excessive data: " + error.Message, error); }
        }

        private static JObject ValidateCore(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20 || bytes.Length > ForgeProtocol.MaximumPayload || Read(bytes, 0) != 0x46546c67 || Read(bytes, 4) != 2 || Read(bytes, 8) != bytes.Length)
                throw new InvalidDataException("The payload is not a complete glTF 2 binary file.");
            int jsonLength = checked((int)Read(bytes, 12));
            if (jsonLength <= 0 || jsonLength > bytes.Length - 20 || jsonLength % 4 != 0 || Read(bytes, 16) != 0x4e4f534a)
                throw new InvalidDataException("The glb JSON chunk is invalid.");
            JObject root;
            using (var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(bytes, 20, jsonLength))) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
            {
                root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("The glb JSON chunk contains trailing data.");
            }
            if ((string)root["asset"]?["version"] != "2.0") throw new InvalidDataException("The glb asset version must be 2.0.");
            int binaryLength = 0;
            int binaryOffset = -1;
            int offset = 20 + jsonLength;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 8) throw new InvalidDataException("The glb contains a truncated chunk.");
                int length = checked((int)Read(bytes, offset));
                if (length < 0 || length % 4 != 0 || length > bytes.Length - offset - 8) throw new InvalidDataException("The glb chunk runs outside the payload.");
                if (Read(bytes, offset + 4) == 0x004e4942)
                {
                    if (binaryOffset >= 0) throw new InvalidDataException("The glb contains several binary chunks.");
                    binaryLength = length;
                    binaryOffset = offset + 8;
                }
                offset += 8 + length;
            }
            foreach (JToken uri in root.SelectTokens("$..uri"))
                if (uri.Type != JTokenType.String || !((string)uri).StartsWith("data:", StringComparison.Ordinal))
                    throw new InvalidDataException("A push must be self-contained; external buffer and image URIs are refused.");
            var buffers = Array(root, "buffers");
            var bufferData = new ArraySegment<byte>[buffers.Count];
            long declaredBufferBytes = 0;
            for (int i = 0; i < buffers.Count; i++)
            {
                long length = Nonnegative(buffers[i], "byteLength");
                declaredBufferBytes = checked(declaredBufferBytes + length);
                if (length == 0 || declaredBufferBytes > ForgeProtocol.MaximumPayload) throw new InvalidDataException("Decoded glTF buffers exceed the 256 MiB limit.");
                if (buffers[i]["uri"] == null && (i != 0 || length > binaryLength)) throw new InvalidDataException("A buffer runs outside the glb binary chunk.");
                if (buffers[i]["uri"] != null)
                {
                    var decoded = DecodeDataUri((string)buffers[i]["uri"]);
                    if (length > decoded.Length) throw new InvalidDataException("A buffer runs outside its data URI.");
                    bufferData[i] = new ArraySegment<byte>(decoded, 0, (int)length);
                }
                else bufferData[i] = new ArraySegment<byte>(bytes, binaryOffset, (int)length);
            }
            foreach (JToken view in Array(root, "bufferViews"))
            {
                var buffer = Reference(root, "buffers", view["buffer"]);
                CheckRange(Nonnegative(view, "byteOffset", 0), Nonnegative(view, "byteLength"), Nonnegative(buffer, "byteLength"), "buffer view");
                if (view["byteStride"] != null)
                {
                    var stride = Nonnegative(view, "byteStride");
                    if (stride < 4 || stride > 252 || stride % 4 != 0) throw new InvalidDataException("Invalid buffer view byteStride.");
                }
            }
            long decodedAccessorBytes = 0;
            foreach (JToken accessor in Array(root, "accessors"))
            {
                long count = Nonnegative(accessor, "count");
                if (count == 0) throw new InvalidDataException("An accessor has no elements.");
                int component = checked((int)Nonnegative(accessor, "componentType"));
                int size = component == 5120 || component == 5121 ? 1 : component == 5122 || component == 5123 ? 2 : component == 5125 || component == 5126 ? 4 : 0;
                string type = (string)accessor["type"];
                int width = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" || type == "MAT2" ? 4 : type == "MAT3" ? 9 : type == "MAT4" ? 16 : 0;
                if (size == 0 || width == 0) throw new InvalidDataException("Invalid accessor component or shape.");
                long element = size * width;
                if (type == "MAT2" || type == "MAT3") { int columns = type == "MAT2" ? 2 : 3; element = ((columns * size + 3) / 4 * 4) * columns; }
                decodedAccessorBytes = checked(decodedAccessorBytes + checked(count * element));
                if (decodedAccessorBytes > ForgeProtocol.MaximumPayload) throw new InvalidDataException("Decoded glTF accessors exceed the 256 MiB limit.");
                if (Nonnegative(accessor, "byteOffset", 0) % size != 0) throw new InvalidDataException("An accessor byteOffset is not component-aligned.");
                if (accessor["bufferView"] != null)
                {
                    var view = Reference(root, "bufferViews", accessor["bufferView"]);
                    long stride = Nonnegative(view, "byteStride", element);
                    if (stride < element || stride % size != 0) throw new InvalidDataException("Invalid accessor stride.");
                    CheckRange(Nonnegative(accessor, "byteOffset", 0), checked((count - 1) * stride + element), Nonnegative(view, "byteLength"), "accessor");
                }
                if (accessor["sparse"] is JToken sparse)
                {
                    long sparseCount = Nonnegative(sparse, "count");
                    if (sparseCount == 0 || sparseCount > count) throw new InvalidDataException("Sparse accessor count exceeds its elements.");
                    var indices = RequireObject(sparse["indices"], "sparse indices");
                    var values = RequireObject(sparse["values"], "sparse values");
                    var indexView = Reference(root, "bufferViews", indices["bufferView"]);
                    var valueView = Reference(root, "bufferViews", values["bufferView"]);
                    int sparseType = checked((int)Nonnegative(indices, "componentType"));
                    int indexSize = sparseType == 5121 ? 1 : sparseType == 5123 ? 2 : sparseType == 5125 ? 4 : 0;
                    if (indexSize == 0 || indexView["byteStride"] != null || valueView["byteStride"] != null)
                        throw new InvalidDataException("Invalid sparse accessor index type or stride.");
                    CheckRange(Nonnegative(indices, "byteOffset", 0), checked(sparseCount * indexSize), Nonnegative(indexView, "byteLength"), "sparse indices");
                    CheckRange(Nonnegative(values, "byteOffset", 0), checked(sparseCount * element), Nonnegative(valueView, "byteLength"), "sparse values");
                    var source = bufferData[(int)indexView["buffer"]];
                    int begin = checked(source.Offset + (int)Nonnegative(indexView, "byteOffset", 0) + (int)Nonnegative(indices, "byteOffset", 0));
                    long previous = -1;
                    for (int i = 0; i < sparseCount; i++)
                    {
                        long index = 0;
                        for (int j = 0; j < indexSize; j++) index |= (long)source.Array[begin + i * indexSize + j] << (j * 8);
                        if (index <= previous || index >= count) throw new InvalidDataException("Sparse accessor indices are unsorted or out of range.");
                        previous = index;
                    }
                }
            }
            long imagePixels = 0;
            foreach (var image in Array(root, "images"))
            {
                if ((image["bufferView"] != null) == (image["uri"] != null)) throw new InvalidDataException("An image must have exactly one bufferView or data URI.");
                ArraySegment<byte> data;
                if (image["bufferView"] != null)
                {
                    var view = Reference(root, "bufferViews", image["bufferView"]);
                    var buffer = bufferData[(int)view["buffer"]];
                    data = new ArraySegment<byte>(buffer.Array, checked(buffer.Offset + (int)Nonnegative(view, "byteOffset", 0)), (int)Nonnegative(view, "byteLength"));
                    if ((string)image["mimeType"] != "image/png" && (string)image["mimeType"] != "image/jpeg")
                        throw new InvalidDataException("Embedded images must identify PNG or JPEG data.");
                }
                else data = new ArraySegment<byte>(DecodeDataUri((string)image["uri"]));
                imagePixels = checked(imagePixels + ImagePixels(data));
                if (imagePixels > 64L * 1024 * 1024) throw new InvalidDataException("Decoded images exceed the 64 megapixel limit.");
            }
            foreach (var texture in Array(root, "textures"))
            {
                if (texture["source"] != null) Reference(root, "images", texture["source"]);
                if (texture["sampler"] != null) Reference(root, "samplers", texture["sampler"]);
            }
            foreach (var material in Array(root, "materials"))
                foreach (var property in ((JContainer)material).DescendantsAndSelf().OfType<JProperty>().Where(p => p.Name.EndsWith("Texture", StringComparison.Ordinal)))
                    Reference(root, "textures", property.Value["index"]);
            foreach (var mesh in Array(root, "meshes")) foreach (var primitive in Array((JObject)mesh, "primitives"))
            {
                if (primitive["indices"] != null) Reference(root, "accessors", primitive["indices"]);
                if (primitive["material"] != null) Reference(root, "materials", primitive["material"]);
                var attributes = RequireObject(primitive["attributes"], "primitive attributes");
                if (attributes["POSITION"] == null) throw new InvalidDataException("A mesh primitive has no POSITION attribute.");
                var position = Reference(root, "accessors", attributes["POSITION"]);
                var vertices = Nonnegative(position, "count");
                if ((string)position["type"] != "VEC3") throw new InvalidDataException("POSITION attributes must be VEC3.");
                foreach (var attribute in attributes.Properties())
                    if (Nonnegative(Reference(root, "accessors", attribute.Value), "count") != vertices)
                        throw new InvalidDataException("Mesh attributes have different vertex counts.");
                foreach (var target in Array((JObject)primitive, "targets")) foreach (var attribute in ((JObject)target).Properties())
                    if (Nonnegative(Reference(root, "accessors", attribute.Value), "count") != vertices)
                        throw new InvalidDataException("Morph attributes have different vertex counts.");
                if (primitive["indices"] != null)
                    ValidatePrimitiveIndices(root, Reference(root, "accessors", primitive["indices"]), bufferData, vertices);
            }
            var nodes = Array(root, "nodes");
            var parents = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node["mesh"] != null) Reference(root, "meshes", node["mesh"]);
                if (node["skin"] != null) Reference(root, "skins", node["skin"]);
                foreach (var child in Indices(node, "children"))
                {
                    Reference(root, "nodes", child);
                    if (++parents[(int)child] > 1) throw new InvalidDataException("A glTF node has several parents.");
                }
            }
            var visits = new byte[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) Visit(nodes, visits, i, 0);
            foreach (var skin in Array(root, "skins"))
            {
                if (Indices(skin, "joints").Count == 0) throw new InvalidDataException("A skin has no joints.");
                foreach (var joint in Indices(skin, "joints")) Reference(root, "nodes", joint);
                if (skin["skeleton"] != null) Reference(root, "nodes", skin["skeleton"]);
                if (skin["inverseBindMatrices"] != null) Reference(root, "accessors", skin["inverseBindMatrices"]);
            }
            foreach (var scene in Array(root, "scenes")) foreach (var node in Indices(scene, "nodes")) Reference(root, "nodes", node);
            if (root["scene"] != null) Reference(root, "scenes", root["scene"]);
            foreach (var animation in Array(root, "animations"))
            {
                foreach (var sampler in Array((JObject)animation, "samplers"))
                { Reference(root, "accessors", sampler["input"]); Reference(root, "accessors", sampler["output"]); }
                foreach (var channel in Array((JObject)animation, "channels"))
                { Reference((JObject)animation, "samplers", channel["sampler"]); Reference(root, "nodes", channel["target"]?["node"]); }
            }
            return root;
        }

        private static void Visit(JArray nodes, byte[] visits, int node, int depth)
        {
            if (depth > 256 || visits[node] == 1) throw new InvalidDataException("The glTF node hierarchy contains a cycle or is too deep.");
            if (visits[node] == 2) return;
            visits[node] = 1;
            foreach (var child in nodes[node]["children"] ?? new JArray()) Visit(nodes, visits, (int)child, depth + 1);
            visits[node] = 2;
        }
        private static JArray Array(JObject root, string field)
        {
            if (root[field] == null) return new JArray();
            if (!(root[field] is JArray array) || array.Any(item => !(item is JObject)))
                throw new InvalidDataException("glTF " + field + " must be an array of objects.");
            return array;
        }
        private static JArray Indices(JToken root, string field)
        {
            if (root[field] == null) return new JArray();
            if (!(root[field] is JArray array) || array.Any(item => item.Type != JTokenType.Integer))
                throw new InvalidDataException("glTF " + field + " must be an array of indices.");
            return array;
        }
        private static JObject RequireObject(JToken token, string field) => token as JObject ?? throw new InvalidDataException("Invalid glTF " + field + ".");
        private static JToken Reference(JObject root, string field, JToken value)
        {
            if (value?.Type != JTokenType.Integer || !long.TryParse(value.ToString(), out var index) || index < 0 || index >= Array(root, field).Count)
                throw new InvalidDataException("Invalid index into glTF " + field + ".");
            return root[field][(int)value];
        }
        private static long Nonnegative(JToken item, string field, long fallback = -1)
        {
            if (!(item is JObject)) throw new InvalidDataException("Invalid glTF object for " + field + ".");
            var value = item[field];
            long result = value == null ? fallback : value.Type == JTokenType.Integer && long.TryParse(value.ToString(), out var parsed) ? parsed : -1;
            if (result < 0) throw new InvalidDataException("Invalid glTF " + field + ".");
            return result;
        }
        private static uint Read(byte[] bytes, int offset) => (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

        private static void CheckRange(long offset, long length, long capacity, string label)
        {
            // Subtraction avoids accepting an overflowing offset + length.
            if (offset > capacity || length > capacity - offset) throw new InvalidDataException("A glTF " + label + " runs outside its buffer.");
        }

        private static byte[] DecodeDataUri(string uri)
        {
            int comma = uri?.IndexOf(',') ?? -1;
            if (comma < 0 || !uri.StartsWith("data:", StringComparison.Ordinal) || !uri.Substring(0, comma).EndsWith(";base64", StringComparison.Ordinal))
                throw new InvalidDataException("Embedded glTF data must use a base64 data URI.");
            int length = uri.Length - comma - 1;
            if (length % 4 != 0 || length / 4L * 3 > ForgeProtocol.MaximumPayload + 2L)
                throw new InvalidDataException("Embedded glTF data is malformed or exceeds the payload limit.");
            // A URI is already bounded by the received GLB. Validate length before decoding allocation.
            try { return Convert.FromBase64String(uri.Substring(comma + 1)); }
            catch (FormatException error) { throw new InvalidDataException("Malformed base64 data URI.", error); }
        }

        private static long ImagePixels(ArraySegment<byte> data)
        {
            var bytes = data.Array;
            int offset = data.Offset;
            int end = offset + data.Count;
            long width = 0, height = 0;
            if (data.Count >= 24 && bytes[offset] == 137 && bytes[offset + 1] == 80 && bytes[offset + 2] == 78 && bytes[offset + 3] == 71 &&
                bytes[offset + 4] == 13 && bytes[offset + 5] == 10 && bytes[offset + 6] == 26 && bytes[offset + 7] == 10 &&
                bytes[offset + 12] == 73 && bytes[offset + 13] == 72 && bytes[offset + 14] == 68 && bytes[offset + 15] == 82)
            {
                width = BigEndian(bytes, offset + 16, 4); height = BigEndian(bytes, offset + 20, 4);
            }
            else if (data.Count >= 4 && bytes[offset] == 255 && bytes[offset + 1] == 216)
            {
                int cursor = offset + 2;
                while (cursor < end)
                {
                    if (bytes[cursor++] != 255) throw new InvalidDataException("Malformed JPEG marker.");
                    while (cursor < end && bytes[cursor] == 255) cursor++;
                    if (cursor >= end) break;
                    int marker = bytes[cursor++];
                    if (marker == 217 || marker == 218) break;
                    if (marker == 1 || marker >= 208 && marker <= 215) continue;
                    if (cursor + 2 > end) break;
                    int segment = (int)BigEndian(bytes, cursor, 2);
                    if (segment < 2 || segment > end - cursor) throw new InvalidDataException("JPEG segment runs outside its image.");
                    if (marker >= 192 && marker <= 207 && marker != 196 && marker != 200 && marker != 204)
                    {
                        if (segment < 8) throw new InvalidDataException("Malformed JPEG dimensions.");
                        height = BigEndian(bytes, cursor + 3, 2); width = BigEndian(bytes, cursor + 5, 2);
                        break;
                    }
                    cursor += segment;
                }
            }
            if (width < 1 || height < 1 || width > 16384 || height > 16384)
                throw new InvalidDataException("Images must be valid PNG or JPEG files with dimensions no greater than 16384.");
            return checked(width * height);
        }

        private static long BigEndian(byte[] bytes, int offset, int count)
        {
            long value = 0;
            for (int i = 0; i < count; i++) value = (value << 8) | bytes[offset + i];
            return value;
        }

        private static void ValidatePrimitiveIndices(JObject root, JToken accessor, ArraySegment<byte>[] buffers, long vertices)
        {
            int component = checked((int)Nonnegative(accessor, "componentType"));
            int size = component == 5121 ? 1 : component == 5123 ? 2 : component == 5125 ? 4 : 0;
            if (size == 0 || (string)accessor["type"] != "SCALAR") throw new InvalidDataException("Mesh indices must be unsigned scalar accessors.");
            ArraySegment<byte> View(JToken reference)
            {
                var view = Reference(root, "bufferViews", reference);
                var source = buffers[(int)view["buffer"]];
                return new ArraySegment<byte>(source.Array, checked(source.Offset + (int)Nonnegative(view, "byteOffset", 0)), (int)Nonnegative(view, "byteLength"));
            }
            long ReadIndex(ArraySegment<byte> data, long offset, int width)
            {
                long value = 0;
                for (int j = 0; j < width; j++) value |= (long)data.Array[data.Offset + (int)offset + j] << (8 * j);
                return value;
            }
            ArraySegment<byte> sourceData = default;
            long stride = size;
            if (accessor["bufferView"] != null)
            {
                sourceData = View(accessor["bufferView"]);
                stride = Nonnegative(Reference(root, "bufferViews", accessor["bufferView"]), "byteStride", size);
            }
            var sparse = accessor["sparse"];
            long sparseCount = sparse == null ? 0 : Nonnegative(sparse, "count");
            ArraySegment<byte> sparseIndices = default, sparseValues = default;
            int sparseWidth = 0;
            if (sparse != null)
            {
                sparseIndices = View(sparse["indices"]["bufferView"]);
                sparseValues = View(sparse["values"]["bufferView"]);
                int kind = (int)sparse["indices"]["componentType"];
                sparseWidth = kind == 5121 ? 1 : kind == 5123 ? 2 : 4;
            }
            long nextSparse = 0;
            long sparsePosition = sparseCount == 0 ? -1 : ReadIndex(sparseIndices, Nonnegative(sparse["indices"], "byteOffset", 0), sparseWidth);
            long start = Nonnegative(accessor, "byteOffset", 0);
            for (long i = 0, count = Nonnegative(accessor, "count"); i < count; i++)
            {
                long value = sourceData.Array == null ? 0 : ReadIndex(sourceData, start + i * stride, size);
                if (i == sparsePosition)
                {
                    value = ReadIndex(sparseValues, Nonnegative(sparse["values"], "byteOffset", 0) + nextSparse * size, size);
                    nextSparse++;
                    sparsePosition = nextSparse < sparseCount ? ReadIndex(sparseIndices, Nonnegative(sparse["indices"], "byteOffset", 0) + nextSparse * sparseWidth, sparseWidth) : -1;
                }
                if (value >= vertices) throw new InvalidDataException("A mesh index points outside its vertex attributes.");
            }
        }
    }
}
