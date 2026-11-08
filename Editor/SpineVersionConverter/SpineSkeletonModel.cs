using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    // 버전에 묶이지 않는 공통 스켈레톤 모델. 각 버전의 리더가 이 모델로 읽고, 라이터가 이 모델에서 쓴다.
    // 원본: Spine-Custom-Editor include/SkeletonData.h (C++). 필드 의미와 기본값을 그대로 옮겼다.

    public enum SpineInherit
    {
        Normal,
        OnlyTranslation,
        NoRotationOrReflection,
        NoScale,
        NoScaleOrReflection,
    }

    public enum SpineBlendMode
    {
        Normal,
        Additive,
        Multiply,
        Screen,
    }

    public enum SpinePositionMode
    {
        Fixed,
        Percent,
    }

    public enum SpineSpacingMode
    {
        Length,
        Fixed,
        Percent,
        Proportional,
    }

    public enum SpineRotateMode
    {
        Tangent,
        Chain,
        ChainScale,
    }

    // 값이 바이너리 플래그의 하위 3비트와 같다 — 순서 변경 금지.
    public enum SpineAttachmentType
    {
        Region,
        BoundingBox,
        Mesh,
        LinkedMesh,
        Path,
        Point,
        Clipping,
    }

    public enum SpineSequenceMode
    {
        Hold,
        Once,
        Loop,
        PingPong,
        OnceReverse,
        LoopReverse,
        PingPongReverse,
    }

    public enum SpineCurveType
    {
        Linear,
        Stepped,
        Bezier,
    }

    public struct SpineColor : IEquatable<SpineColor>
    {
        public static readonly SpineColor White = new SpineColor(0xff, 0xff, 0xff, 0xff);

        // 바이너리는 뼈 색이 없을 때도 이 값을 쓴다 — 읽을 때 이 값이면 "색 없음"으로 되돌린다.
        public static readonly SpineColor DefaultBone = new SpineColor(0x9b, 0x9b, 0x9b, 0xff);

        public byte R;
        public byte G;
        public byte B;
        public byte A;

        public SpineColor(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public bool Equals(SpineColor other)
        {
            return R == other.R && G == other.G && B == other.B && A == other.A;
        }

        public override bool Equals(object obj)
        {
            return obj is SpineColor other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (R << 24) | (G << 16) | (B << 8) | A;
        }

        public static bool operator ==(SpineColor left, SpineColor right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SpineColor left, SpineColor right)
        {
            return left.Equals(right) == false;
        }
    }

    public sealed class SpineSequence
    {
        public int Count;
        public int Start = 1;
        public int Digits;
        public int SetupIndex;
    }

    /// <summary>
    /// UTF-8 바이트 순서로 문자열을 비교한다. 원본 C++은 <c>std::map</c>/<c>std::set</c>(바이트 비교)로 키를 정렬해 쓰므로,
    /// 같은 순서로 써야 원본과 같은 바이너리가 나온다. <see cref="StringComparer.Ordinal"/>은 UTF-16 단위라 한글 등에서 순서가 갈릴 수 있다.
    /// </summary>
    public sealed class SpineUtf8Comparer : IComparer<string>
    {
        public static readonly SpineUtf8Comparer Instance = new SpineUtf8Comparer();

        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x == null)
            {
                return -1;
            }

            if (y == null)
            {
                return 1;
            }

            byte[] left = Encoding.UTF8.GetBytes(x);
            byte[] right = Encoding.UTF8.GetBytes(y);
            int length = Math.Min(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                if (left[i] != right[i])
                {
                    return left[i] - right[i];
                }
            }

            return left.Length - right.Length;
        }
    }

    /// <summary>이름 키를 원본과 같은 순서로 유지하는 사전.</summary>
    public class SpineMap<TValue> : SortedDictionary<string, TValue>
    {
        public SpineMap() : base(SpineUtf8Comparer.Instance)
        {
        }
    }

    public static class SpineMapExtensions
    {
        public static TValue GetOrAdd<TValue>(this SortedDictionary<string, TValue> map, string key) where TValue : new()
        {
            if (map.TryGetValue(key, out TValue value) == false)
            {
                value = new TValue();
                map[key] = value;
            }

            return value;
        }
    }

    /// <summary>타임라인 종류("rotate", "rgba" 등) → 프레임 목록.</summary>
    public sealed class SpineTimelineSet : SpineMap<List<SpineTimelineFrame>>
    {
    }

    /// <summary>
    /// 모든 타임라인이 공유하는 프레임. 원본처럼 범용 슬롯(value1~6, str1~2 등)을 타임라인 종류별 의미로 쓴다.
    /// 예: transform 은 value1~6 = mixRotate·mixX·mixY·mixScaleX·mixScaleY·mixShearY, event 는 str1 = 이벤트 이름.
    /// </summary>
    public sealed class SpineTimelineFrame
    {
        public float Time;
        public string Str1;
        public string Str2;
        public int Int1;
        public float Value1;
        public float Value2;
        public float Value3;
        public float Value4;
        public float Value5;
        public float Value6;
        public SpineColor? Color1;
        public SpineColor? Color2;
        public SpineCurveType CurveType = SpineCurveType.Linear;
        public List<float> Curve = new List<float>();

        public SpineInherit Inherit = SpineInherit.Normal;
        public SpineSequenceMode SequenceMode = SpineSequenceMode.Hold;
        public bool BendPositive = true;
        public bool Compress;
        public bool Stretch;
        public List<float> Vertices = new List<float>();
        public List<KeyValuePair<string, int>> Offsets = new List<KeyValuePair<string, int>>();
    }

    public abstract class SpineAttachment
    {
        public string Name;
        public string Path;

        public abstract SpineAttachmentType Type { get; }
    }

    public sealed class SpineRegionAttachment : SpineAttachment
    {
        public float X;
        public float Y;
        public float Rotation;
        public float ScaleX = 1f;
        public float ScaleY = 1f;
        public float Width = 32f;
        public float Height = 32f;
        public SpineColor? Color;
        public SpineSequence Sequence;

        public override SpineAttachmentType Type => SpineAttachmentType.Region;
    }

    public sealed class SpineBoundingBoxAttachment : SpineAttachment
    {
        public int VertexCount;
        public List<float> Vertices = new List<float>();
        public SpineColor? Color;

        public override SpineAttachmentType Type => SpineAttachmentType.BoundingBox;
    }

    public sealed class SpineMeshAttachment : SpineAttachment
    {
        public float Width = 32f;
        public float Height = 32f;
        public SpineColor? Color;
        public SpineSequence Sequence;

        // 4.3 바이너리 전용: 이 메시의 디폼 타임라인을 함께 받는 다른 슬롯(링크드 메시가 다른 슬롯에 있을 때).
        public List<string> TimelineSlots = new List<string>();
        public int HullLength;
        public List<float> Uvs = new List<float>();
        public List<int> Triangles = new List<int>();
        public List<int> Edges = new List<int>();

        // 가중치 메시는 [뼈 수, (뼈 인덱스, x, y, weight) × 뼈 수] 를 정점마다 이어 붙인 배열이다.
        public List<float> Vertices = new List<float>();

        public override SpineAttachmentType Type => SpineAttachmentType.Mesh;
    }

    public sealed class SpineLinkedMeshAttachment : SpineAttachment
    {
        public float Width = 32f;
        public float Height = 32f;
        public SpineColor? Color;
        public SpineSequence Sequence;

        // 4.3 전용: 원본 메시가 있는 슬롯. null 이면 자기 슬롯.
        public string SourceSlot;
        public string ParentMesh;
        public int Timelines = 1;

        // 바이너리를 읽는 동안만 쓰는 임시 값 — 스킨 목록을 다 읽은 뒤 Skin 이름으로 바꾼다.
        public int SkinIndex = -1;
        public string Skin;

        public override SpineAttachmentType Type => SpineAttachmentType.LinkedMesh;
    }

    public sealed class SpinePathAttachment : SpineAttachment
    {
        public int VertexCount;
        public List<float> Vertices = new List<float>();
        public List<float> Lengths = new List<float>();
        public bool Closed;
        public bool ConstantSpeed = true;
        public SpineColor? Color;

        public override SpineAttachmentType Type => SpineAttachmentType.Path;
    }

    public sealed class SpinePointAttachment : SpineAttachment
    {
        public float X;
        public float Y;
        public float Rotation;
        public SpineColor? Color;

        public override SpineAttachmentType Type => SpineAttachmentType.Point;
    }

    public sealed class SpineClippingAttachment : SpineAttachment
    {
        // 4.3 전용 플래그.
        public bool Convex;
        public bool Inverse;
        public int VertexCount;
        public List<float> Vertices = new List<float>();
        public string EndSlot;
        public SpineColor? Color;

        public override SpineAttachmentType Type => SpineAttachmentType.Clipping;
    }

    public sealed class SpineBone
    {
        public float IconSize = 1f;
        public float IconRotation;
        public string Name;
        public string Parent;
        public float Length;
        public float X;
        public float Y;
        public float Rotation;
        public float ScaleX = 1f;
        public float ScaleY = 1f;
        public float ShearX;
        public float ShearY;
        public SpineInherit Inherit = SpineInherit.Normal;
        public bool SkinRequired;
        public SpineColor? Color;
        public string Icon;
        public bool Visible = true;
    }

    public sealed class SpineSlot
    {
        public string Name;
        public string Bone;
        public SpineColor? Color;
        public SpineColor? DarkColor;
        public string AttachmentName;
        public SpineBlendMode BlendMode = SpineBlendMode.Normal;
        public bool Visible = true;
    }

    public sealed class SpineIkConstraint
    {
        public string Name;
        public int Order;
        public bool SkinRequired;
        public List<string> Bones = new List<string>();
        public string Target;
        public bool BendPositive = true;
        public bool Compress;
        public bool Stretch;
        public bool Uniform;
        public float Mix = 1f;
        public float Softness;
    }

    public sealed class SpineTransformConstraint
    {
        public string Name;
        public int Order;
        public bool SkinRequired;
        public List<string> Bones = new List<string>();
        public string Target;
        public float MixRotate = 1f;
        public float MixX = 1f;
        public float MixY = 1f;
        public float MixScaleX = 1f;
        public float MixScaleY = 1f;
        public float MixShearY = 1f;
        public float OffsetRotation;
        public float OffsetX;
        public float OffsetY;
        public float OffsetScaleX;
        public float OffsetScaleY;
        public float OffsetShearY;
        public bool Relative;
        public bool Local;
    }

    public sealed class SpinePathConstraint
    {
        public string Name;
        public int Order;
        public bool SkinRequired;
        public List<string> Bones = new List<string>();
        public string Target;
        public SpinePositionMode PositionMode = SpinePositionMode.Percent;
        public SpineSpacingMode SpacingMode = SpineSpacingMode.Length;
        public SpineRotateMode RotateMode = SpineRotateMode.Tangent;
        public float OffsetRotation;
        public float Position;
        public float Spacing;
        public float MixRotate = 1f;
        public float MixX = 1f;
        public float MixY = 1f;
    }

    public sealed class SpinePhysicsConstraint
    {
        public string Name;
        public int Order;
        public bool SkinRequired;
        public string Bone;
        public float X;
        public float Y;
        public float Rotate;
        public float ScaleX;
        public float ShearX;
        public float Limit = 5000f;
        public float Fps = 60f;
        public float Inertia = 1f;
        public float Strength = 100f;
        public float Damping = 1f;
        public float Mass = 1f;
        public float Wind;
        public float Gravity;
        public float Mix = 1f;
        public bool InertiaGlobal;
        public bool StrengthGlobal;
        public bool DampingGlobal;
        public bool MassGlobal;
        public bool WindGlobal;
        public bool GravityGlobal;
        public bool MixGlobal;
    }

    public sealed class SpineSkin
    {
        // 4.3 전용: 스킨에 속한 slider 제약조건 이름.
        public List<string> Slider = new List<string>();
        public string Name = string.Empty;

        // 슬롯 이름 → (스킨 플레이스홀더 이름 → 어태치먼트).
        public SpineMap<SpineMap<SpineAttachment>> Attachments = new SpineMap<SpineMap<SpineAttachment>>();
        public List<string> Bones = new List<string>();
        public List<string> Ik = new List<string>();
        public List<string> Transform = new List<string>();
        public List<string> Path = new List<string>();
        public List<string> Physics = new List<string>();

        /// <summary>
        /// 바이너리가 스킨을 «인덱스»로 가리킬 때(디폼 타임라인·4.x 링크드 메시) 기준이 되는 표.
        /// 3.8·4.2·4.3 런타임 모두 어태치먼트가 없는 기본 스킨을 읽으면 null 을 돌려 스킨 표에 넣지 않는다
        /// (SkeletonBinary.ReadSkin: slotCount == 0 → return null) — 그래서 인덱스 0 은 첫 이름 있는 스킨이다.
        /// 모델은 기본 스킨을 늘 갖고 있으므로 모델 순서로 세면 한 칸 밀린다(실측: anim_char_frog 를
        /// 4.2 로 올리면 링크드 메시가 다른 스킨의 같은 이름 어태치먼트를 부모로 잡아 4.2 런타임이 NRE 로 죽었다).
        /// 원본 툴도 모델 순서로 센다 — 의도한 차이다.
        /// </summary>
        public static List<SpineSkin> BinaryIndexTable(List<SpineSkin> skins)
        {
            var table = new List<SpineSkin>(skins.Count);
            foreach (SpineSkin skin in skins)
            {
                if (skin.Name == "default" && skin.Attachments.Count == 0)
                {
                    continue;
                }

                table.Add(skin);
            }

            return table;
        }
        public SpineColor? Color;
    }

    public sealed class SpineEvent
    {
        public string Name = string.Empty;
        public int IntValue;
        public float FloatValue;
        public string StringValue;
        public string AudioPath;
        public float Volume = 1f;
        public float Balance;
    }

    public sealed class SpineAnimation
    {
        // 4.3 전용 데이터.
        public SpineMap<SpineTimelineSet> Slider = new SpineMap<SpineTimelineSet>();
        public JArray DrawOrderFolder = new JArray();
        public SpineColor? Color;

        public string Name = string.Empty;
        public SpineMap<SpineTimelineSet> Slots = new SpineMap<SpineTimelineSet>();
        public SpineMap<SpineTimelineSet> Bones = new SpineMap<SpineTimelineSet>();
        public SpineMap<List<SpineTimelineFrame>> Ik = new SpineMap<List<SpineTimelineFrame>>();
        public SpineMap<List<SpineTimelineFrame>> Transform = new SpineMap<List<SpineTimelineFrame>>();
        public SpineMap<SpineTimelineSet> Path = new SpineMap<SpineTimelineSet>();

        // 키 "" 는 특정 제약조건이 아니라 전체 physics 에 거는 타임라인이다.
        public SpineMap<SpineTimelineSet> Physics = new SpineMap<SpineTimelineSet>();

        // 스킨 → 슬롯 → 어태치먼트 → (deform | sequence).
        public SpineMap<SpineMap<SpineMap<SpineTimelineSet>>> Attachments = new SpineMap<SpineMap<SpineMap<SpineTimelineSet>>>();
        public List<SpineTimelineFrame> DrawOrder = new List<SpineTimelineFrame>();
        public List<SpineTimelineFrame> Events = new List<SpineTimelineFrame>();
    }

    public sealed class SpineSkeleton
    {
        // 4.3 의 통합 제약조건 배열(순서가 곧 적용 순서). 4.3 JSON 과 같은 모양으로 들고 있다.
        // 4.2 에서 읽었으면 비어 있고, 4.3 으로 쓸 때 레거시 제약조건 목록에서 만들어 낸다.
        public JArray Constraints43 = new JArray();
        public ulong Hash;

        // 3.8 전용: 3.8 은 해시를 base64 문자열(보통 SHA-1 20바이트)로 쓴다. Hash 는 그 마지막 8바이트다.
        // 3.8 로 다시 쓸 때 원문을 보존하려고 들고 있다. 4.x 에서 읽었으면 null(3.8 로 쓸 때 Hash 로 만든다).
        public string HashString;
        public string Version;
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public float ReferenceScale = 100f;
        public bool Nonessential;
        public float Fps = 30f;
        public string ImagesPath;
        public string AudioPath;

        // 바이너리 문자열 참조 테이블. 쓸 때마다 다시 만든다.
        public List<string> Strings = new List<string>();
        public List<SpineBone> Bones = new List<SpineBone>();
        public List<SpineSlot> Slots = new List<SpineSlot>();
        public List<SpineIkConstraint> IkConstraints = new List<SpineIkConstraint>();
        public List<SpineTransformConstraint> TransformConstraints = new List<SpineTransformConstraint>();
        public List<SpinePathConstraint> PathConstraints = new List<SpinePathConstraint>();
        public List<SpinePhysicsConstraint> PhysicsConstraints = new List<SpinePhysicsConstraint>();
        public List<SpineSkin> Skins = new List<SpineSkin>();
        public List<SpineEvent> Events = new List<SpineEvent>();
        public List<SpineAnimation> Animations = new List<SpineAnimation>();
    }
}
