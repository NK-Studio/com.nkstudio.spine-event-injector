using System;
using System.Collections.Generic;
using System.Linq;

using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>
    /// 프로젝트 그래프 → 버전 중립 스켈레톤 모델(파이썬 PoC model.py).
    /// 필드 태그 번호는 4.0~4.3 프로젝트에서 같다(포맷마다 키 모양만 다르며 <see cref="ProjectView"/>가 감춘다).
    /// </summary>
    internal sealed class SkeletonModelReader
    {
        static readonly string[] Props = { "rotate", "x", "y", "scaleX", "scaleY", "shearY" };
        internal static readonly string[] SequenceModes = { "hold", "once", "loop", "pingpong", "onceReverse", "loopReverse", "pingpongReverse" };

        sealed class TimelineInfo
        {
            public readonly string Name;
            public readonly int ValueCount;

            public TimelineInfo(string name, int valueCount)
            {
                Name = name;
                ValueCount = valueCount;
            }
        }

        // 타임라인 종류 → (모델 이름, 값 채널 수)
        static readonly Dictionary<long, TimelineInfo> Timelines = new Dictionary<long, TimelineInfo>
        {
            [0] = new TimelineInfo("rotate", 1),
            [1] = new TimelineInfo("translate", 2),
            [2] = new TimelineInfo("scale", 2),
            [3] = new TimelineInfo("shear", 2),
            [4] = new TimelineInfo("color", 7),
            [5] = new TimelineInfo("attachment", 0),
            [6] = new TimelineInfo("deform", 0),
            [7] = new TimelineInfo("ik", 5),
            [8] = new TimelineInfo("transform", 6),
            [9] = new TimelineInfo("pathPosition", 1),
            [10] = new TimelineInfo("pathSpacing", 1),
            [11] = new TimelineInfo("pathMix", 3),
            [12] = new TimelineInfo("event", 0),
            [13] = new TimelineInfo("drawOrder", 0),
            [14] = new TimelineInfo("translatex", 1),
            [15] = new TimelineInfo("translatey", 1),
            [22] = new TimelineInfo("sequence", 3),
            [27] = new TimelineInfo("physicsWind", 1),
            [36] = new TimelineInfo("physicsWindAll", 1),
            [40] = new TimelineInfo("sliderMix", 1),
        };

        // 곡선이 붙는 채널 수(나머지 값은 계단)
        static readonly Dictionary<long, int> CurveChannels = new Dictionary<long, int>
        {
            [0] = 1, [1] = 2, [2] = 2, [3] = 2, [4] = 7, [6] = 1, [7] = 2, [8] = 6, [9] = 1, [10] = 1, [11] = 3, [14] = 1, [15] = 1,
            [27] = 1, [36] = 1, [40] = 1,
        };

        static readonly Dictionary<long, string> Inherits = new Dictionary<long, string>
        {
            [1] = "normal", [2] = "onlyTranslation", [3] = "noRotationOrReflection", [4] = "noScale", [5] = "noScaleOrReflection",
        };

        static readonly Dictionary<long, string> Blends = new Dictionary<long, string>
        {
            [1] = "normal", [2] = "additive", [3] = "multiply", [4] = "screen",
        };

        static readonly Dictionary<long, string> SpacingModes = new Dictionary<long, string>
        {
            [1] = "length", [2] = "fixed", [3] = "percent", [4] = "proportional",
        };

        static readonly Dictionary<long, string> RotateModes = new Dictionary<long, string>
        {
            [1] = "tangent", [2] = "chain", [3] = "chainScale",
        };

        static readonly Dictionary<long, string> PositionModes = new Dictionary<long, string>
        {
            [1] = "fixed", [2] = "percent",
        };

        readonly ProjectView _p;
        double _fps = 30.0;
        readonly Dictionary<Node, string> _full = new Dictionary<Node, string>(); // 폴더 안 슬롯·제약의 전체 이름(폴더/이름)
        List<Node> _bones;
        Dictionary<Node, int> _boneIndex;
        List<Node> _slots;
        Dictionary<Node, int> _slotIndex;
        List<Node> _skinNodes;

        SkeletonModelReader(ProjectView project) => _p = project;

        /// <summary>프로젝트의 스켈레톤마다 모델 하나.</summary>
        public static List<SkeletonModel> ReadAll(ProjectView project)
        {
            List<Node> skeletons = project.Doc.Of("Skeleton").ToList();
            if (skeletons.Count == 0)
            {
                throw new SpineFormatException(Loc.T("스켈레톤이 없습니다"));
            }

            return skeletons.Select(s => new SkeletonModelReader(project).Read(s)).ToList();
        }

        // ------------------------------------------------------------ 작은 도우미

        string Name(Node n)
        {
            if (n == null)
            {
                return null;
            }

            return _full.TryGetValue(n, out string full) && full != null ? full : _p.GetString(n, 1);
        }

        Node NodeOf(Node n, int tag, string cls = ProjectView.KindNode) => _p.GetNode(n, tag, cls);

        /// <summary>폴더 안 항목의 이름: 폴더 경로(Folder 1 이름, 3 부모)/이름. 뿌리 폴더는 이름이 없다.</summary>
        string FullName(Node n, int folderTag)
        {
            string name = _p.GetString(n, 1);
            Node folder = _p.GetNode(n, folderTag, "Folder");
            while (folder != null)
            {
                string part = _p.GetString(folder, 1);
                if (string.IsNullOrEmpty(part) == false)
                {
                    name = part + "/" + name;
                }

                folder = _p.GetNode(folder, 3, "Folder");
            }

            return name;
        }

        string SkinName(Node s) => FullName(s, 5);

        List<Node> Lst(Node n, int tag) => _p.Items(_p.GetNode(n, tag, "List"));

        double F(Node n, int tag, double fallback = 0.0)
        {
            object v = _p.Get(n, tag, ProjectView.KindNumber);
            if (v == null)
            {
                return fallback;
            }

            return v is float f ? f : (long)v;
        }

        long I(Node n, int tag, long fallback = 0)
        {
            object v = _p.Get(n, tag, ProjectView.KindNumber);
            if (v == null)
            {
                return fallback;
            }

            return v is float f ? (long)f : (long)v;
        }

        object LeafOf(Node n, int tag)
        {
            Node v = _p.GetNode(n, tag);
            return v != null ? _p.Leaf(v) : null;
        }

        long LeafLong(Node n, int tag, long fallback) => LeafOf(n, tag) is long v ? v : fallback;

        int[] LeafColor(Node n, int tag) => LeafOf(n, tag) as int[];

        static readonly int[] White = { 255, 255, 255, 255 };

        List<double> LeafFloats(Node n, int tag) => AsFloats(LeafOf(n, tag));

        static List<double> AsFloats(object leaf)
        {
            switch (leaf)
            {
                case List<double> doubles:
                    return doubles;
                case List<long> longs:
                    return longs.Select(v => (double)v).ToList();
                case List<double?> boxed:
                    return boxed.Select(v => v ?? 0.0).ToList();
                default:
                    return null;
            }
        }

        List<long> LeafInts(Node n, int tag)
        {
            switch (LeafOf(n, tag))
            {
                case List<long> longs:
                    return longs;
                case List<double> doubles:
                    return doubles.Select(v => (long)v).ToList();
                default:
                    return null;
            }
        }

        static string Rgba(int[] c) => c == null ? null : string.Concat(c.Take(4).Select(x => x.ToString("x2")));

        static string Rgb(int[] c) => c == null ? null : string.Concat(c.Take(3).Select(x => x.ToString("x2")));

        static long ZigzagDec(long v) => (long)((ulong)v >> 1) ^ -(v & 1);

        static string Mapped(Dictionary<long, string> map, long key, string fallback) =>
            map.TryGetValue(key, out string name) ? name : fallback;

        // ------------------------------------------------------------ 전체

        SkeletonModel Read(Node sk)
        {
            Node proj = _p.Root;
            _fps = F(proj, 2, 30.0);
            if (_fps == 0)
            {
                _fps = 30.0;
            }

            var m = new SkeletonModel
            {
                Name = _p.GetString(sk, 1),
                Format = _p.Format,
                Fps = _fps,
                Images = _p.GetString(sk, 3) ?? string.Empty,
                Audio = _p.GetString(sk, 34) ?? string.Empty,
                ReferenceScale = F(sk, 46, 100.0),
                Bounds = LeafFloats(sk, 24),
            };

            _bones = Lst(sk, 4);
            _boneIndex = IndexOf(_bones);
            _slots = Lst(sk, 13);
            _slotIndex = IndexOf(_slots);
            foreach (Node s0 in _slots)
            {
                _full[s0] = FullName(s0, 16);
            }

            foreach (Node c0 in Lst(sk, 16).Concat(Lst(sk, 22)).Concat(Lst(sk, 23)).Concat(ConstraintOrder(sk)))
            {
                if (c0 != null)
                {
                    _full[c0] = FullName(c0, 32);
                }
            }

            m.Bones = _bones.Select(Bone).ToList();
            m.Slots = _slots.Select(Slot).ToList();
            m.Constraints = Constraints(sk);
            _skinNodes = Lst(sk, 8);
            var inSkin = new HashSet<Node>(_skinNodes.SelectMany(s => Lst(s, 6)).Where(b => b != null));
            for (int i = 0; i < _bones.Count; i++)
            {
                m.Bones[i].Skin = inSkin.Contains(_bones[i]);
            }

            m.Skins = Skins(sk);
            m.Events = Lst(sk, 10).Select(EventData).ToList();
            m.Animations = Lst(sk, 7).Where(a => I(a, 5, 1) != 0).Select(Animation).ToList(); // 5: 내보내기 포함 여부
            return m;
        }

        static Dictionary<Node, int> IndexOf(List<Node> nodes)
        {
            var index = new Dictionary<Node, int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] != null)
                {
                    index[nodes[i]] = i;
                }
            }

            return index;
        }

        // ------------------------------------------------------------ 뼈·슬롯

        ModelBone Bone(Node b)
        {
            Node parent = NodeOf(b, 3, "Bone");
            return new ModelBone
            {
                Name = Name(b),
                Parent = parent != null ? Name(parent) : null,
                Length = F(b, 9),
                X = F(b, 10),
                Y = F(b, 11),
                Rotation = F(b, 12),
                ScaleX = F(b, 13, 1.0),
                ScaleY = F(b, 14, 1.0),
                ShearX = F(b, 23),
                ShearY = F(b, 25),
                Inherit = Mapped(Inherits, LeafLong(b, 27, 1), "normal"),
                Color = Rgba(LeafColor(b, 17)),
                Icon = NullIfEmpty(_p.GetString(b, 28)) ?? "bone",
            };
        }

        static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        ModelSlot Slot(Node s)
        {
            Node att = NodeOf(s, 9);
            if (att != null && Exported(att) == false)
            {
                att = null;
            }

            bool dark = I(s, 12) != 0;
            return new ModelSlot
            {
                Name = Name(s),
                Bone = Name(NodeOf(s, 2, "Bone")),
                Color = Rgba(LeafColor(s, 5) ?? White),
                Dark = dark ? Rgb(LeafColor(s, 11)) : null,
                Attachment = att != null ? AttKey(att) : null,
                Blend = Mapped(Blends, LeafLong(s, 7, 1), "normal"),
                Visible = I(s, 3, 1) != 0,
            };
        }

        // ------------------------------------------------------------ 제약

        List<Node> ConstraintOrder(Node sk)
        {
            Node old = _p.GetNode(sk, 25, "List");
            if (old != null)
            {
                return _p.Items(old);
            }

            Node folder = _p.GetNode(sk, 44, "Folder");
            if (folder == null)
            {
                foreach (Node c in _p.Doc.Of("IkConstraint").Concat(_p.Doc.Of("TransformConstraint")))
                {
                    if (_p.GetNode(c, 0, "Skeleton") != sk) // 다른 스켈레톤의 제약 폴더를 쓰지 않는다
                    {
                        continue;
                    }

                    folder = _p.GetNode(c, 32, "Folder");
                    if (folder != null)
                    {
                        break;
                    }
                }
            }

            var output = new List<Node>();
            if (folder != null)
            {
                Walk(folder, output);
            }

            output.Reverse();
            return output;
        }

        void Walk(Node folder, List<Node> output)
        {
            foreach (Node x in _p.Items(_p.GetNode(folder, 5, "List") ?? _p.GetNode(folder, 4, "List")))
            {
                if (x == null)
                {
                    continue;
                }

                if (x.Cls == "Folder")
                {
                    Walk(x, output);
                }
                else
                {
                    output.Add(x);
                }
            }
        }

        List<ModelConstraint> Constraints(Node sk)
        {
            List<Node> order = ConstraintOrder(sk);
            List<Node> nodes = Lst(sk, 16).Concat(Lst(sk, 22)).Concat(Lst(sk, 23)).ToList();
            var seen = new HashSet<Node>(order.Where(n => n != null));
            order.AddRange(nodes.Where(n => seen.Contains(n) == false));
            var output = new List<ModelConstraint>();
            for (int i = 0; i < order.Count; i++)
            {
                Node c = order[i];
                bool skin = Lst(c, 22).Count > 0;
                var o = new ModelConstraint { Name = Name(c), Order = i, Skin = skin };
                switch (c.Cls)
                {
                    case "IkConstraint":
                        o.Type = "ik";
                        o.Bones = Lst(c, 2).Select(Name).ToList();
                        o.Target = Name(NodeOf(c, 3, "Bone"));
                        o.Mix = F(c, 5, 1.0);
                        o.Softness = F(c, 14);
                        o.BendPositive = I(c, 16, 1) != 0;
                        o.Compress = I(c, 9) != 0;
                        o.Stretch = I(c, 11) != 0;
                        o.Uniform = I(c, 13) != 0;
                        break;
                    case "TransformConstraint":
                        o.Type = "transform";
                        o.Bones = Lst(c, 19).Select(Name).ToList();
                        o.Target = Name(NodeOf(c, 3, "Bone"));
                        o.Rotation = F(c, 14);
                        o.X = F(c, 6);
                        o.Y = F(c, 7);
                        o.ScaleX = F(c, 16);
                        o.ScaleY = F(c, 17);
                        o.ShearY = F(c, 18);
                        o.Local = I(c, 20) != 0;
                        o.Relative = I(c, 21) != 0;
                        o.Mixes[ModelConstraint.MixRotate] = F(c, 8, 1.0);
                        o.Mixes[ModelConstraint.MixX] = F(c, 4, 1.0);
                        o.Mixes[ModelConstraint.MixY] = F(c, 23, 1.0);
                        o.Mixes[ModelConstraint.MixScaleX] = F(c, 10, 1.0);
                        o.Mixes[ModelConstraint.MixScaleY] = F(c, 25, 1.0);
                        o.Mixes[ModelConstraint.MixShearY] = F(c, 12, 1.0);
                        o.Properties = TransformProps(c);
                        break;
                    case "PathConstraint":
                        o.Type = "path";
                        o.Bones = Lst(c, 2).Select(Name).ToList();
                        o.Target = Name(NodeOf(c, 3, "Slot"));
                        o.SpacingMode = Mapped(SpacingModes, LeafLong(c, 14, 1), "length");
                        o.RotateMode = Mapped(RotateModes, LeafLong(c, 15, 1), "tangent");
                        o.PositionMode = Mapped(PositionModes, LeafLong(c, 16, 2), "percent");
                        o.Position = F(c, 4);
                        o.Spacing = F(c, 12);
                        o.Rotation = F(c, 10);
                        o.Mixes[ModelConstraint.MixRotate] = F(c, 8, 1.0);
                        o.Mixes[ModelConstraint.MixX] = F(c, 6, 1.0);
                        o.Mixes[ModelConstraint.MixY] = F(c, 23, 1.0);
                        break;
                    case "PhysicsConstraint":
                    {
                        // 저장값 a(태그 7)·b(태그 9) → rotate = a·t, shearX = b·t, t = a/(a+b) (a 가 0이면 t = 1).
                        double a0 = F(c, 7);
                        double b0 = F(c, 9);
                        double t0 = a0 != 0 ? a0 / (a0 + b0) : 1.0;
                        o.Type = "physics";
                        o.Bone = Name(NodeOf(c, 3, "Bone"));
                        o.X = F(c, 5);
                        o.Y = F(c, 6);
                        o.Rotate = a0 * t0;
                        o.ScaleX = F(c, 8);
                        o.ShearX = b0 * t0;
                        o.Limit = F(c, 33, 5000.0);
                        o.Fps = 1.0 / F(c, 4, 1.0 / 60);
                        o.Inertia = F(c, 10, 1.0);
                        o.Strength = F(c, 12, 100.0);
                        o.Damping = F(c, 14, 1.0);
                        o.Mass = F(c, 16, 1.0);
                        o.Wind = F(c, 18);
                        o.Gravity = F(c, 20);
                        o.Mix = F(c, 23, 1.0);
                        o.InertiaGlobal = I(c, 25) != 0;
                        o.StrengthGlobal = I(c, 26) != 0;
                        o.DampingGlobal = I(c, 27) != 0;
                        o.MassGlobal = I(c, 28) != 0;
                        o.WindGlobal = I(c, 29) != 0;
                        o.GravityGlobal = I(c, 30) != 0;
                        o.MixGlobal = I(c, 31) != 0;
                        break;
                    }
                    case "SliderConstraint":
                    {
                        Node prop = NodeOf(c, 11);
                        Node animation = NodeOf(c, 2, "Animation");
                        Node bone = NodeOf(c, 10, "Bone");
                        o.Type = "slider";
                        o.Animation = animation != null ? FullName(animation, 7) : null;
                        o.Bone = bone != null ? Name(bone) : null;
                        o.Property = prop != null ? Props[int.Parse(prop.Cls.Substring(6)) - 134] : null;
                        o.Loop = I(c, 9) != 0;
                        o.Local = I(c, 13) != 0;
                        o.Max = F(c, 4);
                        o.Scale = F(c, 12, 1.0);
                        o.Mix = F(c, 6, 1.0);
                        o.Offset = F(c, 15);
                        break;
                    }
                    default:
                        throw new SpineFormatException(Loc.T("아직 지원하지 않는 제약: {0}", c.Cls));
                }

                output.Add(o);
            }

            return output;
        }

        /// <summary>
        /// 4.3: 속성 목록(29) — XfFrom134~139(rotate…shearY) {p0 오프셋, p1 [XfTo140~145 (오프셋, 최대, 배율)]}.
        /// 4.2까지는 없다(null) — 출력기가 여섯 속성 모두로 본다.
        /// </summary>
        List<ModelFromProperty> TransformProps(Node c)
        {
            Node list = _p.GetNode(c, 29, "List");
            if (list == null)
            {
                return null;
            }

            var output = new List<ModelFromProperty>();
            foreach (Node x in _p.Items(list))
            {
                if (x.Cls.StartsWith("XfFrom", StringComparison.Ordinal) == false)
                {
                    continue;
                }

                var source = new ModelFromProperty
                {
                    Name = Props[int.Parse(x.Cls.Substring(6)) - 134],
                    Offset = Number(x.F["p0"].Value),
                };
                if (x.F["p1"].Value is List<object> tos)
                {
                    foreach (object raw in tos)
                    {
                        var t = (Node)_p.Deref(raw);
                        List<double> values = _p.Floats(_p.BodyStart(t), 3);
                        source.To.Add(new ModelToProperty
                        {
                            Name = Props[int.Parse(t.Cls.Substring(4)) - 140],
                            Offset = values[0],
                            Max = values[1],
                            Scale = values[2],
                        });
                    }
                }

                output.Add(source);
            }

            return output;
        }

        static double Number(object v) => v is float f ? f : (long)v;

        // ------------------------------------------------------------ 스킨·첨부

        string AttKey(Node a)
        {
            Node placeholder = _p.GetNode(a, 4, "Placeholder");
            return placeholder != null ? _p.GetString(placeholder, 1) : AttName(a);
        }

        string AttName(Node a)
        {
            string n = _p.GetString(a, 1);
            if (n == null) // 정점 첨부: 이름이 다른 단계에 있다(d1 등)
            {
                foreach (KeyValuePair<string, FieldValue> kv in a.F)
                {
                    string[] parts = kv.Key.TrimStart('t', 'd').Split(':');
                    if (parts[parts.Length - 1] == "1" && kv.Value.Value is string s)
                    {
                        return s;
                    }
                }
            }

            return n;
        }

        /// <summary>첨부의 Export 체크(태그 5). 꺼져 있으면 첨부와 그 키가 내보내기에서 빠진다.</summary>
        bool Exported(Node a) => a.Cls == "Placeholder" || I(a, 5, 1) != 0;

        Node AttSlot(Node a) => _p.GetNode(a, 0, "Slot");

        sealed class SkinOrder : IComparer<List<(int Rank, string Part)>>
        {
            public int Compare(List<(int Rank, string Part)> a, List<(int Rank, string Part)> b)
            {
                for (int i = 0; i < a.Count && i < b.Count; i++)
                {
                    if (a[i].Rank != b[i].Rank)
                    {
                        return a[i].Rank < b[i].Rank ? -1 : 1;
                    }

                    int order = string.CompareOrdinal(a[i].Part, b[i].Part);
                    if (order != 0)
                    {
                        return order;
                    }
                }

                return a.Count.CompareTo(b.Count);
            }
        }

        /// <summary>폴더 밖 항목이 먼저, 그다음 폴더별(폴더 안에서 다시 같은 규칙).</summary>
        List<(int Rank, string Part)> SkinOrderKey(Node n)
        {
            string[] parts = SkinName(n).Split('/');
            return parts.Select((p, i) => (i == parts.Length - 1 ? 0 : 1, p.ToLowerInvariant())).ToList();
        }

        bool InDefault(Node a) => a.Cls != "Placeholder" && _p.GetNode(a, 4, "Placeholder") == null; // 6번 목록에는 모든 스킨의 첨부와 자리표시자가 함께 있다

        List<ModelSkin> Skins(Node sk)
        {
            var output = new List<ModelSkin>
            {
                new ModelSkin { Name = "default", Attachments = SkinAtts(Lst(sk, 6).Where(InDefault).ToList()) },
            };
            foreach (Node s in _skinNodes.OrderBy(SkinOrderKey, new SkinOrder()))
            {
                output.Add(new ModelSkin
                {
                    Name = SkinName(s),
                    Bones = Lst(s, 6).Select(Name).ToList(),
                    Constraints = Lst(s, 7).Select(Name).ToList(),
                    Attachments = SkinAtts(Lst(s, 2)),
                });
            }

            return output;
        }

        sealed class SkinRow
        {
            public int SlotIndex;
            public string Key;
            public ModelAttachment Attachment;
        }

        List<ModelSkinEntry> SkinAtts(List<Node> atts)
        {
            var rows = new List<SkinRow>();
            foreach (Node a in atts)
            {
                if (a.Cls == "Placeholder" || Exported(a) == false) // 자리표시자, Export 를 끈 첨부
                {
                    continue;
                }

                Node slot = AttSlot(a);
                rows.Add(new SkinRow
                {
                    SlotIndex = slot != null && _slotIndex.TryGetValue(slot, out int index) ? index : 1 << 30,
                    Key = AttKey(a),
                    Attachment = Attachment(a),
                });
            }

            return rows.OrderBy(r => r.SlotIndex).ThenBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => new ModelSkinEntry { Slot = Name(_slots[r.SlotIndex]), Key = r.Key, Attachment = r.Attachment })
                .ToList();
        }

        void VertexData(Node a, ModelAttachment o)
        {
            List<Node> bones = Lst(a, 1);
            List<Node> vws = Lst(a, 2);
            bool weighted = I(a, 30) != 0;
            var verts = new List<double>();
            var denseMap = new List<int>();
            var setup = new List<double>();
            if (weighted == false)
            {
                for (int vi = 0; vi < vws.Count; vi++)
                {
                    List<double> pos = LeafFloats(vws[vi], 1);
                    verts.AddRange(pos.Take(2));
                    setup.AddRange(pos.Take(2));
                    denseMap.Add(vi * 2);
                    denseMap.Add(vi * 2 + 1);
                }
            }
            else
            {
                int nb = bones.Count;
                for (int vi = 0; vi < vws.Count; vi++)
                {
                    List<double> weights = LeafFloats(vws[vi], 0);
                    List<double> pos = LeafFloats(vws[vi], 1);
                    List<int> used = Enumerable.Range(0, nb).Where(bi => weights[bi] != 0).ToList();
                    verts.Add(used.Count);
                    foreach (int bi in used)
                    {
                        verts.Add(_boneIndex[bones[bi]]);
                        verts.Add(pos[bi * 2]);
                        verts.Add(pos[bi * 2 + 1]);
                        verts.Add(weights[bi]);
                        setup.Add(pos[bi * 2]);
                        setup.Add(pos[bi * 2 + 1]);
                        denseMap.Add((vi * nb + bi) * 2);
                        denseMap.Add((vi * nb + bi) * 2 + 1);
                    }
                }
            }

            o.Vertices = verts;
            o.Weighted = weighted;
            o.DenseMap = denseMap;
            o.Setup = setup;
        }

        ModelSequence Sequence(Node q)
        {
            if (q == null)
            {
                return null;
            }

            return new ModelSequence
            {
                Count = ZigzagDec(I(q, 2)),
                Start = ZigzagDec(I(q, 1)),
                Digits = ZigzagDec(I(q, 3)),
                Setup = ZigzagDec(I(q, 4)),
            };
        }

        ModelAttachment Attachment(Node a)
        {
            var o = new ModelAttachment { Name = AttName(a), Key = AttKey(a) };
            if (a.Cls == "RegionAtt")
            {
                o.Type = "region";
                o.Path = _p.GetString(a, 13) ?? string.Empty;
                o.X = F(a, 6);
                o.Y = F(a, 7);
                o.ScaleX = F(a, 8, 1.0);
                o.ScaleY = F(a, 9, 1.0);
                o.Rotation = F(a, 10);
                o.Width = F(a, 11);
                o.Height = F(a, 12);
                o.Color = Rgba(LeafColor(a, 14) ?? White);
                o.Sequence = Sequence(_p.GetNode(a, 15, "Sequence"));
                return o;
            }

            if (a.Cls == "MeshAtt" || a.Cls == "BBoxAtt" || a.Cls == "ClippingAtt" || a.Cls == "PathAtt")
            {
                VertexData(a, o);
                o.VertexCount = Lst(a, 2).Count;
                o.Color = Rgba(LeafColor(a, 31) ?? White);
                if (a.Cls == "BBoxAtt")
                {
                    o.Type = "boundingbox";
                }
                else if (a.Cls == "PathAtt")
                {
                    o.Type = "path";
                    o.Closed = I(a, 12) != 0;
                    o.ConstantSpeed = I(a, 13, 1) != 0;
                    o.Lengths = LeafFloats(a, 15) ?? new List<double>();
                }
                else if (a.Cls == "ClippingAtt")
                {
                    o.Type = "clipping";
                    o.End = Name(_p.GetNode(a, 12, "Slot"));
                    o.Convex = _p.Format.StartsWith("4.3", StringComparison.Ordinal) && I(a, 13) != 0; // 4.3 새 필드
                }
                else
                {
                    Node parent = _p.GetNode(a, 26, "MeshAtt");
                    if (parent != null)
                    {
                        Node owner = _p.GetNode(parent, 32, "Skin");
                        o.Type = "linkedmesh";
                        o.Path = _p.GetString(a, 14) ?? string.Empty;
                        o.Parent = AttKey(parent);
                        o.Skin = owner != null ? SkinName(owner) : null;
                        o.Timelines = I(a, 27, 1) != 0;
                        o.Width = F(a, 16);
                        o.Height = F(a, 17);
                        return o;
                    }

                    List<long> tri = LeafInts(a, 13) ?? new List<long>();
                    o.Sequence = Sequence(_p.GetNode(a, 36, "Sequence"));
                    o.Type = "mesh";
                    o.Path = _p.GetString(a, 14) ?? string.Empty;
                    o.Uvs = LeafFloats(a, 12) ?? new List<double>();
                    o.Triangles = tri;
                    o.Edges = LeafInts(a, 8) ?? new List<long>();
                    o.Hull = HullCount(tri);
                    o.Width = F(a, 16);
                    o.Height = F(a, 17);
                }

                return o;
            }

            throw new SpineFormatException(Loc.T("아직 지원하지 않는 첨부: {0}", a.Cls));
        }

        /// <summary>경계 변(삼각형 하나에만 속한 변)의 수 = 테두리 정점 수.</summary>
        static int HullCount(List<long> tri)
        {
            var count = new Dictionary<(long, long), int>();
            for (int i = 0; i < tri.Count - 2; i += 3)
            {
                AddEdge(count, tri[i], tri[i + 1]);
                AddEdge(count, tri[i + 1], tri[i + 2]);
                AddEdge(count, tri[i + 2], tri[i]);
            }

            return count.Values.Count(v => v == 1);
        }

        static void AddEdge(Dictionary<(long, long), int> count, long a, long b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            count.TryGetValue(key, out int n);
            count[key] = n + 1;
        }

        // ------------------------------------------------------------ 이벤트

        ModelEvent EventData(Node e) => new ModelEvent
        {
            Name = _p.GetString(e, 2),
            Int = ZigzagDec(I(e, 3)),
            Float = F(e, 4),
            String = _p.GetString(e, 5) ?? string.Empty,
            Audio = _p.GetString(e, 6) ?? string.Empty,
            Volume = F(e, 8, 1.0),
            Balance = F(e, 9),
        };

        // ------------------------------------------------------------ 애니메이션

        ModelAnimation Animation(Node an)
        {
            var output = new ModelAnimation { Name = FullName(an, 7) };
            foreach (Node g in Lst(an, 2))
            {
                Node target = NodeOf(g, 1);
                foreach (Node tl in Lst(g, 2))
                {
                    ModelTimeline t = Timeline(target, tl);
                    if (t != null)
                    {
                        output.Timelines.Add(t);
                    }
                }
            }

            return output;
        }

        long TimelineType(Node tl)
        {
            if (tl.F.TryGetValue("type", out FieldValue type))
            {
                return (long)type.Value;
            }

            return (long)_p.Leaf(_p.GetNode(tl, 9));
        }

        List<Node> TimelineKeys(Node tl)
        {
            if (tl.F.TryGetValue("keys", out FieldValue keys))
            {
                return _p.Items(_p.Deref(keys.Value) as Node);
            }

            return Lst(tl, 2);
        }

        /// <summary>새 방식: extra&lt;종류&gt;, 옛 방식: 3 슬롯, 4 이벤트 정의, 5 첨부, 6 IK, 7 변형.</summary>
        Node TimelineExtra(Node tl, long ty)
        {
            if (tl.F.ContainsKey("type"))
            {
                return tl.F.TryGetValue("extra" + ty, out FieldValue v) && v.Value != null ? _p.Deref(v.Value) as Node : null;
            }

            int tag;
            switch (ty)
            {
                case 4: case 5: tag = 3; break;
                case 6: tag = 5; break;
                case 7: tag = 6; break;
                case 8: tag = 7; break;
                case 9: case 10: case 11: tag = 8; break;
                case 12: tag = 4; break;
                case 22: tag = 3; break;
                case 27: case 40: tag = 11; break;
                default: return null;
            }

            return _p.GetNode(tl, tag);
        }

        ModelTimeline Timeline(Node target, Node tl)
        {
            long ty = TimelineType(tl);
            if (Timelines.TryGetValue(ty, out TimelineInfo info) == false)
            {
                throw new SpineFormatException(Loc.T("아직 지원하지 않는 타임라인 종류 {0}", ty));
            }

            string kind = info.Name;
            Node extra = TimelineExtra(tl, ty);
            var t = new ModelTimeline { Type = kind, Code = (int)ty };
            switch (kind)
            {
                case "rotate":
                case "translate":
                case "scale":
                case "shear":
                case "translatex":
                case "translatey":
                    t.Bone = Name(target);
                    break;
                case "color":
                case "attachment":
                    t.Slot = Name(extra);
                    break;
                case "deform":
                {
                    Node att = extra;
                    if (Exported(att) == false)
                    {
                        return null;
                    }

                    t.Slot = tl.F.ContainsKey("type") ? Name(AttSlot(att)) : Name(_p.GetNode(tl, 3, "Slot"));
                    t.Attachment = AttKey(att);
                    Node skin = _p.GetNode(att, 32, "Skin");
                    t.Skin = skin != null ? SkinName(skin) : "default";
                    break;
                }
                case "ik":
                case "transform":
                case "pathPosition":
                case "pathSpacing":
                case "pathMix":
                case "physicsWind":
                case "sliderMix":
                    t.Constraint = Name(extra);
                    break;
                case "sequence":
                {
                    Node att = tl.F.ContainsKey("type") ? (Node)_p.Deref(tl.F["extraB22"].Value) : _p.GetNode(tl, 10);
                    if (Exported(att) == false)
                    {
                        return null;
                    }

                    t.Slot = Name(extra);
                    t.Attachment = AttKey(att);
                    Node skin = _p.GetNode(att, 32, "Skin");
                    t.Skin = skin != null ? SkinName(skin) : "default";
                    break;
                }
            }

            CurveChannels.TryGetValue(ty, out int nc);
            foreach (Node k in TimelineKeys(tl))
            {
                t.Keys.Add(Key(k, ty, info.ValueCount, nc));
            }

            return t;
        }

        ModelKey Key(Node k, long ty, int n, int nc)
        {
            double frame;
            string curve;
            List<double> vals;
            List<double> handles;
            Node payload = null;
            if (k.F.TryGetValue("time", out FieldValue time)) // 4.2.22~: 고정 배치
            {
                frame = (float)time.Value;
                long flag = (long)k.F["flag"].Value;
                curve = flag == 1 ? "bezier" : flag == 2 ? "stepped" : "linear";
                int valsAt = time.End.Value;
                vals = _p.Floats(valsAt, n);
                if (_p.CurveConditional)
                {
                    // 4.3.04~: 손잡이 블록은 플래그 뒤에, 이 키(나 앞 키)가 베지어일 때만 있다
                    int after = k.F["flag"].End.Value;
                    int payloadStart = k.End;
                    bool any = false;
                    foreach (KeyValuePair<string, FieldValue> kv in k.F)
                    {
                        if (kv.Key.StartsWith("payload", StringComparison.Ordinal))
                        {
                            payloadStart = any ? Math.Min(payloadStart, kv.Value.At) : kv.Value.At;
                            any = true;
                        }
                    }

                    handles = nc != 0 && payloadStart - after >= 20 * nc ? _p.Floats(after, nc * 5) : new List<double>();
                }
                else
                {
                    handles = nc != 0 ? _p.Floats(valsAt + 4 * n, nc * 5) : new List<double>();
                }

                foreach (KeyValuePair<string, FieldValue> kv in k.F)
                {
                    if (kv.Key.StartsWith("payload", StringComparison.Ordinal))
                    {
                        payload = _p.Deref(kv.Value.Value) as Node;
                    }
                }
            }
            else // ~4.2.20: 태그 필드
            {
                frame = F(k, 2);
                long c = LeafLong(k, 5, 1);
                curve = c == 2 ? "bezier" : c == 3 ? "stepped" : "linear";
                vals = LeafFloats(k, 3) ?? new List<double>();
                handles = LeafFloats(k, 4) ?? new List<double>();
                int tag = ty == 5 ? 6 : ty == 6 ? 9 : ty == 12 ? 7 : ty == 13 ? 8 : -1;
                payload = _p.GetNode(k, tag);
            }

            var key = new ModelKey { Frame = frame, Curve = curve, Values = vals, Handles = new List<double[]>() };
            for (int i = 0; i < handles.Count / 5; i++)
            {
                key.Handles.Add(new[] { handles[i * 5], handles[i * 5 + 1], handles[i * 5 + 2], handles[i * 5 + 3] });
            }

            if (ty == 5)
            {
                key.Name = payload != null && Exported(payload) ? AttKey(payload) : null;
            }
            else if (ty == 6)
            {
                key.Dense = payload != null ? AsFloats(_p.Leaf(payload)) : new List<double>();
            }
            else if (ty == 12)
            {
                Node data = _p.GetNode(payload, 1, "EventData");
                key.Event = new ModelKeyEvent
                {
                    Name = _p.GetString(data, 2),
                    Int = ZigzagDec(I(payload, 2)),
                    Float = F(payload, 3),
                    String = _p.GetString(payload, 4),
                    Volume = F(payload, 5, 1.0),
                    Balance = F(payload, 6),
                };
            }
            else if (ty == 13)
            {
                key.Offsets = _p.Items(payload)
                    .Select(e => new KeyValuePair<string, long>(Name(_p.GetNode(e, 0, "Slot")), ZigzagDec(I(e, 1))))
                    .ToList();
            }

            return key;
        }
    }
}
