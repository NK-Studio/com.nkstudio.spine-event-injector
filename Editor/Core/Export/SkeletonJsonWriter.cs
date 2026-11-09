using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using NKStudio.SpineEventInjector.Project;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>미리 문자열로 만든 숫자(JSON 에 따옴표 없이 들어간다).</summary>
    internal sealed class JsonNum
    {
        public readonly string S;

        public JsonNum(string s) => S = s;
    }

    /// <summary>순서를 지키는 JSON 객체. 이미 있는 키에 다시 쓰면 자리는 그대로 두고 값만 바꾼다.</summary>
    internal sealed class JsonMap
    {
        readonly List<string> _keys = new List<string>();
        readonly Dictionary<string, object> _values = new Dictionary<string, object>();

        public IReadOnlyList<string> Keys => _keys;

        public int Count => _keys.Count;

        public bool Contains(string key) => _values.ContainsKey(key);

        public object this[string key]
        {
            get => _values[key];
            set
            {
                if (_values.ContainsKey(key) == false)
                {
                    _keys.Add(key);
                }

                _values[key] = value;
            }
        }

        public JsonMap GetOrAddMap(string key)
        {
            if (_values.TryGetValue(key, out object v))
            {
                return (JsonMap)v;
            }

            var map = new JsonMap();
            this[key] = map;
            return map;
        }

        /// <summary>키를 이름 순서(자연 정렬)로 다시 늘어놓은 새 객체.</summary>
        public JsonMap SortedNatural()
        {
            var sorted = new JsonMap();
            foreach (string key in _keys.OrderBy(k => k, NaturalComparer.Instance))
            {
                sorted[key] = _values[key];
            }

            return sorted;
        }
    }

    /// <summary>
    /// 모델 → Spine JSON 텍스트(파이썬 PoC json_writer.py). 4.0·4.1·4.2 는 옛 목록형, 4.3 은 constraints 통합형.
    /// 4.0~4.2 는 값 종류마다 정해진 소수 자릿수로 반올림하고 끝의 0을 지운다.
    /// 4.3 은 반올림하지 않고 float32 의 가장 짧은 표기를 쓴다. 기본값과 같은 필드는 쓰지 않는다.
    /// </summary>
    internal sealed class SkeletonJsonWriter
    {
        const double Unset = 2147483648.0;

        // 값 종류
        const string Pos = "pos";
        const string Scale = "scale";
        const string Mix = "mix";
        const string Time = "time";
        const string CTime = "ctime";
        const string Uv = "uv";
        const string Weight = "weight";
        const string Deform = "deform";

        readonly SkeletonModel _m;
        readonly string _version;
        readonly int _major;
        readonly int _minor;
        readonly bool _v43;
        readonly bool _nonessential;
        readonly double _fps;

        SkeletonJsonWriter(SkeletonModel model, string version, bool nonessential)
        {
            _m = model;
            _version = version;
            string[] parts = version.Split('.');
            _major = int.Parse(parts[0], CultureInfo.InvariantCulture);
            _minor = int.Parse(parts[1], CultureInfo.InvariantCulture);
            _v43 = AtLeast(4, 3);
            _nonessential = nonessential;
            _fps = model.Fps;
        }

        public static string Write(SkeletonModel model, string version, bool nonessential) =>
            Dumps(new SkeletonJsonWriter(model, version, nonessential).Build());

        bool AtLeast(int major, int minor) => _major > major || (_major == major && _minor >= minor);

        // ------------------------------------------------------------ 숫자

        static int Digits(string kind)
        {
            switch (kind)
            {
                case Pos: return 2;
                case Scale: return 4;
                case Mix: return 3;
                case Time: return 4;
                case CTime: return 3;
                case Uv: return 5;
                case Weight: return 5;
                case Deform: return 5;
                default: throw new ArgumentException(kind);
            }
        }

        JsonNum N(double x, string kind = Pos) =>
            new JsonNum(_v43 ? SpineNumbers.FmtF32(x) : SpineNumbers.FmtRound(x, Digits(kind)));

        /// <summary>4.3 에서 정수도 '.0' 을 붙이는 자리(정점·메시 크기 등).</summary>
        JsonNum Nf(double x, string kind = Pos) =>
            new JsonNum(_v43 ? SpineNumbers.FmtF32(x, false) : SpineNumbers.FmtRound(x, Digits(kind)));

        /// <summary>프레임 → 초. 4.0~4.2 편집기는 float32 로 1/fps 를 곱하고, 4.3 은 fps 로 나눈다.</summary>
        double Sec(double frame)
        {
            if (_v43)
            {
                return SpineNumbers.F32(SpineNumbers.F32(frame) / SpineNumbers.F32(_fps));
            }

            return SpineNumbers.F32(SpineNumbers.F32(frame) * SpineNumbers.F32(1.0 / _fps));
        }

        JsonNum TimeOf(double frame) => N(Sec(frame), Time);

        bool IsDefault(double x, double d, string kind = Pos)
        {
            if (_v43) // 4.3 도 값 종류별 자릿수로 기본값을 판정한다(뼈 scaleY 0.999634 는 쓰고, 위치 0.004 는 뺀다)
            {
                return SpineNumbers.RoundHalfUp(SpineNumbers.F32(x) - SpineNumbers.F32(d), Digits(kind)) == 0;
            }

            return SpineNumbers.RoundHalfUp(x, Digits(kind)) == SpineNumbers.RoundHalfUp(d, Digits(kind));
        }

        /// <summary>정점 좌표·가중치: 4.3 도 값은 자릿수로 반올림한다(표기만 float).</summary>
        JsonNum Vert(double x, string kind = Pos)
        {
            if (_v43)
            {
                return new JsonNum(SpineNumbers.FmtF32(SpineNumbers.RoundHalfUp(x, Digits(kind)), false));
            }

            return new JsonNum(SpineNumbers.FmtRound(x, Digits(kind)));
        }

        /// <summary>기본값이 아니면 쓴다.</summary>
        void Put(JsonMap o, string key, double value, double fallback, string kind = Pos)
        {
            if (IsDefault(value, fallback, kind) == false)
            {
                o[key] = N(value, kind);
            }
        }

        static List<object> L<T>(IEnumerable<T> items, Func<T, object> convert)
        {
            var list = new List<object>();
            foreach (T item in items)
            {
                list.Add(convert(item));
            }

            return list;
        }

        // ------------------------------------------------------------ 전체

        JsonMap Build()
        {
            SkeletonModel m = _m;
            var output = new JsonMap();
            output["skeleton"] = Skeleton();
            output["bones"] = L(m.Bones, Bone);
            output["slots"] = L(m.Slots, Slot);
            List<ModelConstraint> cons = m.Constraints;
            if (_v43)
            {
                if (cons.Count > 0)
                {
                    output["constraints"] = L(cons, Constraint43);
                }
            }
            else
            {
                List<ModelConstraint> iks = SortedOfType(cons, "ik");
                List<ModelConstraint> trs = SortedOfType(cons, "transform");
                List<ModelConstraint> paths = SortedOfType(cons, "path");
                List<ModelConstraint> phys = SortedOfType(cons, "physics");
                if (cons.Any(c => c.Type == "slider"))
                {
                    throw new SpineEventUsageException(Loc.T("슬라이더 제약은 Spine 4.3 전용이라 이 버전으로 내보낼 수 없습니다"));
                }

                if (iks.Count > 0)
                {
                    output["ik"] = L(iks, Ik);
                }

                if (trs.Count > 0)
                {
                    output["transform"] = L(trs, Transform);
                }

                if (paths.Count > 0)
                {
                    output["path"] = L(paths, Path);
                }

                if (phys.Count > 0 && AtLeast(4, 2))
                {
                    output["physics"] = L(phys, Physics);
                }
                else if (phys.Count > 0)
                {
                    throw new SpineEventUsageException(Loc.T("물리 제약은 Spine 4.2 이상에서만 내보낼 수 있습니다"));
                }
            }

            output["skins"] = L(m.Skins, Skin);
            if (m.Events.Count > 0)
            {
                var events = new JsonMap();
                foreach (ModelEvent e in m.Events)
                {
                    events[e.Name] = Event(e);
                }

                output["events"] = events;
            }

            var animations = new JsonMap();
            foreach (ModelAnimation a in m.Animations)
            {
                animations[a.Name] = Animation(a);
            }

            if (animations.Count > 0)
            {
                output["animations"] = animations;
            }

            var skeleton = (JsonMap)output["skeleton"];
            var withHash = new JsonMap();
            withHash["hash"] = Hash(output); // 해시는 맨 앞에 온다
            foreach (string key in skeleton.Keys)
            {
                withHash[key] = skeleton[key];
            }

            output["skeleton"] = withHash;
            return output;
        }

        static List<ModelConstraint> SortedOfType(List<ModelConstraint> cons, string type) =>
            cons.Where(c => c.Type == type).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        /// <summary>편집기의 해시 계산법은 알 수 없다. 내용으로 정해지는 8바이트 값을 쓴다(런타임은 비교에만 쓴다).</summary>
        static string Hash(JsonMap output)
        {
            var body = new JsonMap();
            foreach (string key in output.Keys)
            {
                if (key != "skeleton")
                {
                    body[key] = output[key];
                }
            }

            using (SHA1 sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Dumps(body)));
                return Convert.ToBase64String(digest, 0, 8).TrimEnd('=');
            }
        }

        JsonMap Skeleton()
        {
            SkeletonModel m = _m;
            var s = new JsonMap();
            s["spine"] = _version;
            if (m.Bounds != null && m.Bounds.Count > 0)
            {
                s["x"] = N(m.Bounds[0]);
                s["y"] = N(m.Bounds[1]);
                s["width"] = N(m.Bounds[2]);
                s["height"] = N(m.Bounds[3]);
            }

            if (m.ReferenceScale != 100 && AtLeast(4, 2))
            {
                s["referenceScale"] = N(m.ReferenceScale);
            }

            if (_nonessential)
            {
                if (m.Fps != 30)
                {
                    s["fps"] = N(m.Fps);
                }

                s["images"] = m.Images;
                s["audio"] = m.Audio.Length > 0 || _v43 == false ? m.Audio : null;
            }

            return s;
        }

        object Bone(ModelBone b)
        {
            var o = new JsonMap();
            o["name"] = b.Name;
            if (b.Parent != null)
            {
                o["parent"] = b.Parent;
            }

            if (b.Length != 0)
            {
                o["length"] = N(b.Length);
            }

            Put(o, "rotation", b.Rotation, 0);
            Put(o, "x", b.X, 0);
            Put(o, "y", b.Y, 0);
            Put(o, "scaleX", b.ScaleX, 1, Scale);
            Put(o, "scaleY", b.ScaleY, 1, Scale);
            Put(o, "shearX", b.ShearX, 0);
            Put(o, "shearY", b.ShearY, 0);
            if (b.Inherit != "normal")
            {
                o[AtLeast(4, 2) ? "inherit" : "transform"] = b.Inherit;
            }

            if (b.Skin)
            {
                o["skin"] = true;
            }

            if (_nonessential)
            {
                if (string.IsNullOrEmpty(b.Color) == false && b.Color != "989898ff" && b.Color != "9b9b9bff")
                {
                    o["color"] = b.Color;
                }

                if (string.IsNullOrEmpty(b.Icon) == false && b.Icon != "bone" && AtLeast(4, 1))
                {
                    o["icon"] = b.Icon;
                }
            }

            return o;
        }

        object Slot(ModelSlot s)
        {
            var o = new JsonMap();
            o["name"] = s.Name;
            o["bone"] = s.Bone;
            if (s.Color != "ffffffff")
            {
                o["color"] = s.Color;
            }

            if (string.IsNullOrEmpty(s.Dark) == false)
            {
                o["dark"] = s.Dark;
            }

            if (s.Attachment != null)
            {
                o["attachment"] = s.Attachment;
            }

            if (s.Blend != "normal")
            {
                o["blend"] = s.Blend;
            }

            if (_nonessential && s.Visible == false && AtLeast(4, 1))
            {
                o["visible"] = false;
            }

            return o;
        }

        static void HeaderFields(JsonMap o, ModelConstraint c)
        {
            o["name"] = c.Name;
            if (c.Order != 0)
            {
                o["order"] = c.Order;
            }

            if (c.Skin)
            {
                o["skin"] = true;
            }
        }

        static void BendFields(JsonMap o, ModelConstraint c)
        {
            if (c.Bones.Count == 2)
            {
                if (c.BendPositive == false)
                {
                    o["bendPositive"] = false;
                }

                if (c.Stretch)
                {
                    o["stretch"] = true;
                }
            }
            else
            {
                if (c.Compress)
                {
                    o["compress"] = true;
                }

                if (c.Stretch)
                {
                    o["stretch"] = true;
                }

                if (c.Uniform)
                {
                    o["uniform"] = true;
                }
            }
        }

        object Ik(ModelConstraint c)
        {
            var o = new JsonMap();
            HeaderFields(o, c);
            o["bones"] = c.Bones;
            o["target"] = c.Target;
            Put(o, "mix", c.Mix, 1, Mix);
            Put(o, "softness", c.Softness, 0);
            BendFields(o, c);
            return o;
        }

        object Transform(ModelConstraint c)
        {
            var o = new JsonMap();
            HeaderFields(o, c);
            o["bones"] = c.Bones;
            o["target"] = c.Target;
            Put(o, "rotation", c.Rotation, 0);
            if (c.Local)
            {
                o["local"] = true;
            }

            if (c.Relative)
            {
                o["relative"] = true;
            }

            Put(o, "x", c.X, 0);
            Put(o, "y", c.Y, 0);
            Put(o, "scaleX", c.ScaleX, 0, Scale);
            Put(o, "scaleY", c.ScaleY, 0, Scale);
            Put(o, "shearY", c.ShearY, 0);
            Mixes(o, c.Mixes, false);
            return o;
        }

        /// <summary>위치·간격의 자릿수: 비율 모드는 scale(4자리), 길이 모드는 pos(2자리).</summary>
        static void PathKinds(ModelConstraint c, out string pos, out string spacing)
        {
            pos = c.PositionMode == "percent" ? Scale : Pos;
            spacing = c.SpacingMode == "percent" || c.SpacingMode == "proportional" ? Scale : Pos;
        }

        void PathFields(JsonMap o, ModelConstraint c)
        {
            if (c.PositionMode != "percent")
            {
                o["positionMode"] = c.PositionMode;
            }

            if (c.SpacingMode != "length")
            {
                o["spacingMode"] = c.SpacingMode;
            }

            if (c.RotateMode != "tangent")
            {
                o["rotateMode"] = c.RotateMode;
            }

            PathKinds(c, out string pos, out string spc);
            Put(o, "rotation", c.Rotation, 0);
            Put(o, "position", c.Position, 0, pos);
            Put(o, "spacing", c.Spacing, 0, spc);
            Put(o, "mixRotate", c.Mixes[ModelConstraint.MixRotate], 1, Mix);
            Put(o, "mixX", c.Mixes[ModelConstraint.MixX], 1, Mix);
            Put(o, "mixY", c.Mixes[ModelConstraint.MixY], c.Mixes[ModelConstraint.MixX], Mix);
        }

        object Path(ModelConstraint c)
        {
            var o = new JsonMap();
            HeaderFields(o, c);
            o["bones"] = c.Bones;
            o["target"] = c.Target;
            PathFields(o, c);
            return o;
        }

        void PhysicsFields(JsonMap o, ModelConstraint c)
        {
            Put(o, "x", c.X, 0, Scale);
            Put(o, "y", c.Y, 0, Scale);
            Put(o, "rotate", c.Rotate, 0, Scale);
            Put(o, "scaleX", c.ScaleX, 0, Scale);
            Put(o, "shearX", c.ShearX, 0, Scale);
            Put(o, "limit", c.Limit, 5000);
            Put(o, "fps", c.Fps, 60);
            // 관성·감쇠의 생략 기준은 런타임 버전마다 다르다: 4.3 은 0.5·0.85, 4.2 는 1
            Put(o, "inertia", c.Inertia, AtLeast(4, 3) ? 0.5 : 1, Scale);
            Put(o, "strength", c.Strength, 100);
            Put(o, "damping", c.Damping, AtLeast(4, 3) ? 0.85 : 1, Scale);
            Put(o, "mass", c.Mass, 1, Scale);
            Put(o, "wind", c.Wind, 0);
            Put(o, "gravity", c.Gravity, 0);
            Put(o, "mix", c.Mix, 1, Scale);
            SetIf(o, "inertiaGlobal", c.InertiaGlobal);
            SetIf(o, "strengthGlobal", c.StrengthGlobal);
            SetIf(o, "dampingGlobal", c.DampingGlobal);
            SetIf(o, "massGlobal", c.MassGlobal);
            SetIf(o, "windGlobal", c.WindGlobal);
            SetIf(o, "gravityGlobal", c.GravityGlobal);
            SetIf(o, "mixGlobal", c.MixGlobal);
        }

        static void SetIf(JsonMap o, string key, bool on)
        {
            if (on)
            {
                o[key] = true;
            }
        }

        object Physics(ModelConstraint c)
        {
            var o = new JsonMap();
            HeaderFields(o, c);
            o["bone"] = c.Bone;
            PhysicsFields(o, c);
            return o;
        }

        /// <summary>mix 필드 여섯 개. 순서: rotate, x, y, scaleX, scaleY, shearY.</summary>
        void Mixes(JsonMap o, double[] c, bool keyed)
        {
            double[] defaults =
            {
                1,
                1,
                c[ModelConstraint.MixX],
                1,
                _v43 && keyed ? 1 : c[ModelConstraint.MixScaleX], // 4.3 키의 mixScaleY 기본값은 1
                1,
            };
            string kind = keyed ? Scale : Mix;
            for (int i = 0; i < 6; i++)
            {
                Put(o, ModelConstraint.MixNames[i], c[i], defaults[i], kind);
            }
        }

        static List<ModelFromProperty> LegacyProperties(ModelConstraint c)
        {
            // 4.2 이하 프로젝트의 변형 제약을 4.3 형식으로: 여섯 속성이 각자 자기 자신으로, mix 가 0 인 속성은 빠진다
            string[] names = { "rotate", "x", "y", "scaleX", "scaleY", "shearY" };
            var output = new List<ModelFromProperty>();
            for (int i = 0; i < 6; i++)
            {
                if (c.Mixes[i] != 0)
                {
                    var from = new ModelFromProperty { Name = names[i], Offset = 0 };
                    from.To.Add(new ModelToProperty
                    {
                        Name = names[i],
                        Offset = 0,
                        Max = names[i] == "scaleX" || names[i] == "scaleY" ? 1.0 : 100.0,
                        Scale = 1.0,
                    });
                    output.Add(from);
                }
            }

            return output;
        }

        object Constraint43(ModelConstraint c)
        {
            var o = new JsonMap();
            o["type"] = c.Type;
            o["name"] = c.Name;
            if (c.Skin)
            {
                o["skin"] = true;
            }

            if (c.Type == "ik")
            {
                o["target"] = c.Target;
                o["bones"] = c.Bones;
                Put(o, "mix", c.Mix, 1);
                Put(o, "softness", c.Softness, 0);
                BendFields(o, c);
                return o;
            }

            if (c.Type == "physics")
            {
                o["bone"] = c.Bone;
                PhysicsFields(o, c);
                return o;
            }

            if (c.Type == "slider")
            {
                SetIf(o, "loop", c.Loop);
                if (string.IsNullOrEmpty(c.Animation) == false)
                {
                    o["animation"] = c.Animation;
                }

                if (string.IsNullOrEmpty(c.Bone) == false)
                {
                    o["bone"] = c.Bone;
                }

                if (string.IsNullOrEmpty(c.Property) == false)
                {
                    o["property"] = c.Property;
                }

                Put(o, "max", c.Max, 0);
                if (IsDefault(c.Scale, 1, Scale) == false)
                {
                    o["scale"] = N(c.Scale); // 4.3 이라 자릿수는 쓰이지 않는다
                }

                SetIf(o, "local", c.Local);
                return o;
            }

            if (c.Type == "path")
            {
                o["slot"] = c.Target;
                o["bones"] = c.Bones;
                PathFields(o, c);
                return o;
            }

            o["source"] = c.Target;
            o["bones"] = c.Bones;
            List<ModelFromProperty> props = c.Properties ?? LegacyProperties(c);
            var targets = new HashSet<string>(props.SelectMany(p => p.To).Select(t => t.Name));
            PutIfTarget(o, targets, "rotation", "rotate", c.Rotation);
            PutIfTarget(o, targets, "x", "x", c.X);
            PutIfTarget(o, targets, "y", "y", c.Y);
            PutIfTarget(o, targets, "scaleX", "scaleX", c.ScaleX);
            PutIfTarget(o, targets, "scaleY", "scaleY", c.ScaleY);
            PutIfTarget(o, targets, "shearY", "shearY", c.ShearY);
            var po = new JsonMap();
            foreach (ModelFromProperty from in props)
            {
                var d = new JsonMap();
                Put(d, "offset", from.Offset, 0);
                var to = new JsonMap();
                foreach (ModelToProperty t in from.To)
                {
                    var td = new JsonMap();
                    Put(td, "offset", t.Offset, 0);
                    Put(td, "max", t.Max, 1);
                    Put(td, "scale", t.Scale, 1);
                    to[t.Name] = td;
                }

                d["to"] = to;
                po[from.Name] = d;
            }

            o["properties"] = po;
            if (c.Local)
            {
                o["localSource"] = true;
                o["localTarget"] = true;
            }

            SetIf(o, "additive", c.Relative);
            PutIfHave(o, targets, "mixRotate", "rotate", c.Mixes[ModelConstraint.MixRotate], 1);
            PutIfHave(o, targets, "mixX", "x", c.Mixes[ModelConstraint.MixX], 1);
            PutIfHave(o, targets, "mixY", "y", c.Mixes[ModelConstraint.MixY], targets.Contains("x") ? c.Mixes[ModelConstraint.MixX] : 1);
            PutIfHave(o, targets, "mixScaleX", "scaleX", c.Mixes[ModelConstraint.MixScaleX], 1);
            PutIfHave(o, targets, "mixScaleY", "scaleY", c.Mixes[ModelConstraint.MixScaleY], targets.Contains("scaleX") ? c.Mixes[ModelConstraint.MixScaleX] : 1);
            PutIfHave(o, targets, "mixShearY", "shearY", c.Mixes[ModelConstraint.MixShearY], 1);
            return o;
        }

        // 대상 속성이 없는 오프셋은 쓰지 않는다
        void PutIfTarget(JsonMap o, HashSet<string> targets, string key, string prop, double value)
        {
            if (targets.Contains(prop))
            {
                Put(o, key, value, 0);
            }
        }

        void PutIfHave(JsonMap o, HashSet<string> targets, string key, string prop, double value, double fallback)
        {
            if (targets.Contains(prop))
            {
                Put(o, key, value, fallback);
            }
        }

        // ------------------------------------------------------------ 스킨

        string ConstraintType(string name)
        {
            foreach (ModelConstraint c in _m.Constraints)
            {
                if (c.Name == name)
                {
                    return c.Type;
                }
            }

            return null;
        }

        ModelConstraint Constraint(string name)
        {
            foreach (ModelConstraint c in _m.Constraints)
            {
                if (c.Name == name)
                {
                    return c;
                }
            }

            throw new SpineFormatException(Loc.T("제약을 찾을 수 없습니다: {0}", name));
        }

        /// <summary>슬롯·첨부 키 모두 자연 정렬. 이름이 다르지만 자연 정렬로 같으면 뒤의 키를 보지 않는다(파이썬 튜플 비교와 같음).</summary>
        sealed class RowOrder : IComparer<ModelSkinEntry>
        {
            public int Compare(ModelSkinEntry a, ModelSkinEntry b)
            {
                if (a.Slot != b.Slot)
                {
                    return NaturalComparer.Instance.Compare(a.Slot, b.Slot);
                }

                return a.Key == b.Key ? 0 : NaturalComparer.Instance.Compare(a.Key, b.Key);
            }
        }

        object Skin(ModelSkin s)
        {
            var o = new JsonMap();
            o["name"] = s.Name;
            if (s.Bones.Count > 0)
            {
                o["bones"] = s.Bones;
            }

            List<string> OfType(string type) => s.Constraints.Where(n => ConstraintType(n) == type).ToList();
            List<string> iks = OfType("ik");
            List<string> trs = OfType("transform");
            List<string> paths = OfType("path");
            List<string> phys = OfType("physics");
            if (iks.Count > 0)
            {
                o["ik"] = iks;
            }

            if (trs.Count > 0)
            {
                o["transform"] = trs;
            }

            if (paths.Count > 0)
            {
                o["path"] = paths;
            }

            if (phys.Count > 0)
            {
                o["physics"] = phys;
            }

            var atts = new JsonMap();
            foreach (ModelSkinEntry row in s.Attachments.OrderBy(r => r, new RowOrder()))
            {
                atts.GetOrAddMap(row.Slot)[row.Key] = Attachment(row.Attachment);
            }

            if (atts.Count > 0) // 첨부가 없는 스킨은 키를 쓰지 않는다
            {
                o["attachments"] = atts;
            }

            return o;
        }

        object Attachment(ModelAttachment a)
        {
            var o = new JsonMap();
            string t = a.Type;
            if (t != "region")
            {
                o["type"] = t;
            }

            if (a.Name != a.Key)
            {
                o["name"] = a.Name;
            }

            switch (t)
            {
                case "region":
                    PathAndColor(o, a);
                    Put(o, "x", a.X, 0);
                    Put(o, "y", a.Y, 0);
                    Put(o, "scaleX", a.ScaleX, 1, Scale);
                    Put(o, "scaleY", a.ScaleY, 1, Scale);
                    Put(o, "rotation", a.Rotation, 0);
                    o["width"] = N(a.Width);
                    o["height"] = N(a.Height);
                    SequenceField(o, a);
                    return o;
                case "mesh":
                    PathAndColor(o, a);
                    o["uvs"] = L(a.Uvs, x => N(x, Uv));
                    o["triangles"] = a.Triangles;
                    o["vertices"] = Verts(a);
                    o["hull"] = a.Hull;
                    if (_nonessential)
                    {
                        if (a.Edges.Count > 0)
                        {
                            o["edges"] = a.Edges;
                        }

                        o["width"] = Nf(a.Width);
                        o["height"] = Nf(a.Height);
                    }

                    SequenceField(o, a);
                    return o;
                case "linkedmesh":
                    PathAndColor(o, a);
                    if (string.IsNullOrEmpty(a.Skin) == false)
                    {
                        o["skin"] = a.Skin;
                    }

                    o[_v43 ? "source" : "parent"] = a.Parent;
                    if (a.Timelines == false)
                    {
                        o[AtLeast(4, 1) ? "timelines" : "deform"] = false;
                    }

                    if (_nonessential)
                    {
                        o["width"] = Nf(a.Width);
                        o["height"] = Nf(a.Height);
                    }

                    return o;
                case "path":
                    if (a.Closed)
                    {
                        o["closed"] = true;
                    }

                    if (a.ConstantSpeed == false)
                    {
                        o["constantSpeed"] = false;
                    }

                    o["lengths"] = L(a.Lengths, x => Nf(x));
                    o["vertexCount"] = a.VertexCount;
                    o["vertices"] = Verts(a);
                    if (_nonessential && a.Color != "ff7f00ff")
                    {
                        o["color"] = a.Color;
                    }

                    return o;
                case "boundingbox":
                case "clipping":
                    if (t == "clipping")
                    {
                        o["end"] = a.End;
                        if (_v43 && a.Convex)
                        {
                            o["convex"] = true;
                        }
                    }

                    o["vertexCount"] = a.VertexCount;
                    o["vertices"] = Verts(a);
                    if (_nonessential && a.Color != (t == "boundingbox" ? "60f000ff" : "ffffffff"))
                    {
                        o["color"] = a.Color;
                    }

                    return o;
                case "point":
                    Put(o, "x", a.X, 0);
                    Put(o, "y", a.Y, 0);
                    Put(o, "rotation", a.Rotation, 0);
                    if (_nonessential && a.Color != "f1f100ff")
                    {
                        o["color"] = a.Color;
                    }

                    return o;
                default:
                    throw new ArgumentException(t);
            }
        }

        static void PathAndColor(JsonMap o, ModelAttachment a)
        {
            if (string.IsNullOrEmpty(a.Path) == false)
            {
                o["path"] = a.Path;
            }

            if (a.Color != "ffffffff")
            {
                o["color"] = a.Color;
            }
        }

        static void SequenceField(JsonMap o, ModelAttachment a)
        {
            ModelSequence q = a.Sequence;
            if (q == null)
            {
                return;
            }

            var d = new JsonMap();
            d["count"] = q.Count;
            if (q.Start != 1)
            {
                d["start"] = q.Start;
            }

            if (q.Digits != 0)
            {
                d["digits"] = q.Digits;
            }

            if (q.Setup != 0)
            {
                d["setup"] = q.Setup;
            }

            o["sequence"] = d;
        }

        List<object> Verts(ModelAttachment a)
        {
            List<double> v = a.Vertices;
            if (a.Weighted == false)
            {
                return L(v, x => Vert(x));
            }

            var output = new List<object>();
            int i = 0;
            while (i < v.Count)
            {
                long n = (long)v[i];
                output.Add(Count(n));
                i++;
                for (long j = 0; j < n; j++)
                {
                    output.Add(Count((long)v[i]));
                    output.Add(Vert(v[i + 1]));
                    output.Add(Vert(v[i + 2]));
                    output.Add(Vert(v[i + 3], Weight));
                    i += 4;
                }
            }

            return output;
        }

        // 4.3 은 정점 배열 전체가 float 표기
        object Count(long n) => _v43 ? new JsonNum(n.ToString(CultureInfo.InvariantCulture) + ".0") : (object)n;

        // ------------------------------------------------------------ 이벤트

        object Event(ModelEvent e)
        {
            var o = new JsonMap();
            if (e.Int != 0)
            {
                o["int"] = e.Int;
            }

            if (e.Float != 0)
            {
                o["float"] = N(e.Float);
            }

            if (string.IsNullOrEmpty(e.String) == false)
            {
                o["string"] = e.String;
            }

            if (string.IsNullOrEmpty(e.Audio) == false)
            {
                o["audio"] = e.Audio;
                o["volume"] = N(e.Volume);
                o["balance"] = N(e.Balance);
            }

            return o;
        }

        // ------------------------------------------------------------ 애니메이션

        object Animation(ModelAnimation an)
        {
            var sec = new JsonMap();
            var events = new List<ModelTimeline>();
            foreach (ModelTimeline t in an.Timelines)
            {
                switch (t.Type)
                {
                    case "color":
                    case "attachment":
                    {
                        JsonMap d = sec.GetOrAddMap("slots").GetOrAddMap(t.Slot);
                        if (t.Type == "attachment")
                        {
                            d["attachment"] = AttKeys(t);
                        }
                        else
                        {
                            List<object> keys = ColorKeys(t, out string name);
                            d[name] = keys;
                        }

                        break;
                    }
                    case "rotate":
                    case "translate":
                    case "scale":
                    case "shear":
                        sec.GetOrAddMap("bones").GetOrAddMap(t.Bone)[t.Type] = BoneKeys(t);
                        break;
                    case "ik":
                        sec.GetOrAddMap("ik")[t.Constraint] = IkKeys(t);
                        break;
                    case "transform":
                        sec.GetOrAddMap("transform")[t.Constraint] = TransformKeys(t);
                        break;
                    case "deform":
                        if (AtLeast(4, 1) == false) // 4.0: deform/<스킨>/<슬롯>/<첨부> = 키 목록
                        {
                            sec.GetOrAddMap("deform").GetOrAddMap(t.Skin).GetOrAddMap(t.Slot)[t.Attachment] = DeformKeys(t);
                        }
                        else
                        {
                            sec.GetOrAddMap("attachments").GetOrAddMap(t.Skin).GetOrAddMap(t.Slot).GetOrAddMap(t.Attachment)["deform"] = DeformKeys(t);
                        }

                        break;
                    case "event":
                        events.Add(t);
                        break;
                    case "drawOrder":
                        sec["drawOrder"] = DrawKeys(t);
                        break;
                    case "translatex":
                    case "translatey":
                        sec.GetOrAddMap("bones").GetOrAddMap(t.Bone)[t.Type] = ValueKeys(t, 0, Pos);
                        break;
                    case "sequence":
                        sec.GetOrAddMap("attachments").GetOrAddMap(t.Skin).GetOrAddMap(t.Slot).GetOrAddMap(t.Attachment)["sequence"] = SeqKeys(t);
                        break;
                    case "physicsWind":
                        sec.GetOrAddMap("physics").GetOrAddMap(t.Constraint)["wind"] = ValueKeys(t, 0, Pos);
                        break;
                    case "physicsWindAll":
                        sec.GetOrAddMap("physics").GetOrAddMap(string.Empty)["wind"] = ValueKeys(t, 0, Pos);
                        break;
                    case "sliderMix":
                        sec.GetOrAddMap("slider").GetOrAddMap(t.Constraint)["mix"] = ValueKeys(t, 1, Mix);
                        break;
                    case "pathPosition":
                    case "pathSpacing":
                    case "pathMix":
                    {
                        string name = t.Type == "pathPosition" ? "position" : t.Type == "pathSpacing" ? "spacing" : "mix";
                        sec.GetOrAddMap("path").GetOrAddMap(t.Constraint)[name] = PathKeys(t);
                        break;
                    }
                }
            }

            if (events.Count > 0) // 이벤트 타임라인은 정의마다 따로 있다: 시간순으로 합친다
            {
                sec["events"] = EventKeys(events.SelectMany(t => t.Keys).OrderBy(k => k.Frame).ToList());
            }

            if (_v43 && sec.Contains("bones")) // 4.3 은 뼈도 이름순
            {
                sec["bones"] = ((JsonMap)sec["bones"]).SortedNatural();
            }

            foreach (string k in new[] { "slots", "ik", "transform", "constraints", "path", "physics" })
            {
                if (sec.Contains(k))
                {
                    sec[k] = ((JsonMap)sec[k]).SortedNatural();
                }
            }

            foreach (string name in new[] { "attachments", "deform" })
            {
                if (sec.Contains(name) == false)
                {
                    continue;
                }

                var skins = new JsonMap();
                JsonMap skinsSorted = ((JsonMap)sec[name]).SortedNatural();
                foreach (string sk in skinsSorted.Keys)
                {
                    var slots = new JsonMap();
                    JsonMap slotsSorted = ((JsonMap)skinsSorted[sk]).SortedNatural();
                    foreach (string sl in slotsSorted.Keys)
                    {
                        slots[sl] = ((JsonMap)slotsSorted[sl]).SortedNatural();
                    }

                    skins[sk] = slots;
                }

                sec[name] = skins;
            }

            var output = new JsonMap();
            foreach (string k in new[] { "slots", "bones", "ik", "transform", "path", "physics", "slider", "attachments", "deform", "drawOrder", "events" })
            {
                if (sec.Contains(k))
                {
                    output[k] = sec[k];
                }
            }

            return output;
        }

        JsonMap KeyBase(ModelKey k)
        {
            var o = new JsonMap();
            if (k.Frame != 0)
            {
                o["time"] = TimeOf(k.Frame);
            }

            return o;
        }

        /// <summary>값이 같은 두 키 사이를 계단으로 낼지. IK 키는 mix·softness 만 보고 직선일 때만 계단이다.</summary>
        static bool SameKeyStepped(ModelKey k, ModelKey next, int channels)
        {
            List<double> a = k.Values;
            List<double> b = next.Values;
            if (a.Count == 0 || a.Count != b.Count)
            {
                return false;
            }

            bool ik = a.Count == 5;
            if (channels == 4 || channels == 7) // 색은 8비트 값으로 비교한다(알파 0과 0.0011은 같은 키다)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (Color8(a[i]) != Color8(b[i]))
                    {
                        return false;
                    }
                }
            }
            else
            {
                int count = ik ? Math.Min(2, a.Count) : a.Count;
                for (int i = 0; i < count; i++)
                {
                    if ((Math.Abs(a[i] - b[i]) <= 1e-6 || Ulps(a[i], b[i]) <= 1) == false)
                    {
                        return false;
                    }
                }
            }

            if (k.Curve == "linear")
            {
                return true;
            }

            if (ik)
            {
                return false;
            }

            for (int c = 0; c < channels; c++)
            {
                if ((c < k.Handles.Count && c < next.Handles.Count && Math.Abs(k.Handles[c][3] - a[c]) <= 1e-5 &&
                     Math.Abs(next.Handles[c][1] - b[c]) <= 1e-5) == false)
                {
                    return false;
                }
            }

            return true;
        }

        static int Color8(double x) => (int)Math.Round(x * 255, MidpointRounding.ToEven);

        /// <summary>float32 표현 사이의 간격 수(편집기 계산 오차로 끝 비트만 다른 키는 같은 값으로 본다).</summary>
        static long Ulps(double a, double b) => Math.Abs(UlpKey(a) - UlpKey(b));

        static long UlpKey(double x)
        {
            long n = FloatBits.ToInt((float)x);
            return n >= 0 ? n : -2147483648L - n;
        }

        object CurveOf(List<ModelKey> keys, int i, IReadOnlyList<string> kinds)
        {
            ModelKey k = keys[i];
            if (i == keys.Count - 1)
            {
                return null;
            }

            if (k.Curve == "stepped")
            {
                return "stepped";
            }

            ModelKey next = keys[i + 1];
            if (k.Dense != null && k.Curve == "linear" && k.Dense.Count == next.Dense.Count)
            {
                bool flat = true;
                for (int j = 0; j < k.Dense.Count && flat; j++)
                {
                    flat = Math.Abs(k.Dense[j] - next.Dense[j]) <= 0.0001;
                }

                if (flat)
                {
                    return "stepped";
                }
            }

            if (SameKeyStepped(k, next, kinds.Count))
            {
                return "stepped";
            }

            if (k.Curve == "linear")
            {
                return null;
            }

            double t0 = k.Frame;
            double t1 = next.Frame;
            var output = new List<object>();
            for (int c = 0; c < kinds.Count; c++)
            {
                double v0 = 0.0;
                double v1 = 1.0; // 값 없는 타임라인(변형 등): 곡선은 0→1 비율
                if (k.Values.Count > 0)
                {
                    v0 = k.Values[c];
                    v1 = next.Values[c];
                }

                double[] h0 = c < k.Handles.Count ? k.Handles[c] : null;
                double[] h1 = c < next.Handles.Count ? next.Handles[c] : null;
                double cx1 = t0 + (t1 - t0) / 3;
                double cy1 = v0;
                if (h0 != null && h0[2] != Unset)
                {
                    cx1 = h0[2];
                    cy1 = h0[3];
                }

                double cx2 = t1 - (t1 - t0) / 3;
                double cy2 = v1;
                if (h1 != null && h1[0] != Unset)
                {
                    cx2 = h1[0];
                    cy2 = h1[1];
                }

                output.Add(N(Sec(cx1), CTime));
                output.Add(N(cy1, kinds[c]));
                output.Add(N(Sec(cx2), CTime));
                output.Add(N(cy2, kinds[c]));
            }

            return output;
        }

        static void PutCurve(JsonMap o, object curve)
        {
            if (curve != null)
            {
                o["curve"] = curve;
            }
        }

        List<object> ValueKeys(ModelTimeline t, double fallback, string kind)
        {
            var output = new List<object>();
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                Put(o, "value", k.Values[0], fallback, kind);
                PutCurve(o, CurveOf(t.Keys, i, new[] { kind }));
                output.Add(o);
            }

            return output;
        }

        List<object> SeqKeys(ModelTimeline t)
        {
            var output = new List<object>();
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                string mode = SkeletonModelReader.SequenceModes[(int)k.Values[0]];
                if (mode != "hold")
                {
                    o["mode"] = mode;
                }

                if ((int)k.Values[1] != 0)
                {
                    o["index"] = (int)k.Values[1];
                }

                double prev = i > 0 ? t.Keys[i - 1].Values[2] : 0.0; // 앞 키와 같으면 쓰지 않는다
                if (k.Values[2] != prev)
                {
                    o["delay"] = N(k.Values[2], Time);
                }

                output.Add(o);
            }

            return output;
        }

        List<object> PathKeys(ModelTimeline t)
        {
            ModelConstraint c = Constraint(t.Constraint);
            PathKinds(c, out string pos, out string spc);
            var output = new List<object>();
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                List<double> v = k.Values;
                string[] kinds;
                if (t.Type == "pathMix")
                {
                    Put(o, "mixRotate", v[0], 1, Mix);
                    Put(o, "mixX", v[1], 1, Mix);
                    Put(o, "mixY", v[2], v[1], Mix);
                    kinds = new[] { Mix, Mix, Mix };
                }
                else
                {
                    string kind = t.Type == "pathPosition" ? pos : spc;
                    Put(o, "value", v[0], 0, kind);
                    kinds = new[] { kind };
                }

                PutCurve(o, CurveOf(t.Keys, i, kinds));
                output.Add(o);
            }

            return output;
        }

        List<object> BoneKeys(ModelTimeline t)
        {
            string ty = t.Type;
            double d = ty == "scale" ? 1 : 0;
            string ckind = ty == "scale" ? Mix : Pos;
            var output = new List<object>();
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                string[] kinds;
                if (ty == "rotate")
                {
                    Put(o, "value", k.Values[0], 0);
                    kinds = new[] { Pos };
                }
                else
                {
                    Put(o, "x", k.Values[0], d, ckind);
                    Put(o, "y", k.Values[1], d, ckind);
                    kinds = new[] { ckind, ckind };
                }

                PutCurve(o, CurveOf(t.Keys, i, kinds));
                output.Add(o);
            }

            return output;
        }

        static string ColorText(IEnumerable<double> values) =>
            string.Concat(values.Select(x => Color8(x).ToString("x2", CultureInfo.InvariantCulture)));

        List<object> ColorKeys(ModelTimeline t, out string name)
        {
            ModelSlot slot = _m.Slots.First(s => s.Name == t.Slot);
            bool two = slot.Dark != null;
            var output = new List<object>();
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                string light = ColorText(k.Values.Take(4));
                int channels;
                if (two)
                {
                    o["light"] = light;
                    o["dark"] = ColorText(k.Values.Skip(4).Take(3));
                    channels = 7;
                }
                else
                {
                    o["color"] = light;
                    channels = 4;
                }

                PutCurve(o, CurveOf(t.Keys, i, Enumerable.Repeat(Pos, channels).ToArray()));
                output.Add(o);
            }

            name = two ? "rgba2" : "rgba";
            return output;
        }

        List<object> AttKeys(ModelTimeline t)
        {
            var output = new List<object>();
            foreach (ModelKey k in t.Keys)
            {
                JsonMap o = KeyBase(k);
                if (k.Name != null || AtLeast(4, 1) == false)
                {
                    o["name"] = k.Name;
                }

                output.Add(o);
            }

            return output;
        }

        List<object> IkKeys(ModelTimeline t)
        {
            var output = new List<object>();
            bool two = Constraint(t.Constraint).Bones.Count == 2;
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                List<double> v = k.Values;
                Put(o, "mix", v[0], 1, Mix);
                Put(o, "softness", v[1], 0);
                if (two && v[4] == 0)
                {
                    o["bendPositive"] = false;
                }

                if (v[3] != 0 && two == false) // 값 순서: mix, softness, stretch, compress, bend (차등 표본으로 확인)
                {
                    o["compress"] = true;
                }

                if (v[2] != 0)
                {
                    o["stretch"] = true;
                }

                PutCurve(o, CurveOf(t.Keys, i, new[] { Mix, Pos }));
                output.Add(o);
            }

            return output;
        }

        List<object> TransformKeys(ModelTimeline t)
        {
            var output = new List<object>();
            string[] kinds = Enumerable.Repeat(Mix, 6).ToArray();
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                Mixes(o, TransformUsed(t, k.Values), true);
                PutCurve(o, CurveOf(t.Keys, i, kinds));
                output.Add(o);
            }

            return output;
        }

        /// <summary>4.3: 제약에 없는 속성의 mix 는 쓰지 않는다(런타임 기본값 1로 둬도 쓰이지 않는다).</summary>
        double[] TransformUsed(ModelTimeline t, List<double> values)
        {
            double[] v = values.Take(6).ToArray();
            if (_v43 == false)
            {
                return v;
            }

            ModelConstraint c = Constraint(t.Constraint);
            List<ModelFromProperty> props = c.Properties ?? LegacyProperties(c);
            var have = new HashSet<string>(props.SelectMany(p => p.To).Select(x => x.Name));
            string[] names = { "rotate", "x", "y", "scaleX", "scaleY", "shearY" };
            for (int i = 0; i < 6; i++)
            {
                if (have.Contains(names[i]) == false)
                {
                    v[i] = i == ModelConstraint.MixY ? v[ModelConstraint.MixX] : 1.0;
                }
            }

            return v;
        }

        List<object> DeformKeys(ModelTimeline t)
        {
            ModelAttachment att = FindAttachment(t);
            var output = new List<object>();
            for (int i = 0; i < t.Keys.Count; i++)
            {
                ModelKey k = t.Keys[i];
                JsonMap o = KeyBase(k);
                List<double> vec = DeformVector(att, k);
                // 앞뒤에서 설정 위치와 0.01 이내로 같은 값은 잘라낸다(편집기 규칙, float32 로 비교)
                int start = -1;
                int end = -1;
                int count = Math.Min(vec.Count, att.Setup.Count);
                for (int j = 0; j < count; j++)
                {
                    double s0 = att.Setup[j];
                    if (Math.Abs(SpineNumbers.F32(SpineNumbers.F32(s0 + vec[j]) - s0)) > (double)0.01f)
                    {
                        if (start < 0)
                        {
                            start = j;
                        }

                        end = j + 1;
                    }
                }

                if (start >= 0)
                {
                    if (start != 0)
                    {
                        o["offset"] = start;
                    }

                    var values = new List<object>(end - start);
                    for (int j = start; j < end; j++)
                    {
                        values.Add(N(vec[j], Deform));
                    }

                    o["vertices"] = values;
                }

                PutCurve(o, CurveOf(t.Keys, i, new[] { Pos }));
                output.Add(o);
            }

            return output;
        }

        ModelAttachment FindAttachment(ModelTimeline t)
        {
            foreach (ModelSkin s in _m.Skins)
            {
                if (s.Name == t.Skin)
                {
                    foreach (ModelSkinEntry row in s.Attachments)
                    {
                        if (row.Slot == t.Slot && row.Key == t.Attachment)
                        {
                            return row.Attachment;
                        }
                    }
                }
            }

            throw new SpineFormatException(Loc.T("첨부를 찾을 수 없습니다: {0}/{1}/{2}", t.Skin, t.Slot, t.Attachment));
        }

        static List<double> DeformVector(ModelAttachment att, ModelKey k)
        {
            var vec = new List<double>(att.DenseMap.Count);
            foreach (int j in att.DenseMap)
            {
                vec.Add(j < k.Dense.Count ? k.Dense[j] : 0.0);
            }

            return vec;
        }

        List<object> DrawKeys(ModelTimeline t)
        {
            var index = new Dictionary<string, int>();
            for (int i = 0; i < _m.Slots.Count; i++)
            {
                index[_m.Slots[i].Name] = i; // 이름이 겹치면 뒤의 것(dict 와 같음)
            }

            var output = new List<object>();
            foreach (ModelKey k in t.Keys)
            {
                JsonMap o = KeyBase(k);
                List<KeyValuePair<string, long>> changed = k.Offsets.OrderBy(r => index[r.Key]).ToList();
                if (changed.Count > 0)
                {
                    o["offsets"] = L(changed, r =>
                    {
                        var e = new JsonMap();
                        e["slot"] = r.Key;
                        e["offset"] = r.Value;
                        return e;
                    });
                }

                output.Add(o);
            }

            return output;
        }

        List<object> EventKeys(List<ModelKey> keys)
        {
            var output = new List<object>();
            foreach (ModelKey k in keys)
            {
                JsonMap o = KeyBase(k);
                ModelKeyEvent e = k.Event;
                o["name"] = e.Name;
                ModelEvent data = _m.Events.First(d => d.Name == e.Name);
                if (e.Int != data.Int)
                {
                    o["int"] = e.Int;
                }

                if (e.Float != data.Float)
                {
                    o["float"] = N(e.Float);
                }

                if (e.String != null && e.String != data.String)
                {
                    o["string"] = e.String;
                }

                if (string.IsNullOrEmpty(data.Audio) == false)
                {
                    if (e.Volume != data.Volume)
                    {
                        o["volume"] = N(e.Volume);
                    }

                    if (e.Balance != data.Balance)
                    {
                        o["balance"] = N(e.Balance);
                    }
                }

                output.Add(o);
            }

            return output;
        }

        // ------------------------------------------------------------ 직렬화

        static string Dumps(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case JsonNum num:
                    sb.Append(num.S);
                    break;
                case JsonMap map:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (string key in map.Keys)
                    {
                        if (first == false)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        WriteString(sb, key);
                        sb.Append(':');
                        Write(sb, map[key]);
                    }

                    sb.Append('}');
                    break;
                }
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (first == false)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        Write(sb, item);
                    }

                    sb.Append(']');
                    break;
                }
                default:
                    throw new ArgumentException(value.GetType().Name);
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                if (ch == '"')
                {
                    sb.Append("\\\"");
                }
                else if (ch == '\\')
                {
                    sb.Append("\\\\");
                }
                else if (ch == '\n')
                {
                    sb.Append("\\n");
                }
                else if (ch < 0x20)
                {
                    sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(ch);
                }
            }

            sb.Append('"');
        }
    }
}
