using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NKStudio.SpineEventInjector.Project;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 창의 동작: 열기(바뀐 것이 있으면 묻기), 미리보기 준비, 적용하기, 원본 연결·반영, Unity 이벤트 가져오기.
    /// 대화상자와 파일 입출력은 여기서만 한다.
    /// </summary>
    sealed class InjectorActions
    {
        const string LastOpenedKey = "NKStudio.SpineEventInjector.LastOpened";
        const string WorkingKey = "NKStudio.SpineEventInjector.Working";
        const string SkeletonKey = "NKStudio.SpineEventInjector.Skeleton";

        readonly InjectorSession _session;
        readonly SkeletonPreview _preview;
        Task<ExternalSpinePreview.Export> _exporting;
        EventSource _exportFor;
        Task<EventSource> _opening;
        string _openingPath;
        Action _opened;

        /// <summary>미리보기 렌더러의 대상이 바뀌었다(준비 완료 또는 없음). 인자: 화면에 띄울 안내(없으면 null).</summary>
        public event Action<string> PreviewChanged;

        /// <summary>창이 있을 때 .spine 쓰기 경고를 창 안에 띄운다(경로, 끝나면 부를 콜백). 없으면 시스템 대화상자로 묻는다.</summary>
        public Action<string, Action<bool>> SpineWriteConfirm;

        public InjectorActions(InjectorSession session, SkeletonPreview preview)
        {
            _session = session;
            _preview = preview;
        }

        List<MecanimEventImporter.Found> _unityEvents = new List<MecanimEventImporter.Found>();

        /// <summary>Mecanim 클립에 있는데 작업본에는 아직 없는 Unity 이벤트 수.</summary>
        public int UnityEventCount => _session.Source == null ? 0 : MecanimEventImporter.Pending(_unityEvents, _session.Source.Working).Count;

        // ------------------------------------------------------------ 열기

        /// <summary>바뀐 것이 있으면 적용·버리기·취소를 묻는다. 계속해도 되면 true.</summary>
        public bool ConfirmLeave()
        {
            EventSource source = _session.Source;
            if (source == null || source.IsDirty == false)
            {
                return true;
            }

            int choice = EditorUtility.DisplayDialogComplex(Loc.T("적용하지 않은 변경"),
                Loc.T("{0}에 적용하지 않은 이벤트 변경이 있습니다.\n{1}", source.DisplayName, EventFileWriter.Describe(EventSetDiff.Compute(source.Original, source.Working))),
                Loc.T("적용하기"), Loc.T("버리기"), Loc.T("취소"));
            if (choice == 0)
            {
                return Apply();
            }

            return choice == 1;
        }

        public void OpenAsset(Object asset)
        {
            if (asset == null || EventSourceLoader.CanOpen(asset) == false)
            {
                _session.Report(Loc.T("Spine 스켈레톤 데이터(SkeletonDataAsset · .skel.bytes · .json)나 .spine 이 아닙니다"), StatusKind.Warning);
                return;
            }

            string path = AssetDatabase.GetAssetPath(asset);
            if (EventSourceLoader.IsSpineProjectPath(path))
            {
                OpenPath(Path.GetFullPath(path));
                return;
            }

            Open(() => EventSourceLoader.FromAsset(asset));
        }

        /// <summary>열린 것을 닫고 창을 비운다(적용하지 않은 변경은 묻는다). 다시 열릴 때 되살리지도 않는다.</summary>
        public void Close()
        {
            if (_session.Source == null)
            {
                return;
            }

            if (ConfirmLeave() == false)
            {
                _session.NotifySourceInfoChanged(); // 비워진 칸에 원래 에셋을 다시 보인다
                return;
            }

            Clear();
        }

        /// <summary>창을 처음 화면(아무것도 열지 않은 상태)으로 되돌린다. 묻지 않는다.</summary>
        void Clear()
        {
            _session.Open(null);
            _unityEvents = new List<MecanimEventImporter.Found>();
            _preview.SetAsset(null);
            SessionState.EraseString(LastOpenedKey);
            SessionState.EraseString(WorkingKey);
            PreviewChanged?.Invoke(null);
            _session.Report(string.Empty);
        }

        /// <summary>
        /// 파일을 연다. .spine 은 본문이 커서(수 MB) 백그라운드에서 읽고, 다 읽으면 창에 띄운다(그동안 에디터가 멈추지 않는다).
        /// opened: 열기에 성공한 뒤 부른다.
        /// </summary>
        public void OpenPath(string fullPath, Action opened = null)
        {
            if (EventSourceLoader.IsSpineProjectPath(fullPath) == false)
            {
                if (Open(() => EventSourceLoader.FromPath(fullPath)))
                {
                    opened?.Invoke();
                }

                return;
            }

            if (ConfirmLeave() == false)
            {
                return;
            }

            // 에디터 API 가 필요한 값은 여기(메인 스레드)서 구해 넘긴다
            IReadOnlyList<SpineProjectSchema> schemas = SchemaLibrary.All;
            string assetPath = EventSourceLoader.ToAssetPath(fullPath);
            // 읽기 전에 지금 미리보기를 먼저 내린다(새 파일을 읽는 동안 이전 스켈레톤이 보이지 않게).
            // 이전 대상의 미리보기 내보내기가 돌고 있으면 그 결과도 버린다.
            EditorApplication.update -= PollExport;
            _exporting = null;
            _exportFor = null;
            _preview.SetAsset(null);

            _openingPath = fullPath;
            _opened = opened;
            _opening = Task.Run(() => EventSourceLoader.FromSpineProject(fullPath, assetPath, schemas));
            _session.Report(Loc.T("{0} 여는 중…", Path.GetFileName(fullPath)));
            PreviewChanged?.Invoke(null);
            EditorApplication.update -= PollOpen;
            EditorApplication.update += PollOpen;
        }

        /// <summary>.spine 을 백그라운드에서 여는 중인가.</summary>
        public bool OpenBusy => _opening != null;

        void PollOpen()
        {
            if (_opening == null)
            {
                EditorApplication.update -= PollOpen;
                return;
            }

            if (_opening.IsCompleted == false)
            {
                return;
            }

            EditorApplication.update -= PollOpen;
            Task<EventSource> task = _opening;
            string path = _openingPath;
            Action opened = _opened;
            _opening = null;
            _openingPath = null;
            _opened = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                // 열기 전에 이미 지금 대상을 떠나기로 했으므로 처음 화면으로 돌아간다
                Clear();
                _session.Report(task.Exception?.GetBaseException().Message ?? Loc.T("열지 못했습니다"), StatusKind.Error);
                return;
            }

            if (ConfirmLeave() == false) // 읽는 동안 지금 대상을 고쳤을 수 있다
            {
                _session.Report(string.Empty);
                RestorePreview();
                return;
            }

            Show(task.Result);
            SourceLinkStore.AddRecent(path);
            opened?.Invoke();
        }

        public void BrowseSpine()
        {
            string start = SourceLinkStore.Recent.Count > 0 ? Path.GetDirectoryName(SourceLinkStore.Recent[0]) : string.Empty;
            string path = EditorUtility.OpenFilePanel(Loc.T("Spine 프로젝트(.spine) 열기"), start, "spine");
            if (string.IsNullOrEmpty(path) == false)
            {
                OpenPath(path);
            }
        }

        /// <summary>창이 다시 열릴 때(도메인 리로드 포함) 마지막 대상을 다시 연다.</summary>
        public void RestoreLast()
        {
            string last = SessionState.GetString(LastOpenedKey, string.Empty);
            if (last.Length == 0 || _session.Source != null)
            {
                return; // 기억한 것이 없거나 그사이 이미 무언가를 열었다
            }

            if (last.StartsWith("guid:"))
            {
                Object asset = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(last.Substring(5)));
                if (asset != null)
                {
                    OpenAsset(asset);
                }
            }
            else if (File.Exists(last))
            {
                OpenPath(last, RestoreWorkingState); // .spine 은 다 읽은 뒤에 되살린다
                return;
            }

            RestoreWorkingState();
        }

        /// <summary>
        /// 적용하지 않은 작업본을 SessionState 에 남긴다(스크립트 다시 컴파일·도메인 리로드로 창이 다시 만들어져도 잃지 않게).
        /// 창이 꺼질 때 부른다. Unity 를 끄면 SessionState 도 사라진다.
        /// </summary>
        public void SaveWorkingState()
        {
            EventSource source = _session.Source;
            if (source == null || source.IsDirty == false)
            {
                SessionState.EraseString(WorkingKey);
                return;
            }

            var state = new Newtonsoft.Json.Linq.JObject
            {
                ["path"] = source.FilePath,
                ["hash"] = Hash(source.Bytes),
                ["working"] = Newtonsoft.Json.Linq.JToken.FromObject(source.Working),
            };
            SessionState.SetString(WorkingKey, state.ToString(Newtonsoft.Json.Formatting.None));
        }

        void RestoreWorkingState()
        {
            string text = SessionState.GetString(WorkingKey, string.Empty);
            SessionState.EraseString(WorkingKey);
            EventSource source = _session.Source;
            if (text.Length == 0 || source == null)
            {
                return;
            }

            try
            {
                var state = Newtonsoft.Json.Linq.JObject.Parse(text);
                if ((string)state["path"] != source.FilePath || (string)state["hash"] != Hash(source.Bytes))
                {
                    return; // 그사이 파일이 바뀌었으면 버린다
                }

                EventSet saved = state["working"].ToObject<EventSet>();
                source.Edit(set =>
                {
                    set.Definitions = saved.Definitions;
                    set.Keys = saved.Keys;
                });
                _session.Report(Loc.T("적용하지 않은 변경을 이어서 엽니다(스크립트 다시 컴파일 전 상태)"), StatusKind.Warning);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Loc.T("[Spine Event Injector] 이전 작업본을 되살리지 못했습니다: ") + e.Message);
            }
        }

        static string Hash(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(bytes));
            }
        }

        bool Open(Func<EventSource> load)
        {
            if (ConfirmLeave() == false)
            {
                return false;
            }

            EventSource source;
            try
            {
                source = load();
            }
            catch (Exception e)
            {
                _session.Report(e.Message, StatusKind.Error);
                return false;
            }

            Show(source);
            return true;
        }

        /// <summary>새 파일로 바꾸는 것을 취소해 지금 대상이 그대로면, 열기 전에 내린 미리보기를 다시 띄운다.</summary>
        void RestorePreview()
        {
            if (_session.Source != null)
            {
                PreparePreview(_session.Source);
            }
            else
            {
                PreviewChanged?.Invoke(null);
            }
        }

        /// <summary>읽은 대상을 창에 띄운다.</summary>
        void Show(EventSource source)
        {
            // 백그라운드로 여는 중이던 것은 버린다(그사이 다른 것을 열었다)
            if (_opening != null)
            {
                EditorApplication.update -= PollOpen;
                _opening = null;
                _openingPath = null;
                _opened = null;
            }

            RestoreSkeleton(source);
            _session.Open(source);
            Remember(source);
            _unityEvents = MecanimEventImporter.Scan(source.Preview);
            PreparePreview(source);
            _session.Report(Loc.T("{0}: 정의 {1}개 · 키 {2}개를 읽었습니다", source.DisplayName, source.Working.Definitions.Count, source.Working.Keys.Count));
        }

        /// <summary>고칠 스켈레톤을 바꾼다(적용하지 않은 변경이 있으면 먼저 묻는다).</summary>
        public void SelectSkeleton(string name)
        {
            EventSource source = _session.Source;
            if (source == null || name == source.Skeleton || ConfirmLeave() == false)
            {
                _session.NotifySourceInfoChanged();
                return;
            }

            source.SelectSkeleton(name);
            SessionState.SetString(SkeletonKey, source.FilePath + "\n" + name);
            _session.Open(source);
            _unityEvents = MecanimEventImporter.Scan(source.Preview);
            PreparePreview(source);
            _session.Report(Loc.T("{0}: 정의 {1}개 · 키 {2}개를 읽었습니다", name, source.Working.Definitions.Count, source.Working.Keys.Count));
        }

        /// <summary>같은 파일을 다시 열 때(도메인 리로드 포함) 고치던 스켈레톤으로 돌아간다.</summary>
        static void RestoreSkeleton(EventSource source)
        {
            string[] saved = SessionState.GetString(SkeletonKey, string.Empty).Split('\n');
            if (saved.Length == 2 && saved[0] == source.FilePath && source.Skeleton != saved[1] && source.Skeletons.Contains(saved[1]))
            {
                source.SelectSkeleton(saved[1]);
            }
        }

        static void Remember(EventSource source)
        {
            string key = source.AssetPath != null
                ? "guid:" + AssetDatabase.AssetPathToGUID(source.Preview != null ? AssetDatabase.GetAssetPath(source.Preview) : source.AssetPath)
                : source.FilePath;
            SessionState.SetString(LastOpenedKey, key);
        }

        // ------------------------------------------------------------ 미리보기

        void PreparePreview(EventSource source)
        {
            if (source.Preview != null)
            {
                _preview.SetAsset(source.Preview);
                FillDurations(source);
                PreviewChanged?.Invoke(_preview.HasData ? null : Loc.T("미리보기를 만들지 못했습니다(아틀라스·텍스처를 확인하세요)"));
                return;
            }

            _preview.SetAsset(null);
            if (source.IsSpineProject == false)
            {
                PreviewChanged?.Invoke(Loc.T("이 데이터를 쓰는 SkeletonDataAsset 이 없어 미리보기가 없습니다. 이벤트 편집은 됩니다"));
                return;
            }

            _exportFor = source;
            _exporting = ExternalSpinePreview.ExportAsync(source.FilePath, source.Version, source.Skeleton,
                (source.Codec as SpineProjectCodec)?.ReadProject); // 열 때 읽은 그래프를 그대로 쓴다
            PreviewChanged?.Invoke(null);
            EditorApplication.update -= PollExport;
            EditorApplication.update += PollExport;
        }

        /// <summary>지금 열린 대상의 미리보기를 내보내는 중인가.</summary>
        public bool PreviewBusy => OpenBusy || (_exporting != null && _exportFor == _session.Source);

        void PollExport()
        {
            if (_exporting == null || _exporting.IsCompleted == false)
            {
                return;
            }

            EditorApplication.update -= PollExport;
            Task<ExternalSpinePreview.Export> task = _exporting;
            EventSource source = _exportFor;
            _exporting = null;
            if (source != _session.Source)
            {
                return; // 그사이 다른 대상을 열었다
            }

            ExternalSpinePreview.Export export = task.IsFaulted ? new ExternalSpinePreview.Export { Error = task.Exception?.GetBaseException().Message } : task.Result;
            if (export.Error != null)
            {
                PreviewChanged?.Invoke(export.Error);
                return;
            }

            foreach (KeyValuePair<string, float> d in export.Durations)
            {
                source.Durations[d.Key] = d.Value;
            }

            source.SetProjectFps(export.Fps);
            source.Preview = ExternalSpinePreview.CreateAsset(export, out string message);
            _preview.SetAsset(source.Preview);
            FillDurations(source);
            _session.NotifySourceInfoChanged();
            PreviewChanged?.Invoke(_preview.HasData ? null : message ?? Loc.T("미리보기를 만들지 못했습니다"));
            if (_preview.HasData && message != null)
            {
                _session.Report(message, StatusKind.Warning);
            }
        }

        void FillDurations(EventSource source)
        {
            if (_preview.HasData == false)
            {
                return;
            }

            foreach (string anim in source.Working.Animations)
            {
                float d = _preview.Duration(anim);
                if (d > 0f)
                {
                    source.Durations[anim] = d;
                }
            }
        }

        // ------------------------------------------------------------ 적용

        /// <summary>버튼·단축키로 적용한다. .spine 이면 창 안 확인 창으로 먼저 묻는다.</summary>
        public void ApplyFromWindow()
        {
            EventSource source = _session.Source;
            if (source == null || source.IsDirty == false || source.IsSpineProject == false || SpineWriteConfirm == null ||
                source.Working.Keys.Any(k => k.Name.Length == 0))
            {
                Apply(); // 바뀐 것 없음·빈 키 안내는 여기서 한다
                return;
            }

            SpineWriteConfirm(source.FilePath, confirmed =>
            {
                if (_session.Source != source)
                {
                    return;
                }

                if (confirmed)
                {
                    Apply(true);
                }
                else
                {
                    _session.Report(Loc.T("취소했습니다"));
                }
            });
        }

        public bool Apply() => Apply(false);

        bool Apply(bool confirmed)
        {
            EventSource source = _session.Source;
            if (source == null || source.IsDirty == false)
            {
                return true;
            }

            List<EventKey> empty = source.Working.Keys.Where(k => k.Name.Length == 0).ToList();
            if (empty.Count > 0)
            {
                _session.Select(empty.Take(1));
                _session.Report(Loc.T("이벤트를 고르지 않은 빈 키가 {0}개 있습니다. 오른쪽 속성에서 이벤트를 고른 뒤 적용하세요", empty.Count), StatusKind.Warning);
                return false;
            }

            string summary = EventFileWriter.Describe(EventSetDiff.Compute(source.Original, source.Working));
            var log = new List<string>();
            try
            {
                if (EventFileWriter.Apply(source, log, confirmed) == false)
                {
                    _session.Report(Loc.T("취소했습니다"));
                    return false;
                }
            }
            catch (Exception e)
            {
                _session.Report(Loc.T("적용하지 못했습니다: ") + e.Message, StatusKind.Error);
                Debug.LogException(e);
                return false;
            }

            _session.RefreshOrigin();
            _session.Report(Loc.T("{0}에 적용했습니다({1})", source.DisplayName, summary), StatusKind.Success);
            return true;
        }

        public void Revert()
        {
            if (_session.Source?.IsDirty == true)
            {
                _session.Source.Revert();
                _session.Report(Loc.T("적용하지 않은 변경을 버렸습니다(⌘/Ctrl+Z 로 되살릴 수 있습니다)"));
            }
        }

        /// <summary>열어 둔 에셋이 다시 임포트됐다(재익스포트·되살리기 등). 바뀐 것이 없으면 조용히 다시 읽는다.</summary>
        public void OnImported(string assetPath)
        {
            EventSource source = _session.Source;
            if (source?.AssetPath != assetPath)
            {
                return;
            }

            byte[] now = File.ReadAllBytes(source.FilePath);
            if (now.AsSpan().SequenceEqual(source.Bytes))
            {
                return;
            }

            if (source.IsDirty)
            {
                _session.Report(Loc.T("파일이 바깥에서 바뀌었습니다. 지금 변경을 적용하려면 먼저 다시 열어야 합니다"), StatusKind.Warning);
                return;
            }

            source.Reload(now);
            _session.RefreshOrigin();
            _session.Report(Loc.T("{0}가 다시 임포트되어 새로 읽었습니다", source.DisplayName));
        }

        // ------------------------------------------------------------ 원본(.spine)

        public void LinkOrigin()
        {
            if (_session.Source == null || _session.Source.IsSpineProject || _session.Source.AssetPath == null)
            {
                _session.Report(Loc.T("원본 연결은 프로젝트 안의 데이터 파일(.skel.bytes·.json)을 열었을 때만 쓸 수 있습니다"), StatusKind.Warning);
                return;
            }

            string path = EditorUtility.OpenFilePanel(Loc.T("이 데이터의 Spine 원본(.spine) 고르기"), string.Empty, "spine");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            _session.LinkOrigin(path);
            SourceLinkStore.AddRecent(path);
            ReportOrigin();
        }

        public void UnlinkOrigin()
        {
            _session.LinkOrigin(null);
            _session.Report(Loc.T("원본 연결을 해제했습니다"));
        }

        public void RefreshOrigin()
        {
            _session.RefreshOrigin();
            ReportOrigin();
        }

        void ReportOrigin()
        {
            OriginSync sync = _session.Origin;
            if (sync == null)
            {
                return;
            }

            if (sync.Error != null)
            {
                _session.Report(Loc.T("원본을 읽지 못했습니다: ") + sync.Error, StatusKind.Error);
            }
            else
            {
                _session.Report(sync.MissingCount == 0 ? Loc.T("원본과 이벤트가 같습니다") : Loc.T("원본에 없는 이벤트: 키 {0}개, 정의 {1}개", sync.MissingKeys.Count, sync.MissingDefinitions.Count),
                    sync.MissingCount == 0 ? StatusKind.Success : StatusKind.Info);
            }
        }

        public void PushOrigin()
        {
            OriginSync sync = _session.Origin;
            if (sync == null)
            {
                return;
            }

            if (_session.Source.IsDirty)
            {
                _session.Report(Loc.T("먼저 «적용하기»로 데이터 파일에 쓴 뒤 원본에 반영하세요(원본에는 파일에 있는 이벤트를 옮깁니다)"), StatusKind.Warning);
                return;
            }

            if (SpineWriteConfirm == null)
            {
                PushNow(sync, false);
                return;
            }

            try
            {
                sync.Validate();
            }
            catch (Exception e)
            {
                _session.Report(Loc.T("원본에 반영하지 못했습니다: ") + e.Message, StatusKind.Error);
                return;
            }

            SpineWriteConfirm(sync.SpinePath, confirmed =>
            {
                if (confirmed)
                {
                    PushNow(sync, true);
                }
                else
                {
                    _session.Report(Loc.T("취소했습니다"));
                }
            });
        }

        void PushNow(OriginSync sync, bool confirmed)
        {
            try
            {
                List<string> log = sync.Push(confirmed);
                if (log == null)
                {
                    _session.Report(Loc.T("취소했습니다"));
                    return;
                }

                _session.RefreshOrigin();
                _session.Report(Loc.T("원본에 반영했습니다: {0} (백업: 같은 폴더의 .bak)", Path.GetFileName(sync.SpinePath)), StatusKind.Success);
            }
            catch (Exception e)
            {
                _session.Report(Loc.T("원본에 반영하지 못했습니다: ") + e.Message, StatusKind.Error);
                Debug.LogException(e);
            }
        }

        // ------------------------------------------------------------ Unity 이벤트

        /// <summary>쓰는 키가 있으면 경고 창으로 묻고, 없으면 바로 지운다.</summary>
        public void DeleteDefinitions(List<EventDefinition> defs)
        {
            EventSet set = _session.Source?.Working;
            if (set == null || defs.Count == 0)
            {
                return;
            }

            List<EventDefinition> used = defs.Where(d => set.Keys.Any(k => k.Name == d.Name)).ToList();
            if (used.Count > 0)
            {
                var names = new HashSet<string>(used.Select(d => d.Name));
                int keys = set.Keys.Count(k => names.Contains(k.Name));
                const int MaxListed = 10;
                string list = string.Join("\n", used.Take(MaxListed).Select(d => "· " + d.Name)) +
                              (used.Count > MaxListed ? "\n" + Loc.T("외 {0}개", used.Count - MaxListed) : string.Empty);
                if (EditorUtility.DisplayDialog(Loc.T("사용 중인 이벤트 정의"),
                        Loc.T("지우려는 정의 중 {0}개를 키 {1}개가 쓰고 있습니다. 지우면 그 키도 함께 사라집니다.\n\n{2}", used.Count, keys, list),
                        Loc.T("키까지 삭제"), Loc.T("취소")) == false)
                {
                    return;
                }
            }

            _session.DeleteDefinitions(defs);
        }

        public void ImportUnityEvents()
        {
            EventSource source = _session.Source;
            List<MecanimEventImporter.Found> found = source == null ? new List<MecanimEventImporter.Found>() : MecanimEventImporter.Pending(_unityEvents, source.Working);
            if (found.Count == 0)
            {
                _session.Report(Loc.T("가져올 Unity 이벤트가 없습니다"));
                return;
            }

            (int added, List<string> skipped) = MecanimEventImporter.Import(source, found);
            foreach (string s in skipped)
            {
                Debug.Log("[Spine Event Injector] " + s);
            }

            _session.Report(Loc.T("Unity 이벤트 {0}개를 작업본에 옮겼습니다", added) + (skipped.Count > 0 ? Loc.T("(건너뜀 {0}개 — 콘솔 참고)", skipped.Count) : "") +
                            Loc.T(". 확인한 뒤 «적용하기»를 누르세요"), StatusKind.Success);
        }
    }
}
