using System.Collections.Generic;

namespace NKStudio.SpineEventInjector.Export
{
    /// <summary>
    /// 버전 중립 스켈레톤 모델(Spine 4.2 JSON 과 같은 이름). 값은 반올림하지 않고 기본값도 모두 채운다.
    /// 무엇을 생략할지는 출력기가 버전 규칙대로 정한다. 시간은 프레임 단위 그대로 둔다.
    /// </summary>
    internal sealed class SkeletonModel
    {
        public string Name;
        public string Format;
        public double Fps;
        public string Images;
        public string Audio;
        public double ReferenceScale;
        public List<double> Bounds;
        public List<ModelBone> Bones = new List<ModelBone>();
        public List<ModelSlot> Slots = new List<ModelSlot>();
        public List<ModelConstraint> Constraints = new List<ModelConstraint>();
        public List<ModelSkin> Skins = new List<ModelSkin>();
        public List<ModelEvent> Events = new List<ModelEvent>();
        public List<ModelAnimation> Animations = new List<ModelAnimation>();
    }

    internal sealed class ModelBone
    {
        public string Name;
        public string Parent;
        public double Length;
        public double X;
        public double Y;
        public double Rotation;
        public double ScaleX;
        public double ScaleY;
        public double ShearX;
        public double ShearY;
        public string Inherit;
        public string Color;
        public string Icon;
        public bool Skin;
    }

    internal sealed class ModelSlot
    {
        public string Name;
        public string Bone;
        public string Color;
        public string Dark;
        public string Attachment;
        public string Blend;
        public bool Visible;
    }

    /// <summary>변형 제약의 대상 속성.</summary>
    internal sealed class ModelToProperty
    {
        public string Name;
        public double Offset;
        public double Max;
        public double Scale;
    }

    /// <summary>변형 제약의 입력 속성과 그 대상들.</summary>
    internal sealed class ModelFromProperty
    {
        public string Name;
        public double Offset;
        public List<ModelToProperty> To = new List<ModelToProperty>();
    }

    /// <summary>제약 하나. 종류별로 쓰는 필드만 채운다.</summary>
    internal sealed class ModelConstraint
    {
        // Mixes 배열의 자리
        public const int MixRotate = 0;
        public const int MixX = 1;
        public const int MixY = 2;
        public const int MixScaleX = 3;
        public const int MixScaleY = 4;
        public const int MixShearY = 5;
        public static readonly string[] MixNames = { "mixRotate", "mixX", "mixY", "mixScaleX", "mixScaleY", "mixShearY" };

        public string Type;
        public string Name;
        public int Order;
        public bool Skin;
        public List<string> Bones = new List<string>();
        public string Target;
        public double[] Mixes = new double[6];

        // ik · physics · slider 공통
        public double Mix;

        // ik
        public double Softness;
        public bool BendPositive;
        public bool Compress;
        public bool Stretch;
        public bool Uniform;

        // transform (X·Y·ScaleX·ScaleY·Rotation 은 physics, path 도 함께 쓴다)
        public double Rotation;
        public double X;
        public double Y;
        public double ScaleX;
        public double ScaleY;
        public double ShearY;
        public bool Local;
        public bool Relative;
        public List<ModelFromProperty> Properties;

        // path
        public string SpacingMode;
        public string RotateMode;
        public string PositionMode;
        public double Position;
        public double Spacing;

        // physics
        public string Bone;
        public double Rotate;
        public double ShearX;
        public double Limit;
        public double Fps;
        public double Inertia;
        public double Strength;
        public double Damping;
        public double Mass;
        public double Wind;
        public double Gravity;
        public bool InertiaGlobal;
        public bool StrengthGlobal;
        public bool DampingGlobal;
        public bool MassGlobal;
        public bool WindGlobal;
        public bool GravityGlobal;
        public bool MixGlobal;

        // slider
        public string Animation;
        public string Property;
        public bool Loop;
        public double Max;
        public double Scale;
        public double Offset;
    }

    internal sealed class ModelSkin
    {
        public string Name;
        public List<string> Bones = new List<string>();
        public List<string> Constraints = new List<string>();
        public List<ModelSkinEntry> Attachments = new List<ModelSkinEntry>();
    }

    internal sealed class ModelSkinEntry
    {
        public string Slot;
        public string Key;
        public ModelAttachment Attachment;
    }

    internal sealed class ModelSequence
    {
        public long Count;
        public long Start;
        public long Digits;
        public long Setup;
    }

    /// <summary>첨부 하나. Type: region · mesh · linkedmesh · path · boundingbox · clipping.</summary>
    internal sealed class ModelAttachment
    {
        public string Name;
        public string Key;
        public string Type;
        public string Path = string.Empty;
        public string Color;
        public ModelSequence Sequence;

        // region
        public double X;
        public double Y;
        public double ScaleX;
        public double ScaleY;
        public double Rotation;
        public double Width;
        public double Height;

        // 정점 첨부 공통: 런타임 형식 정점 배열, 조밀한(편집기) 배열 → 런타임 순서 변환표, 설정 위치
        public List<double> Vertices;
        public bool Weighted;
        public List<int> DenseMap;
        public List<double> Setup;
        public int VertexCount;

        // path
        public bool Closed;
        public bool ConstantSpeed;
        public List<double> Lengths;

        // clipping
        public string End;
        public bool Convex;

        // linkedmesh
        public string Parent;
        public string Skin;
        public bool Timelines;

        // mesh
        public List<double> Uvs;
        public List<long> Triangles;
        public List<long> Edges;
        public int Hull;
    }

    internal sealed class ModelEvent
    {
        public string Name;
        public long Int;
        public double Float;
        public string String;
        public string Audio;
        public double Volume;
        public double Balance;
    }

    internal sealed class ModelAnimation
    {
        public string Name;
        public List<ModelTimeline> Timelines = new List<ModelTimeline>();
    }

    internal sealed class ModelTimeline
    {
        public string Type;
        public int Code;
        public string Bone;
        public string Slot;
        public string Constraint;
        public string Attachment;
        public string Skin;
        public List<ModelKey> Keys = new List<ModelKey>();
    }

    /// <summary>이벤트 키의 값(스켈레톤 정의와 다를 때만 JSON 에 쓴다).</summary>
    internal sealed class ModelKeyEvent
    {
        public string Name;
        public long Int;
        public double Float;
        public string String;
        public double Volume;
        public double Balance;
    }

    internal sealed class ModelKey
    {
        public double Frame;
        public string Curve;
        public List<double> Values;
        /// <summary>채널마다 곡선 손잡이 4개.</summary>
        public List<double[]> Handles;
        public string Name;
        /// <summary>변형 키의 조밀한 값(변형 키가 아니면 null).</summary>
        public List<double> Dense;
        public ModelKeyEvent Event;
        public List<KeyValuePair<string, long>> Offsets;
    }
}
