using System;
using System.Collections.Generic;
using Spine;
using Spine.Unity;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 스켈레톤 미리보기 렌더러. 보이지 않는 미리보기 씬(PreviewRenderUtility)에 SkeletonAnimation 을 두고,
    /// 정한 애니메이션·시각의 포즈를 RenderTexture 로 그린다.
    /// 🚨 미리보기 씬·렌더링은 <see cref="RenderNow"/> 안에서만 만든다 — 그것은 EditorApplication.update 에서만 부른다
    /// (UI 이벤트 안이나 도메인 리로드 직후에 카메라를 그리지 않도록).
    /// </summary>
    sealed class SkeletonPreview : IDisposable
    {
        PreviewRenderUtility _util;
        SkeletonAnimation _skeleton;
        RenderTexture _texture;
        SkeletonDataAsset _built;
        string _animation;
        Bounds _bounds;
        Color _background = Color.gray;

        /// <summary>체크보드 한 칸의 화면 크기(포인트). 확대와 상관없이 일정하다.</summary>
        const float CheckerCell = 16f;
        GameObject _checker;
        Material _checkerMaterial;
        Texture2D _checkerTexture;
        Color _checkerA = Color.gray;
        Color _checkerB = Color.gray;

        string _skin;

        public SkeletonDataAsset Asset { get; private set; }

        /// <summary>고를 수 있는 스킨 이름들.</summary>
        public IReadOnlyList<string> SkinNames
        {
            get
            {
                var names = new List<string>();
                if (Data != null)
                {
                    foreach (Skin skin in Data.Skins)
                    {
                        names.Add(skin.Name);
                    }
                }

                return names;
            }
        }

        /// <summary>미리보기에 입힐 스킨. 바꾸면 다음 그리기 때 화면 틀까지 다시 잡는다.</summary>
        public string SkinName
        {
            get => _skin;
            set
            {
                if (value != _skin)
                {
                    _skin = value;
                    _animation = null;
                }
            }
        }

        /// <summary>확대 배율(1 = 처음 보기: 애니메이션 전체가 화면에 맞게).</summary>
        public float Zoom { get; set; } = 1f;

        /// <summary>그릴 수 있는 데이터가 있는가(아틀라스·텍스처까지 읽힘).</summary>
        public bool HasData => Data != null;

        SkeletonData Data => Asset != null ? Asset.GetSkeletonData(true) : null;

        public void SetAsset(SkeletonDataAsset asset)
        {
            if (asset != Asset)
            {
                if (asset == null)
                {
                    DestroySkeleton(); // 에셋을 지울 수 있게, 그 에셋으로 만든 미리보기 객체를 먼저 없앤다
                }

                Asset = asset;
                _animation = null;
                _skin = InitialSkin();
            }
        }

        /// <summary>처음 입힐 스킨: 기본 스킨이 장식 몇 개뿐이고 몸 전체는 다른 스킨에 있으면 그 첫 스킨을 고른다.</summary>
        string InitialSkin()
        {
            SkeletonData data = Data;
            if (data == null)
            {
                return null;
            }

            Skin main = data.DefaultSkin;
            int own = main?.Attachments.Count ?? 0;
            foreach (Skin skin in data.Skins)
            {
                if (skin != main && skin.Attachments.Count > own * 2)
                {
                    return skin.Name;
                }
            }

            return main?.Name;
        }

        /// <summary>애니메이션 길이(초). 없는 애니메이션이면 0.</summary>
        public float Duration(string animation) => Find(animation)?.Duration ?? 0f;

        public void SetBackground(Color color) => _background = color;

        public void SetChecker(Color a, Color b)
        {
            _checkerA = a;
            _checkerB = b;
            PaintChecker();
        }

        void PaintChecker()
        {
            if (_checkerTexture != null)
            {
                _checkerTexture.SetPixels(new[] { _checkerA, _checkerB, _checkerB, _checkerA });
                _checkerTexture.Apply(false);
            }
        }

        /// <summary>
        /// 스켈레톤 뒤, 카메라가 보는 영역 전체를 덮는 체크보드 판(미리보기 씬 안의 쿼드).
        /// 같은 카메라로 그리므로 스켈레톤의 반투명이 체크보드 위에 그대로 합성된다.
        /// </summary>
        void EnsureChecker()
        {
            if (_checker != null)
            {
                return;
            }

            _checkerTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave };
            PaintChecker();
            _checkerMaterial = new Material(Shader.Find("Unlit/Texture")) { mainTexture = _checkerTexture, hideFlags = HideFlags.HideAndDontSave };
            _checker = EditorUtility.CreateGameObjectWithHideFlags("Spine Event Injector Checker", HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer));
            _checker.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            _checker.GetComponent<MeshRenderer>().sharedMaterial = _checkerMaterial;
            _util.AddSingleGO(_checker);
        }

        void PlaceChecker(Camera camera, int width, int height, float aspect)
        {
            float worldHeight = camera.orthographicSize * 2f;
            Vector3 cameraPosition = camera.transform.position;
            _checker.transform.position = new Vector3(cameraPosition.x, cameraPosition.y, 50f);
            _checker.transform.localScale = new Vector3(worldHeight * aspect, worldHeight, 1f);
            // 무늬는 화면 왼쪽 위 기준: 판 높이가 바뀌어도(위쪽은 고정) 칸이 움직이지 않는다
            var tiling = new Vector2(width / (CheckerCell * 2f), height / (CheckerCell * 2f));
            _checkerMaterial.mainTextureScale = tiling;
            _checkerMaterial.mainTextureOffset = new Vector2(0f, -tiling.y);
        }

        /// <summary>애니메이션을 그 시각의 포즈로 두고 width×height(포인트)로 그린다. EditorApplication.update 에서만 부른다.
        /// 돌려주는 텍스처는 PreviewRenderUtility 소유다(파괴하지 않는다).</summary>
        public Texture RenderNow(string animation, float seconds, int width, int height)
        {
            if (width < 2 || height < 2 || EditorApplication.isCompiling || EditorApplication.isUpdating || EnsureBuilt() == false)
            {
                return null;
            }

            Spine.Animation clip = Find(animation);
            if (clip == null)
            {
                return null;
            }

            if (animation != _animation)
            {
                _animation = animation;
                ApplySkin();
                _skeleton.AnimationState.ClearTracks();
                _skeleton.Skeleton.SetupPose();
                _skeleton.AnimationState.SetAnimation(0, clip, true);
                _bounds = MeasureBounds(animation);
            }

            Pose(seconds);
            Camera camera = _util.camera;
            float aspect = (float)width / height;
            Vector3 size = _bounds.size;
            camera.backgroundColor = _background;
            camera.orthographicSize = Mathf.Max(size.y * 0.5f, size.x * 0.5f / aspect, 0.1f) * 1.15f / Mathf.Max(Zoom, 0.01f);
            camera.transform.position = new Vector3(_bounds.center.x, _bounds.center.y, -10f);
            camera.aspect = aspect;
            PlaceChecker(camera, width, height, aspect);

            // 🚨 Render() 는 에디터의 조명 설정을 미리보기 씬으로 돌려놓고(SetOverrideLightingSettings) 되돌리지 않는다 —
            // 되돌리는 것은 EndPreview() 다. 둘 없이 Render() 만 부르면 미리보기 씬이 닫힌 뒤(도메인 리로드 등)
            // SceneView.OnEnable 이 «RenderSettings is NULL» Fatal Error 로 에디터를 죽인다(2026-11-08 실측).
            // 그래서 반드시 BeginPreview → Render → EndPreview 를 짝지어, 예외가 나도 EndPreview 가 돌게 한다.
            _util.BeginPreview(new Rect(0f, 0f, width, height), GUIStyle.none);
            try
            {
                // OnGUI 밖에서는 BeginPreview 가 카메라 그림 영역(pixelRect)을 새 텍스처 크기로 맞추지 않는다
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.aspect = aspect;
                _util.Render(true, false);
            }
            finally
            {
                _texture = _util.EndPreview() as RenderTexture;
            }

            return _texture;
        }

        bool EnsureBuilt()
        {
            if (Asset == null || Data == null)
            {
                return false;
            }

            if (_built == Asset && _skeleton != null)
            {
                return true;
            }

            DestroySkeleton();
            if (_util == null)
            {
                _util = new PreviewRenderUtility(true);
                _util.camera.orthographic = true;
                _util.camera.nearClipPlane = 0.01f;
                _util.camera.farClipPlane = 1000f;
                _util.camera.clearFlags = CameraClearFlags.SolidColor;
            }

            EnsureChecker();

            // HideAndDontSave 로 만들어 열린 씬을 «바뀜»으로 만들지 않는다
            GameObject go = EditorUtility.CreateGameObjectWithHideFlags("Spine Event Injector Preview", HideFlags.HideAndDontSave,
                typeof(MeshFilter), typeof(MeshRenderer), typeof(SkeletonRenderer), typeof(SkeletonAnimation));
            _skeleton = go.GetComponent<SkeletonAnimation>();
            _skeleton.skeletonDataAsset = Asset;
            _skeleton.Initialize(true);
            _util.AddSingleGO(go);
            _built = Asset;
            _animation = null;
            return _skeleton.IsValid;
        }

        /// <summary>
        /// 애니메이션을 찾는다. .spine 프로젝트의 목록은 이름만 갖지만 내보낸 데이터는 «폴더/이름»으로 부른다.
        /// 다른 폴더에 같은 이름이 있어 하나로 정할 수 없으면 찾지 못한 것으로 둔다.
        /// </summary>
        Spine.Animation Find(string name)
        {
            SkeletonData data = Data;
            if (data == null || name == null)
            {
                return null;
            }

            Spine.Animation exact = data.FindAnimation(name);
            if (exact != null)
            {
                return exact;
            }

            string suffix = "/" + name;
            Spine.Animation found = null;
            foreach (Spine.Animation candidate in data.Animations)
            {
                if (candidate.Name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    if (found != null)
                    {
                        return null;
                    }

                    found = candidate;
                }
            }

            return found;
        }

        void ApplySkin()
        {
            Spine.Skeleton skeleton = _skeleton.Skeleton;
            Skin wanted = _skin != null ? skeleton.Data.FindSkin(_skin) : null;
            if (wanted != skeleton.Skin)
            {
                skeleton.SetSkin(wanted);
                skeleton.SetupPoseSlots();
            }
        }

        void Pose(float seconds)
        {
            TrackEntry entry = _skeleton.AnimationState.GetTrack(0);
            entry.TrackTime = seconds;
            _skeleton.Skeleton.SetupPose();
            _skeleton.Update(0f);
            _skeleton.Renderer.LateUpdate();
        }

        /// <summary>애니메이션 몇 군데의 메시 범위를 합쳐 카메라 틀을 잡는다(재생 중 화면이 흔들리지 않게).</summary>
        Bounds MeasureBounds(string animation)
        {
            float duration = Duration(animation);
            Bounds? total = null;
            var renderer = _skeleton.GetComponent<MeshRenderer>();
            var filter = _skeleton.GetComponent<MeshFilter>();
            for (int i = 0; i <= 4; i++)
            {
                Pose(duration * i / 4f);
                if (filter.sharedMesh == null || filter.sharedMesh.vertexCount == 0)
                {
                    continue;
                }

                Bounds b = renderer.bounds;
                if (total == null)
                {
                    total = b;
                }
                else
                {
                    Bounds t = total.Value;
                    t.Encapsulate(b);
                    total = t;
                }
            }

            return total ?? new Bounds(Vector3.zero, Vector3.one * 2f);
        }

        void DestroySkeleton()
        {
            if (_skeleton != null)
            {
                Object.DestroyImmediate(_skeleton.gameObject);
                _skeleton = null;
            }

            _built = null;
        }

        public void Dispose()
        {
            DestroySkeleton();
            _texture = null;
            if (_checker != null)
            {
                Object.DestroyImmediate(_checker);
                _checker = null;
            }

            if (_checkerMaterial != null)
            {
                Object.DestroyImmediate(_checkerMaterial);
                _checkerMaterial = null;
            }

            if (_checkerTexture != null)
            {
                Object.DestroyImmediate(_checkerTexture);
                _checkerTexture = null;
            }

            _util?.Cleanup();
            _util = null;
            Asset = null;
        }
    }
}
