using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace CharmReplacer
{
    internal static class SkinLoader
    {
        private static readonly byte[] s_pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// Load a PNG into a Unity Texture2D. Decoded with the BCL (zlib + PNG row filters): the game's
        /// IL2CPP build strips the managed ImageConversion.LoadImage, and System.Drawing.Common had to
        /// ship next to the DLL — a player missing that copy got no skin at all, theirs or anyone's.
        /// The backend only accepts PNG, so that is the only format handled.
        /// </summary>
        public static Texture2D LoadTextureFromFile(string path, string texName)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var pixels = DecodePng(File.ReadAllBytes(path), out int w, out int h);

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                tex.name = texName;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                return tex;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Skin] Error loading {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>A PNG file's pixels, bottom-up (Unity's order).</summary>
        public static Color32[] LoadPixels(string path, out int width, out int height) =>
            DecodePng(File.ReadAllBytes(path), out width, out height);

        private static Color32[] DecodePng(byte[] data, out int width, out int height)
        {
            if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(s_pngSignature))
                throw new InvalidDataException("not a PNG file");

            width = height = 0;
            int bitDepth = 0, colorType = 0, interlace = 0;
            byte[] palette = null, paletteAlpha = null;
            using var idat = new MemoryStream();

            int pos = 8;
            while (pos + 8 <= data.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
                string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                int body = pos + 8;
                if (length < 0 || body + length > data.Length) throw new InvalidDataException("truncated PNG chunk");

                switch (type)
                {
                    case "IHDR":
                        width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body));
                        height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body + 4));
                        bitDepth = data[body + 8];
                        colorType = data[body + 9];
                        interlace = data[body + 12];
                        break;
                    case "PLTE": palette = data.AsSpan(body, length).ToArray(); break;
                    case "tRNS": paletteAlpha = data.AsSpan(body, length).ToArray(); break;
                    case "IDAT": idat.Write(data, body, length); break;
                }
                if (type == "IEND") break;
                pos = body + length + 4; // + CRC
            }

            if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no IHDR");
            if (interlace != 0) throw new NotSupportedException("interlaced PNG; export it without interlacing");
            if (bitDepth != 8 && bitDepth != 16) throw new NotSupportedException($"{bitDepth}-bit PNG; export it as 8-bit");
            int channels = colorType switch
            {
                0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
                _ => throw new InvalidDataException($"PNG color type {colorType}"),
            };
            if (colorType == 3 && (palette == null || bitDepth != 8)) throw new InvalidDataException("bad palette PNG");

            int sample = bitDepth / 8;
            int bpp = channels * sample; // filter distance in bytes
            int stride = width * bpp;

            var raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length)
                {
                    int n = z.Read(raw, read, raw.Length - read);
                    if (n == 0) throw new InvalidDataException("truncated PNG image data");
                    read += n;
                }
            }

            var pixels = new Color32[width * height];
            var prev = new byte[stride];
            var line = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int src = y * (stride + 1);
                Buffer.BlockCopy(raw, src + 1, line, 0, stride);
                Unfilter(raw[src], line, prev, bpp);

                // PNG rows run top-down, Unity textures bottom-up.
                int dst = (height - 1 - y) * width;
                for (int x = 0; x < width; x++)
                    pixels[dst + x] = ReadPixel(line, x * bpp, sample, colorType, palette, paletteAlpha);

                (prev, line) = (line, prev);
            }
            return pixels;
        }

        private static void Unfilter(byte filter, byte[] line, byte[] prev, int bpp)
        {
            for (int i = 0; i < line.Length; i++)
            {
                int a = i >= bpp ? line[i - bpp] : 0;
                int b = prev[i];
                int c = i >= bpp ? prev[i - bpp] : 0;
                int add = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {filter}"),
                };
                line[i] = (byte)(line[i] + add);
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        // 16-bit samples keep their high byte.
        private static Color32 ReadPixel(byte[] line, int offset, int sample, int colorType, byte[] palette, byte[] paletteAlpha)
        {
            byte C(int k) => line[offset + k * sample];
            switch (colorType)
            {
                case 0: { byte g = C(0); return new Color32(g, g, g, 255); }
                case 2: return new Color32(C(0), C(1), C(2), 255);
                case 3:
                {
                    int index = line[offset];
                    if (index * 3 + 2 >= palette.Length) return new Color32(0, 0, 0, 255);
                    byte alpha = paletteAlpha != null && index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                    return new Color32(palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2], alpha);
                }
                case 4: { byte g = C(0); return new Color32(g, g, g, C(1)); }
                default: return new Color32(C(0), C(1), C(2), C(3));
            }
        }

        /// <summary>
        /// A texture's pixels, bottom-up. The game's textures are GPU-only, so they are read back through a
        /// RenderTexture (sRGB, so the bytes come back as authored).
        /// </summary>
        public static Color32[] ReadPixels(Texture source)
        {
            int w = source.width, h = source.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply(false, false);
                var pixels = readable.GetPixels32();
                UnityEngine.Object.Destroy(readable);
                return pixels;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Encodes bottom-up RGBA pixels (Unity's order) as an 8-bit RGBA PNG.</summary>
        public static byte[] EncodePng(Color32[] pixels, int width, int height)
        {
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int dst = y * (stride + 1) + 1; // filter byte 0 = none
                int src = (height - 1 - y) * width;
                for (int x = 0; x < width; x++)
                {
                    var c = pixels[src + x];
                    raw[dst + x * 4] = c.r;
                    raw[dst + x * 4 + 1] = c.g;
                    raw[dst + x * 4 + 2] = c.b;
                    raw[dst + x * 4 + 3] = c.a;
                }
            }

            using var idat = new MemoryStream();
            using (var z = new ZLibStream(idat, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);

            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; // bit depth
            header[9] = 6; // RGBA

            using var png = new MemoryStream();
            png.Write(s_pngSignature, 0, s_pngSignature.Length);
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", idat.ToArray());
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream output, string type, byte[] body)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, body.Length);
            output.Write(buffer, 0, 4);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes, 0, 4);
            output.Write(body, 0, body.Length);
            uint crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), body) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
            output.Write(buffer, 0, 4);
        }

        private static uint[] s_crcTable;

        private static uint Crc32(uint crc, byte[] data)
        {
            if (s_crcTable == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                s_crcTable = table;
            }
            foreach (byte b in data) crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static void LoadCustomSkin()
        {
            string skinPath = Path.Combine(Plugin.PluginDirectory, "Skin.png");
            string emissionPath = Path.Combine(Plugin.PluginDirectory, "Skin_Emission.png");

            var mainTex = LoadTextureFromFile(skinPath, "CustomPlayerSkin");
            if (mainTex != null)
            {
                CharmState.CustomSkinTexture = mainTex;
                Plugin.Log.LogInfo($"[Skin] ✓ Main Skin.png loaded");

                var emissionTex = LoadTextureFromFile(emissionPath, "CustomPlayerSkinEmission");
                if (emissionTex != null)
                {
                    CharmState.CustomSkinEmissionTexture = emissionTex;
                    Plugin.Log.LogInfo($"[Skin] ✓ Skin_Emission.png loaded");
                }
            }

            string skinsDir = Path.Combine(Plugin.PluginDirectory, "Skins");
            if (Directory.Exists(skinsDir))
            {
                foreach (var file in Directory.GetFiles(skinsDir, "*.png", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.EndsWith("_emission", StringComparison.OrdinalIgnoreCase)) continue;

                    // Remote-cached files (have a .hash sidecar) are named by SteamID and are
                    // applied under the correct nick by RemoteSkinService when the player
                    // appears. Loading them here would key them by the SteamID filename and
                    // never match a player. Only hand-placed files are loaded at boot.
                    if (File.Exists(file + ".hash")) continue;

                    var tex = LoadTextureFromFile(file, $"skin_{fileName}");
                    if (tex != null)
                    {
                        CharmState.PlayerBaseTextures[fileName] = tex;

                        string emFile = Path.Combine(Path.GetDirectoryName(file), fileName + "_emission.png");
                        var emTex = LoadTextureFromFile(emFile, $"skinemission_{fileName}");
                        if (emTex != null)
                        {
                            CharmState.PlayerEmissionTextures[fileName] = emTex;
                            Plugin.Log.LogInfo($"[Skin] ✓ Multiplayer skin loaded for: {fileName} (+ emission)");
                        }
                        else
                        {
                            Plugin.Log.LogInfo($"[Skin] ✓ Multiplayer skin loaded for: {fileName} (no emission file)");
                        }
                    }
                }
            }

            if (CharmState.CustomSkinTexture == null && CharmState.PlayerBaseTextures.Count == 0)
            {
                Plugin.Log.LogWarning($"[Skin] No custom skins found in Plugin directory or Skins/ folder.");
            }
        }
    }
}
