using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>
    /// 첨부가 쓰는 이미지를 PNG 페이지로 묶고 .atlas 텍스트를 쓴다(파이썬 PoC atlas_packer.py).
    /// Spine 기본 팩 설정과 같다: 여백 2픽셀(가장자리 포함), 투명한 테두리 잘라내기, 회전 허용, 미리 곱한 알파, 영역 이름 정렬.
    /// 페이지 안 배치는 Spine 과 같지 않을 수 있다(런타임은 atlas 좌표를 쓰므로 결과에는 영향이 없다).
    /// </summary>
    internal static class SpineAtlasPacker
    {
        const int Padding = 2;
        const int MaxSize = 4096;
        const int AlphaThreshold = 3;
        const int ScoreShift = 16;
        const long BestNone = 1L << 40;
        static readonly double[] WidthFactors = { 0.8, 0.9, 1.0, 1.1, 1.2, 1.35, 1.5, 1.75, 2.0, 2.5, 3.0 };

        sealed class Region
        {
            public string Name;
            public int OrigW;
            public int OrigH;
            public int Left;
            public int Top;
            public int W;
            public int H;
            public int BottomOffset;
            public byte[] Pix;
            public int X;
            public int Y;
            public bool Rotated;

            public bool Stripped => W != OrigW || H != OrigH;
        }

        struct Rect : IEquatable<Rect>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int W;
            public readonly int H;

            public Rect(int x, int y, int w, int h)
            {
                X = x;
                Y = y;
                W = w;
                H = h;
            }

            public bool Equals(Rect o) => X == o.X && Y == o.Y && W == o.W && H == o.H;
        }

        struct Placed
        {
            public Region Region;
            public int X;
            public int Y;
            public bool Rotated;
        }

        sealed class Page
        {
            public int W;
            public int H;
            public List<Region> Regions;
        }

        /// <summary>
        /// 결과의 AtlasText·Pages 를 채운다. 이미지를 읽을 수 없으면 예외.
        /// 폴더에 없는 이미지는 Spine 에디터처럼 그리지 않는다(투명한 1픽셀 영역으로 두어 런타임이 첨부를 찾게 한다). 그 목록은 AtlasMessage 에 남긴다.
        /// </summary>
        public static void Pack(SkeletonModel model, string imagesDir, string name, SpineExportResult result)
        {
            HashSet<string> mesh = RegionNames(model, out List<string> names);
            if (names.Count == 0)
            {
                throw new SpineFormatException(Loc.T("묶을 이미지가 없습니다"));
            }

            if (Directory.Exists(imagesDir) == false)
            {
                throw new SpineFormatException(Loc.T("이미지 폴더가 없습니다: {0}", imagesDir));
            }

            var regions = new List<Region>();
            var missing = new List<string>();
            foreach (string regionName in names)
            {
                string path = Path.Combine(imagesDir, regionName + ".png");
                if (File.Exists(path) == false)
                {
                    missing.Add(regionName);
                    regions.Add(BuildRegion(regionName, 1, 1, new byte[4], false));
                    continue;
                }

                byte[] rgba = SpinePngCodec.DecodeFile(path, out int w, out int h);
                regions.Add(BuildRegion(regionName, w, h, rgba, mesh.Contains(regionName) == false));
            }

            List<Page> pages = PackPages(regions);
            var lines = new List<string>();
            for (int i = 0; i < pages.Count; i++)
            {
                Page page = pages[i];
                string fileName = pages.Count == 1 ? name + ".png" : name + (i + 1).ToString(CultureInfo.InvariantCulture) + ".png";
                if (i > 0)
                {
                    lines.Add(string.Empty); // 페이지 사이에는 빈 줄이 있어야 런타임이 새 페이지로 읽는다
                }

                result.Pages.Add(new SpineExportPage { Name = fileName, Width = page.W, Height = page.H, Rgba = Blit(page.W, page.H, page.Regions) });
                lines.Add(fileName);
                lines.Add("size:" + page.W.ToString(CultureInfo.InvariantCulture) + "," + page.H.ToString(CultureInfo.InvariantCulture));
                lines.Add("filter:Linear,Linear");
                lines.Add("pma:true");
                foreach (Region r in page.Regions.OrderBy(r => r.Name, NaturalComparer.Instance))
                {
                    lines.Add(r.Name);
                    lines.Add(Line("bounds", r.X, r.Y, r.W, r.H));
                    if (r.Stripped)
                    {
                        lines.Add(Line("offsets", r.Left, r.BottomOffset, r.OrigW, r.OrigH));
                    }

                    if (r.Rotated)
                    {
                        lines.Add("rotate:90");
                    }
                }
            }

            result.AtlasText = string.Join("\n", lines) + "\n";
            result.AtlasMessage = missing.Count > 0
                ? Loc.T("이미지 폴더 {0}에 없는 이미지는 그리지 않습니다: {1}", imagesDir, string.Join(", ", missing))
                : null;
        }

        static string Line(string key, int a, int b, int c, int d) =>
            key + ":" + a.ToString(CultureInfo.InvariantCulture) + "," + b.ToString(CultureInfo.InvariantCulture) + "," +
            c.ToString(CultureInfo.InvariantCulture) + "," + d.ToString(CultureInfo.InvariantCulture);

        /// <summary>첨부가 쓰는 이미지 이름(경로가 있으면 경로). region·mesh·linkedmesh 만. 돌려주는 값은 메시가 쓰는 이름 집합.</summary>
        static HashSet<string> RegionNames(SkeletonModel model, out List<string> sorted)
        {
            var names = new HashSet<string>();
            var mesh = new HashSet<string>();
            foreach (ModelSkin skin in model.Skins)
            {
                foreach (ModelSkinEntry row in skin.Attachments)
                {
                    ModelAttachment a = row.Attachment;
                    if (a.Type != "region" && a.Type != "mesh" && a.Type != "linkedmesh")
                    {
                        continue;
                    }

                    string n = !string.IsNullOrEmpty(a.Path) ? a.Path : !string.IsNullOrEmpty(a.Name) ? a.Name : row.Key;
                    ModelSequence q = a.Sequence;
                    if (q == null)
                    {
                        AddFrame(names, mesh, a, n);
                    }
                    else
                    {
                        for (long i = 0; i < q.Count; i++)
                        {
                            AddFrame(names, mesh, a, n + ZFill(q.Start + i, (int)q.Digits));
                        }
                    }
                }
            }

            sorted = names.OrderBy(n => n, StringComparer.Ordinal).OrderBy(n => n, NaturalComparer.Instance).ToList();
            return mesh;
        }

        static void AddFrame(HashSet<string> names, HashSet<string> mesh, ModelAttachment a, string frame)
        {
            names.Add(frame);
            if (a.Type != "region")
            {
                mesh.Add(frame);
            }
        }

        /// <summary>파이썬 str.zfill: 부호는 앞에 두고 0을 채운다.</summary>
        static string ZFill(long value, int width)
        {
            string s = value.ToString(CultureInfo.InvariantCulture);
            if (s.Length >= width)
            {
                return s;
            }

            return s[0] == '-' ? "-" + s.Substring(1).PadLeft(width - 1, '0') : s.PadLeft(width, '0');
        }

        /// <summary>투명 테두리 잘라내기(알파 3 이하, Spine 과 같음). 메시 이미지는 UV 가 전체 이미지 기준이라 자르지 않는다.</summary>
        static Region BuildRegion(string name, int w, int h, byte[] rgba, bool strip)
        {
            int left = w;
            int top = h;
            int right = -1;
            int bottom = -1;
            if (strip)
            {
                for (int y = 0; y < h; y++)
                {
                    int first = -1;
                    int last = -1;
                    for (int x = 0; x < w; x++)
                    {
                        if (rgba[(y * w + x) * 4 + 3] > AlphaThreshold)
                        {
                            if (first < 0)
                            {
                                first = x;
                            }

                            last = x;
                        }
                    }

                    if (first < 0)
                    {
                        continue;
                    }

                    top = Math.Min(top, y);
                    bottom = y;
                    left = Math.Min(left, first);
                    right = Math.Max(right, last);
                }

                if (right < 0)
                {
                    left = 0;
                    top = 0;
                    right = 0;
                    bottom = 0;
                }
            }
            else
            {
                left = 0;
                top = 0;
                right = w - 1;
                bottom = h - 1;
            }

            int rw = right - left + 1;
            int rh = bottom - top + 1;
            var pix = new byte[rw * rh * 4];
            int o = 0;
            for (int y = top; y <= bottom; y++)
            {
                int s = (y * w + left) * 4;
                for (int x = 0; x < rw; x++, s += 4, o += 4)
                {
                    int a = rgba[s + 3];
                    pix[o] = (byte)(rgba[s] * a / 255); // 미리 곱한 알파(버림, Spine 과 같음)
                    pix[o + 1] = (byte)(rgba[s + 1] * a / 255);
                    pix[o + 2] = (byte)(rgba[s + 2] * a / 255);
                    pix[o + 3] = (byte)a;
                }
            }

            return new Region
            {
                Name = name,
                OrigW = w,
                OrigH = h,
                Left = left,
                Top = top,
                W = rw,
                H = rh,
                BottomOffset = h - 1 - bottom,
                Pix = pix,
            };
        }

        /// <summary>MaxRects(짧은 변 맞춤). 좌표·크기에 여백 포함.</summary>
        static void PackPage(List<Region> regions, int width, int height, List<Placed> placed, List<Region> left)
        {
            var free = new List<Rect> { new Rect(Padding, Padding, width - Padding, height - Padding) };
            foreach (Region r in regions)
            {
                int w0 = r.W + Padding;
                int h0 = r.H + Padding;
                long best = BestNone;
                Rect spot = default;
                foreach (Rect f in free)
                {
                    if (w0 <= f.W && h0 <= f.H) // 점수 (짧은 변 여유, 긴 변 여유, 회전)을 정수 하나로 합쳐 비교한다
                    {
                        long key = Score(f.W - w0, f.H - h0) << 1;
                        if (key < best)
                        {
                            best = key;
                            spot = f;
                        }
                    }

                    if (h0 <= f.W && w0 <= f.H)
                    {
                        long key = (Score(f.W - h0, f.H - w0) << 1) | 1;
                        if (key < best)
                        {
                            best = key;
                            spot = f;
                        }
                    }

                    if (best == 0) // 빈틈 없이 맞는 자리는 더 찾지 않는다
                    {
                        break;
                    }
                }

                if (best == BestNone)
                {
                    left.Add(r);
                    continue;
                }

                bool rotated = (best & 1) != 0;
                int x = spot.X;
                int y = spot.Y;
                int w = rotated ? h0 : w0;
                int h = rotated ? w0 : h0;
                placed.Add(new Placed { Region = r, X = x, Y = y, Rotated = rotated });
                free = SplitFree(free, x, y, x + w, y + h);
            }
        }

        static long Score(long dw, long dh) => dw < dh ? (dw << ScoreShift) | dh : (dh << ScoreShift) | dw;

        /// <summary>겹치는 빈칸만 쪼갠다. 안 겹친 빈칸끼리는 이미 서로를 포함하지 않으므로 새 조각만 포함 검사를 한다.</summary>
        static List<Rect> SplitFree(List<Rect> free, int x, int y, int right, int bottom)
        {
            var next = new List<Rect>(free.Count + 4);
            var pieces = new List<int>();
            foreach (Rect f in free)
            {
                int fr = f.X + f.W;
                int fb = f.Y + f.H;
                if (x >= fr || right <= f.X || y >= fb || bottom <= f.Y)
                {
                    next.Add(f);
                    continue;
                }

                if (x > f.X)
                {
                    pieces.Add(next.Count);
                    next.Add(new Rect(f.X, f.Y, x - f.X, f.H));
                }

                if (right < fr)
                {
                    pieces.Add(next.Count);
                    next.Add(new Rect(right, f.Y, fr - right, f.H));
                }

                if (y > f.Y)
                {
                    pieces.Add(next.Count);
                    next.Add(new Rect(f.X, f.Y, f.W, y - f.Y));
                }

                if (bottom < fb)
                {
                    pieces.Add(next.Count);
                    next.Add(new Rect(f.X, bottom, f.W, fb - bottom));
                }
            }

            if (pieces.Count == 0)
            {
                return next;
            }

            var isPiece = new HashSet<int>(pieces);
            var drop = new HashSet<int>();
            foreach (int i in pieces)
            {
                Rect a = next[i];
                int ar = a.X + a.W;
                int ab = a.Y + a.H;
                for (int j = 0; j < next.Count; j++)
                {
                    Rect b = next[j];
                    if (a.X >= b.X && a.Y >= b.Y && ar <= b.X + b.W && ab <= b.Y + b.H &&
                        i != j && (a.Equals(b) == false || i > j || isPiece.Contains(j) == false))
                    {
                        drop.Add(i);
                        break;
                    }
                }
            }

            if (drop.Count == 0)
            {
                return next;
            }

            var kept = new List<Rect>(next.Count - drop.Count);
            for (int i = 0; i < next.Count; i++)
            {
                if (drop.Contains(i) == false)
                {
                    kept.Add(next[i]);
                }
            }

            return kept;
        }

        /// <summary>여러 폭을 시험해 넓이가 가장 작은 배치를 고른다. 한 장에 안 들어가면 페이지를 나눈다.</summary>
        static List<Page> PackPages(List<Region> regions)
        {
            List<Region> todo = regions
                .OrderBy(r => -Math.Max(r.W, r.H))
                .ThenBy(r => -Math.Min(r.W, r.H))
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();
            var pages = new List<Page>();
            while (todo.Count > 0)
            {
                long area = todo.Sum(r => (long)(r.W + Padding) * (r.H + Padding));
                int widest = todo.Max(r => Math.Max(r.W, r.H) + 2 * Padding);
                int side = Math.Max(widest, (int)Math.Sqrt(area));
                var widths = new SortedSet<int>(WidthFactors.Select(f => Math.Min(MaxSize, Math.Max(widest, (int)(side * f)))));
                List<Placed> bestPlaced = null;
                List<Region> bestLeft = null;
                int bestW = 0;
                int bestH = 0;
                int bestLeftCount = 0;
                long bestArea = 0;
                foreach (int wide in widths)
                {
                    var placed = new List<Placed>();
                    var left = new List<Region>();
                    PackPage(todo, wide, MaxSize, placed, left);
                    if (placed.Count == 0)
                    {
                        continue;
                    }

                    int usedW = placed.Max(p => p.X + (p.Rotated ? p.Region.H : p.Region.W)) + Padding;
                    int usedH = placed.Max(p => p.Y + (p.Rotated ? p.Region.W : p.Region.H)) + Padding;
                    long usedArea = (long)usedW * usedH;
                    if (bestPlaced == null || left.Count < bestLeftCount || (left.Count == bestLeftCount && usedArea < bestArea))
                    {
                        bestPlaced = placed;
                        bestLeft = left;
                        bestW = usedW;
                        bestH = usedH;
                        bestLeftCount = left.Count;
                        bestArea = usedArea;
                    }
                }

                if (bestPlaced == null)
                {
                    throw new SpineFormatException(Loc.T("{0}픽셀 페이지에 들어가지 않는 이미지가 있습니다: {1}", MaxSize, todo[0].Name));
                }

                foreach (Placed p in bestPlaced)
                {
                    p.Region.X = p.X;
                    p.Region.Y = p.Y;
                    p.Region.Rotated = p.Rotated;
                }

                pages.Add(new Page { W = bestW, H = bestH, Regions = bestPlaced.Select(p => p.Region).ToList() });
                todo = bestLeft;
            }

            return pages;
        }

        static byte[] Blit(int pageW, int pageH, List<Region> regions)
        {
            var output = new byte[pageW * pageH * 4];
            foreach (Region r in regions)
            {
                if (r.Rotated == false)
                {
                    for (int y = 0; y < r.H; y++)
                    {
                        Buffer.BlockCopy(r.Pix, y * r.W * 4, output, ((r.Y + y) * pageW + r.X) * 4, r.W * 4);
                    }
                }
                else // 90도 회전: 원본 (x, y) → 페이지 (r.X + y, r.Y + w - 1 - x)
                {
                    for (int y = 0; y < r.H; y++)
                    {
                        for (int x = 0; x < r.W; x++)
                        {
                            Buffer.BlockCopy(r.Pix, (y * r.W + x) * 4, output, ((r.Y + r.W - 1 - x) * pageW + r.X + y) * 4, 4);
                        }
                    }
                }
            }

            return output;
        }
    }
}
